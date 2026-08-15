using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace JacRed.Engine
{
    /// <summary>
    /// Реестр узлов-помощников: выходы, о которых узлы объявили САМИ.
    ///
    /// Зачем: скрапинг трекеров с домашнего IP жжёт его репутацию (у torrent.by наш адрес под
    /// баном постоянно). Узел-помощник даёт другой адрес, но настраивать его руками в init.conf
    /// на каждую машину — то, чего владелец просил избежать: «нода сама стучится, а на сервере
    /// ничего не нужно настраивать».
    ///
    /// Модель: узел раз в 30 секунд шлёт POST /qdl/nodes/hello. Пока удары идут — дом им
    /// пользуется. Удары прекратились — узел выпадает по TTL, и дом ходит как раньше.
    ///
    /// ⚠️ ГЛАВНЫЙ ИНВАРИАНТ: пустой реестр обязан давать РОВНО сегодняшнее поведение.
    ///    Поэтому <see cref="ProxyOrNull"/> возвращает null, а не «какой-нибудь» прокси —
    ///    вызывающий код тогда падает на штатный proxyManager.Get, как будто узлов нет вовсе.
    ///
    /// Пять решений, каждое со своей причиной:
    ///  1. **Адрес узла приходит в теле, а адрес соединения — лишь запасной вариант.**
    ///     Изначально было наоборот («узел не может ошибиться в своём IP»), и это оказалось
    ///     неверно: Docker Desktop подменяет адрес источника на шлюз своей сети у ВСЕХ запросов
    ///     на проброшенный порт. В бою узел с другой машины зарегистрировался как 172.24.0.1 —
    ///     адрес, по которому никакого прокси нет, и поиск молча ходил через пустоту.
    ///     Защита от вранья теперь не в источнике адреса, а в проверке <see cref="Reachable"/>.
    ///  2. **Живость НЕ проверяем повторяющейся пробой.** Разовая проверка достижимости на
    ///     приёме — да (см. п.1), но дальше отказ ловит ok()-предикат трекера + ProxyFallback.
    ///     Вторая, более тупая система периодических проверок врала бы — ровно этим кончились
    ///     активные хелс-чеки (боевой лог §BR).
    ///  3. **Состояние в статике.** ModInit.conf пересоздаётся целиком на каждый перечит
    ///     init.conf; список живых узлов там бы обнулялся раз в секунду.
    ///  4. **TTL проверяем лениво, на чтении.** Фоновый таймер добавил бы жизненный цикл и
    ///     ничего не дал: реестр читается на каждом запросе к трекеру.
    ///  5. **Файл не обращается ни к ModInit, ни к BaseController** (как ProxyFallback.cs) —
    ///     настройки приходят параметрами, поэтому реестр линкуется в тесты одной строкой.
    /// </summary>
    public static class NodeRegistry
    {
        sealed class Node
        {
            public string name;
            public string ip;
            public int egressPort;
            public int solverPort;
            public DateTime seenUtc;
        }

        static readonly ConcurrentDictionary<string, Node> _nodes = new ConcurrentDictionary<string, Node>();

        /// <summary>Часы делегатом — чтобы TTL проверялся тестом, а не ожиданием в 90 секунд.</summary>
        public static Func<DateTime> Now = () => DateTime.UtcNow;

        static bool Alive(Node n, int ttlSeconds)
            => n != null && (Now() - n.seenUtc).TotalSeconds <= Math.Max(30, ttlSeconds);

        #region приём объявления
        /// <summary>
        /// Узел объявился. Возвращает false, если объявление отвергнуто (чужой адрес, мусор в
        /// имени, переполнение реестра) — вызывающий отдаст 400, но НИКОГДА не исключение.
        /// </summary>
        /// <param name="ip">адрес, по которому дом будет ходить (уже провалидирован вызывающим)</param>
        public static bool Hello(string name, string ip, int egressPort, int solverPort, int maxNodes)
        {
            // Имя — ключ словаря. Без санитайза любой мусор в теле раздул бы реестр без границ.
            if (string.IsNullOrWhiteSpace(name) || name.Length > 32)
                return false;

            foreach (char c in name)
            {
                if (!char.IsLetterOrDigit(c) && c != '-' && c != '_' && c != '.')
                    return false;
            }

            if (string.IsNullOrEmpty(ip))
                return false;

            // Порт 0 = роль на узле не поднята; хотя бы одна роль должна быть.
            if (!ValidPort(egressPort) && !ValidPort(solverPort))
                return false;

            var node = new Node
            {
                name = name,
                ip = ip,
                egressPort = ValidPort(egressPort) ? egressPort : 0,
                solverPort = ValidPort(solverPort) ? solverPort : 0,
                seenUtc = Now()
            };

            // Снятый с эксплуатации узел иначе занимал бы место в капе вечно. Час — намеренно
            // много: до этого он висит в /qdl/nodes с alive:false, и по диагностике видно
            // «узел был, умер 40 минут назад», а не «узла никогда не existовало».
            Forget(ForgetAfterSeconds);

            // Уже известный узел обновляется всегда; новый — только если есть место.
            // Кап нужен не от «своих» узлов, а от зациклившегося агента с новым именем на удар.
            if (!_nodes.ContainsKey(name) && _nodes.Count >= Math.Max(1, maxNodes))
                return false;

            _nodes[name] = node;
            return true;
        }

        static bool ValidPort(int p) => p > 0 && p <= 65535;

        /// <summary>Через сколько секунд молчания забыть узел совсем (не просто «не живой»).</summary>
        public const int ForgetAfterSeconds = 3600;

        /// <summary>Выкинуть узлы, молчащие дольше указанного. Вызывается на приёме объявления —
        /// отдельный таймер ради этого заводить незачем.</summary>
        static void Forget(int olderThanSeconds)
        {
            var now = Now();

            foreach (var kv in _nodes)
            {
                if ((now - kv.Value.seenUtc).TotalSeconds > olderThanSeconds)
                    _nodes.TryRemove(kv.Key, out _);
            }
        }

        /// <summary>
        /// Адрес из приватной подсети? Узлы живут в LAN (через туннель), и объявление из
        /// интернета принимать нельзя ни при каких условиях.
        /// IPv4-mapped (::ffff:192.168.0.5) разворачиваем — Kestrel часто отдаёт адрес так.
        /// </summary>
        public static bool IsPrivate(IPAddress addr)
        {
            if (addr == null)
                return false;

            if (addr.IsIPv4MappedToIPv6)
                addr = addr.MapToIPv4();

            if (IPAddress.IsLoopback(addr))
                return true;

            byte[] b = addr.GetAddressBytes();

            if (b.Length == 4)
            {
                if (b[0] == 10) return true;                              // 10.0.0.0/8
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true; // 172.16.0.0/12
                if (b[0] == 192 && b[1] == 168) return true;              // 192.168.0.0/16
                return false;
            }

            // IPv6 unique local fc00::/7 — на случай туннеля с ULA-адресацией
            return b.Length == 16 && (b[0] & 0xFE) == 0xFC;
        }
        #endregion

        #region выдача
        /// <summary>
        /// Делегат выбора живого выхода — или **null**, если предложить нечего.
        /// Null здесь принципиален: вызывающий обязан упасть на штатный путь, а не получить
        /// «какой-нибудь» прокси.
        /// </summary>
        public static Func<WebProxy> ProxyOrNull(int ttlSeconds)
        {
            // Снимок берём СЕЙЧАС, а не внутри делегата: выбор должен быть предсказуемым и
            // не зависеть от того, когда именно вызывающий дёрнет Func.
            var live = _nodes.Values.Where(n => n.egressPort > 0 && Alive(n, ttlSeconds)).ToArray();
            if (live.Length == 0)
                return null;

            return () =>
            {
                try
                {
                    var n = live[Random.Shared.Next(live.Length)];
                    return new WebProxy($"http://{n.ip}:{n.egressPort}");
                }
                catch
                {
                    // Наружу не летит ни одно исключение: не смогли собрать прокси — идём прямо.
                    return null;
                }
            };
        }

        /// <summary>
        /// Убрать узел из реестра руками. Нужно, когда узел снят с эксплуатации или это
        /// след от теста: сам он исчезнет только через <see cref="ForgetAfterSeconds"/>,
        /// а до тех пор мозолит глаза в диагностике. Возвращает, сколько записей убрано.
        /// </summary>
        public static int Forget(string name)
            => !string.IsNullOrWhiteSpace(name) && _nodes.TryRemove(name, out _) ? 1 : 0;

        /// <summary>Убрать все НЕживые записи разом (уборка после тестов и переименований).</summary>
        public static int ForgetDead(int ttlSeconds)
        {
            int n = 0;

            foreach (var kv in _nodes)
            {
                if (!Alive(kv.Value, ttlSeconds) && _nodes.TryRemove(kv.Key, out _))
                    n++;
            }

            return n;
        }

        /// <summary>Живые солверы (для пула FlareSolverr, следующий шаг).</summary>
        public static string[] Solvers(int ttlSeconds)
            => _nodes.Values
                     .Where(n => n.solverPort > 0 && Alive(n, ttlSeconds))
                     .Select(n => $"http://{n.ip}:{n.solverPort}")
                     .ToArray();

        /// <summary>Срез для диагностики. maskIp=true — не светить адреса наружу.</summary>
        public static object[] Snapshot(int ttlSeconds, bool maskIp)
            => _nodes.Values
                     .OrderBy(n => n.name)
                     .Select(n => (object)new
                     {
                         name = n.name,
                         ip = maskIp ? Mask(n.ip) : n.ip,
                         egressPort = n.egressPort,
                         solverPort = n.solverPort,
                         ageSeconds = (int)(Now() - n.seenUtc).TotalSeconds,
                         alive = Alive(n, ttlSeconds)
                     })
                     .ToArray();

        static string Mask(string ip)
        {
            if (string.IsNullOrEmpty(ip))
                return null;

            int i = ip.LastIndexOf('.');
            return i > 0 ? ip.Substring(0, i) + ".x" : "x";
        }

        /// <summary>
        /// Разовая проверка «дом вообще может достучаться до этого выхода» — на приёме
        /// объявления, не в горячем пути. Это НЕ хелс-чек: живость ролей дальше показывает
        /// сам трафик (урок §BR — проба, не повторяющая боевой путь, врёт). Здесь проверяется
        /// именно адрес: узел мог сообщить тот, по которому его не видно.
        ///
        /// Запрос идёт ЧЕРЕЗ прокси на его служебную страницу — наружу не уходит ни байта.
        /// Подменяется делегатом: тесты не ходят в сеть.
        /// </summary>
        public static Func<string, int, Task<bool>> Reachable = async (ip, port) =>
        {
            try
            {
                var handler = new HttpClientHandler
                {
                    Proxy = new WebProxy($"http://{ip}:{port}"),
                    UseProxy = true
                };

                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
                using var resp = await http.GetAsync("http://tinyproxy.stats/");
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        };

        /// <summary>Только для тестов. Часы возвращаем к реальным, чтобы замороженное
        /// время не утекло в соседний тест.</summary>
        public static void Reset()
        {
            _nodes.Clear();
            Now = () => DateTime.UtcNow;
        }
        #endregion
    }
}
