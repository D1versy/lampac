using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading.Tasks;

namespace JacRed.Engine
{
    /// <summary>
    /// Прокси не ответил (или отдал не тот сайт) — сходить тем же запросом НАПРЯМУЮ.
    ///
    /// Зачем: ProxyManager уже умеет раунд-робин по proxy.list и ротацию залипшего адреса по
    /// ошибкам, но при useproxy:true запросы идут ТОЛЬКО через прокси — отказ всех адресов и
    /// трекер молча замолкает. Владельцу нужно «прокси основной путь, наш IP запасной».
    ///
    /// Шесть инвариантов (нарушение любого = регресс):
    ///  1. Ретрай РОВНО ОДИН, циклов нет. Веер трекеров ждётся через Task.WhenAll, и общее
    ///     время поиска = самый медленный парсер: удвоение на каждый мёртвый прокси недопустимо.
    ///  2. Прокси нет (useproxy:false) → ветка ретрая физически недостижима: второй запрос был
    ///     бы побайтовой копией первого. Это и есть гарантия нулевого изменения поведения на
    ///     текущем конфиге, где прокси не включены нигде.
    ///  3. Упали ОБА пути — значит лежит ТРЕКЕР, а не прокси. Прямой путь не запоминаем и
    ///     ретрай глушим до конца кулдауна, иначе каждый поиск платил бы двойным таймаутом за
    ///     лежащий сайт.
    ///  4. Удачный direct запоминаем: следующий поиск идёт сразу мимо прокси — один запрос, без
    ///     штрафа по времени. Кулдаун истёк — снова пробуем прокси (вдруг починили).
    ///  5. Кулдаун клампится снизу (30с): cooldownSeconds:0 превратил бы каждый поиск в двойной.
    ///  6. Исключения НЕ ловим — их уже изолирует Joinparse (один сломанный трекер стоит только
    ///     своих раздач). Второй слой глушил бы настоящие баги.
    ///
    /// Состояние живёт в статике по той же причине, что и у FlareSolverr: ModInit.conf
    /// пересоздаётся целиком на каждый перечит init.conf, класть «прокси сейчас мёртв» в
    /// TrackerSettings нельзя. Сброс — ProxyFallback.Reset() из updateConf().
    ///
    /// Файл сознательно НЕ обращается к ModInit.conf и объявляет usings явно — так его можно
    /// слинковать в тестовый проект, не затаскивая всё дерево JacRed.
    /// </summary>
    public static class ProxyFallback
    {
        enum Mode
        {
            /// <summary>Прокси мёртв, прямой путь работает — идём сразу напрямую.</summary>
            Direct,
            /// <summary>Мертвы оба — лежит трекер. Ретрай не делаем.</summary>
            NoRetry
        }

        static readonly ConcurrentDictionary<string, (Mode mode, DateTime until)> _state = new();

        /// <summary>Подмена часов в тестах: кулдаун иначе не промотать.</summary>
        public static Func<DateTime> Now = () => DateTime.Now;

        /// <summary>Сброс вердиктов: перечит init.conf и тесты.</summary>
        public static void Reset() => _state.Clear();

        static Mode? ModeOf(string plugin)
        {
            if (!_state.TryGetValue(plugin, out var s))
                return null;

            if (Now() < s.until)
                return s.mode;

            _state.TryRemove(plugin, out _);
            return null;
        }

        /// <param name="plugin">ключ трекера — тот же, что у ProxyManager ("rutor", "kinozal", ...)</param>
        /// <param name="getProxy">обычно proxyManager.Get (method group); null = прокси нет</param>
        /// <param name="send">весь запрос целиком. Тело POST обязано строиться ВНУТРИ лямбды:
        /// отправленный HttpContent повторно не переиспользуется</param>
        /// <param name="ok">«дошли ли мы до трекера», а НЕ «залогинены ли мы». Каптив-портал
        /// отдаёт 200 с чужой страницей — ретрай оправдан; разлогиненный трекер это и есть
        /// трекер, прямой запрос протухшую куку не вылечит и запрос сгорит зря</param>
        /// <param name="enabled">выключатель из init.conf (глобальный + персональный на трекере)</param>
        /// <param name="log">обычно consoleErrorLog: (plugin, reason)</param>
        public static async Task<T> Run<T>(
            string plugin,
            Func<WebProxy> getProxy,
            Func<WebProxy, Task<T>> send,
            Func<T, bool> ok,
            bool enabled,
            int cooldownSeconds,
            Action<string, string> log = null) where T : class
        {
            if (!enabled)
            {
                // выключили на лету — вердикт не должен залипнуть до конца кулдауна
                _state.TryRemove(plugin, out _);
                return await send(getProxy?.Invoke());
            }

            var mode = ModeOf(plugin);

            if (mode == Mode.Direct)
            {
                T direct = await send(null);
                if (!ok(direct))
                    _state.TryRemove(plugin, out _);   // и прямой сдох — в следующий раз снова через прокси
                return direct;
            }

            WebProxy proxy = getProxy?.Invoke();
            T res = await send(proxy);

            if (ok(res))
                return res;

            // ⚠️ Инвариант 2: без прокси второй запрос — копия первого.
            if (proxy == null || mode == Mode.NoRetry)
                return res;

            T retry = await send(null);
            bool good = ok(retry);

            int cd = Math.Max(30, cooldownSeconds);
            _state[plugin] = (good ? Mode.Direct : Mode.NoRetry, Now().AddSeconds(cd));

            log?.Invoke(plugin, good
                ? $"прокси не ответил, прямой путь ok — идём мимо прокси {cd}с"
                : $"прокси и прямой путь оба пусты — лежит трекер, ретрай выключен на {cd}с");

            return good ? retry : (res ?? retry);
        }
    }
}
