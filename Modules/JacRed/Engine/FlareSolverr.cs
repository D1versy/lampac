using JacRed.Models.AppConf;
using Newtonsoft.Json.Linq;
using Shared;
using Shared.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace JacRed.Engine
{
    /// <summary>
    /// Клиент FlareSolverr: прогоняет запрос через реальный браузер и отдаёт готовый HTML.
    ///
    /// Зачем: Cloudflare режет rutracker по TLS-отпечатку, и HttpClient туда не попадает
    /// НИКАК — ни с кукой из браузера, ни через прокси, ни через headless Chromium
    /// (проверено, Media-server claude/06 §AY). Единственный рабочий путь — гонять сами
    /// запросы через солвер, а не забирать у него куку.
    ///
    /// Три инварианта:
    ///  1. Наружу НЕ ЛЕТИТ ни одно исключение — любая проблема это null + кулдаун.
    ///     Сломанный солвер стоит только раздач своего трекера, остальные не страдают.
    ///  2. Солвер НЕ БЛОКИРУЕТ пользовательский поиск: solve занимает десятки секунд,
    ///     а поиск отвечает за две. Ждём не дольше выданного бюджета, дальше — фон и кеш.
    ///  3. Состояние живёт в статике: ModInit.conf пересоздаётся целиком на каждый
    ///     перечит init.conf, класть туда id сессии нельзя.
    ///
    /// Файл сознательно НЕ обращается к ModInit.conf и объявляет usings явно — так его можно
    /// слинковать в тестовый проект, не затаскивая всё дерево JacRed.
    /// </summary>
    public static class FlareSolverr
    {
        public sealed class Solution
        {
            public int status;
            public string html;
            public string userAgent;
        }

        static string _session;
        static DateTime _sessionAt;
        static DateTime _downUntil;

        // Сломались — следующий EnsureSession обязан снести браузер и поднять новый.
        // Раньше здесь было _session = null, и это была УТЕЧКА: имя терялось, а сессия на
        // солвере оставалась жить. За сутки набегало 153 sessions.create против 0 destroy,
        // 37 живых Chrome'ов и упор в mem_limit (боевой лог §BW).
        static bool _forceNew;

        static int _epoch;

        /// <summary>
        /// Поколение сессии: +1 на каждый новый браузер. Кука логина живёт ВНУТРИ браузера,
        /// поэтому потребителям (rutracker) надо знать, что их логин уехал вместе со старой
        /// сессией, — иначе они час считают себя залогиненными и молча отдают пустую выдачу.
        /// </summary>
        public static int SessionEpoch => Volatile.Read(ref _epoch);

        // Солвер — это один браузер: два параллельных solve его роняют.
        static readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        static readonly ConcurrentDictionary<string, Task<string>> _inflight = new();
        static readonly ConcurrentDictionary<string, (string html, DateTime until)> _cache = new();

        /// <summary>Подмена транспорта в тестах: (url, jsonBody, timeoutSec) → тело ответа.</summary>
        public static Func<string, string, int, Task<string>> Transport = RealTransport;

        /// <summary>Подмена часов в тестах: кулдаун и TTL сессии иначе не промотать.</summary>
        public static Func<DateTime> Now = () => DateTime.Now;

        /// <summary>
        /// Сброс состояния процесса — ТОЛЬКО для тестов. Сессию на солвере не трогает
        /// (для этого <see cref="DropSession"/>), поэтому в бою вызывать нельзя: забыли имя —
        /// получили орфана.
        /// </summary>
        public static void Reset()
        {
            _session = null;
            _sessionAt = default;
            _downUntil = default;
            _forceNew = false;
            _epoch = 0;
            _cache.Clear();
            _inflight.Clear();
        }

        static Task<string> RealTransport(string url, string json, int timeoutSec)
            // statusCodeOK:false обязателен — FlareSolverr кладёт осмысленный message в тело 500,
            // а при true Http вернул бы null и причина потерялась бы.
            => Http.Post(url, new System.Net.Http.StringContent(json, Encoding.UTF8, "application/json"),
                         timeoutSeconds: timeoutSec, statusCodeOK: false);

        public static bool Available(FlareSolverrConf c)
            => c != null && c.enable && !string.IsNullOrWhiteSpace(c.url) && Now() >= _downUntil;

        static void MarkDown(FlareSolverrConf c, string why)
        {
            _downUntil = Now().AddSeconds(Math.Max(30, c?.cooldownSeconds ?? 300));

            // ⚠️ Имя сессии НЕ забываем — только помечаем, что браузер надо пересоздать.
            // Забыть имя = потерять единственную возможность его снести.
            _forceNew = true;
            Console.WriteLine($"[FlareSolverr] недоступен ({why}) — пауза {Math.Max(30, c?.cooldownSeconds ?? 300)}с");
        }

        #region низкий уровень
        static async Task<JObject> Cmd(FlareSolverrConf c, JObject body)
        {
            string raw = await Transport(c.url.TrimEnd('/') + "/v1", body.ToString(Newtonsoft.Json.Formatting.None),
                                         Math.Max(30, c.timeoutSeconds));
            if (string.IsNullOrEmpty(raw)) return null;
            try { return JObject.Parse(raw); } catch { return null; }
        }

        static string SessionName(FlareSolverrConf c)
            => string.IsNullOrWhiteSpace(c?.sessionName) ? "jacred" : c.sessionName;

        /// <summary>
        /// Снести сессию на солвере. true = «на солвере её больше нет».
        ///
        /// ⚠️ destroy НЕсуществующей сессии образ отдаёт HTTP 500 с «The session doesn't exist»
        /// (flaresolverr_service.py:206-208). Это УСПЕХ — цель достигнута, браузера нет. Считать
        /// это отказом нельзя: уборка сама ставила бы себе кулдаун. Настоящий отказ — только
        /// r == null, то есть транспорт не дошёл и браузер мог остаться.
        /// </summary>
        static async Task<bool> DestroySession(FlareSolverrConf c, string name)
        {
            var r = await Cmd(c, new JObject { ["cmd"] = "sessions.destroy", ["session"] = name });
            if (r == null)
                return false;

            if (r.Value<string>("status") == "ok")
                return true;

            string msg = r.Value<string>("message") ?? "";
            return msg.Contains("exist", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Живая сессия солвера. Имя СТАБИЛЬНОЕ — и это главная защита от утечки: в образе
        /// sessions.create идемпотентен по имени (sessions.py:38-45 — «уже есть» возвращает
        /// существующую со статусом ok). Значит сколько бы мы ни падали, ни таймаутились и ни
        /// перезапускались, больше ОДНОГО браузера не заведётся никогда.
        ///
        /// ⚠️ Раньше имя было `jacred-{Ticks}`, уникальное на каждый заход: любая потеря имени
        /// делала браузер невосстановимым, и они копились до упора в mem_limit (§BW).
        /// </summary>
        static async Task<string> EnsureSession(FlareSolverrConf c)
        {
            string name = SessionName(c);

            if (!_forceNew && _session != null && (Now() - _sessionAt).TotalMinutes < Math.Max(5, c.sessionTtlMinutes))
                return _session;

            // Сюда попадаем в двух случаях: сломались (_forceNew) или истёк наш TTL. И там и там
            // прошлый браузер надо снести. Шлём destroy и когда _session == null: имя стабильное,
            // значит сессия от прошлого процесса lampac зовётся так же — иначе она осталась бы
            // висеть навсегда, ведь никто больше её имени не знает.
            if (_forceNew || _session != null)
            {
                // Не дошли — старый браузер, возможно, остался жив. Создать новый всё равно надо
                // (имя то же, create идемпотентен), но знать об этом полезно: регулярная строчка
                // здесь означает, что солвер не отвечает вообще, а не «страница не решилась».
                if (!await DestroySession(c, name))
                    Console.WriteLine($"[FlareSolverr] сессию «{name}» снести не удалось — солвер не ответил");
            }

            var r = await Cmd(c, new JObject { ["cmd"] = "sessions.create", ["session"] = name });
            if (r == null || r.Value<string>("status") != "ok") return null;

            _session = name;
            _sessionAt = Now();
            _forceNew = false;
            Interlocked.Increment(ref _epoch);
            return _session;
        }

        static Solution Read(FlareSolverrConf c, JObject r, string what)
        {
            if (r == null) { MarkDown(c, what + ": нет ответа"); return null; }
            if (r.Value<string>("status") != "ok")
            {
                // Не решённый челлендж — это не «солвер лёг», это конкретная страница.
                // Кулдаун ставим только на транспортные проблемы, иначе один упрямый URL
                // выключал бы солвер целиком.
                Console.WriteLine($"[FlareSolverr] {what}: {r.Value<string>("message")}");
                return null;
            }

            var sol = r["solution"] as JObject;
            string html = sol?.Value<string>("response");
            if (string.IsNullOrEmpty(html)) return null;
            if (html.Contains("Just a moment")) { Console.WriteLine($"[FlareSolverr] {what}: челлендж не пройден"); return null; }

            return new Solution { status = sol.Value<int?>("status") ?? 0, html = html, userAgent = sol.Value<string>("userAgent") };
        }
        #endregion

        #region публичный API
        public static async Task<Solution> Get(FlareSolverrConf c, string url)
        {
            if (!Available(c)) return null;
            await _gate.WaitAsync();
            try
            {
                string s = await EnsureSession(c);
                if (s == null) { MarkDown(c, "сессия"); return null; }
                return Read(c, await Cmd(c, new JObject
                {
                    ["cmd"] = "request.get", ["session"] = s, ["url"] = url, ["maxTimeout"] = c.maxTimeoutMs,

                    // Вторая линия обороны от утечки: образ умеет ротировать сессию сам
                    // (sessions.py:79-82 → create(force_new: true) → корректный destroy с
                    // driver.quit() и уборкой /tmp/FlareSolverr/<id>). Раньше поле не слали
                    // никогда, и sessionTtlMinutes работал только в НАШЕЙ памяти.
                    ["session_ttl_minutes"] = Math.Max(5, c.sessionTtlMinutes)
                }), "get " + url);
            }
            catch (Exception ex) { MarkDown(c, ex.GetType().Name + ": " + ex.Message); return null; }
            finally { _gate.Release(); }
        }

        public static async Task<Solution> PostForm(FlareSolverrConf c, string url, IDictionary<string, string> form)
        {
            if (!Available(c)) return null;
            await _gate.WaitAsync();
            try
            {
                string s = await EnsureSession(c);
                if (s == null) { MarkDown(c, "сессия"); return null; }

                var sb = new StringBuilder();
                foreach (var kv in form)
                {
                    if (sb.Length > 0) sb.Append('&');
                    sb.Append(System.Web.HttpUtility.UrlEncode(kv.Key)).Append('=').Append(System.Web.HttpUtility.UrlEncode(kv.Value));
                }

                return Read(c, await Cmd(c, new JObject
                {
                    ["cmd"] = "request.post", ["session"] = s, ["url"] = url,
                    ["postData"] = sb.ToString(), ["maxTimeout"] = c.maxTimeoutMs,
                    ["session_ttl_minutes"] = Math.Max(5, c.sessionTtlMinutes)
                }), "post " + url);
            }
            catch (Exception ex) { MarkDown(c, ex.GetType().Name + ": " + ex.Message); return null; }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// HTML с кешем и ограниченным ожиданием. Ядро всей схемы «солвер не блокирует поиск»:
        /// есть свежий кеш — отдаём мгновенно; нет — запускаем solve в фоне и ждём не дольше
        /// budgetSeconds. Не дождались → null, трекер просто не участвует в ЭТОЙ выдаче,
        /// а следующий поиск того же тайтла возьмёт готовый HTML из кеша.
        /// </summary>
        public static async Task<string> CachedHtml(FlareSolverrConf c, string cacheKey, string url, int budgetSeconds)
        {
            if (!Available(c)) return null;

            if (_cache.TryGetValue(cacheKey, out var hit) && Now() < hit.until)
                return hit.html;

            // дедуп одинаковых запросов: пять клиентов на одном тайтле = один solve
            var task = _inflight.GetOrAdd(cacheKey, _ => Task.Run(async () =>
            {
                try
                {
                    var sol = await Get(c, url);
                    if (sol?.html != null)
                        _cache[cacheKey] = (sol.html, Now().AddMinutes(Math.Max(1, c.htmlCacheMinutes)));
                    return sol?.html;
                }
                finally { _inflight.TryRemove(cacheKey, out Task<string> _); }
            }));

            if (budgetSeconds <= 0) return null;   // фон уже запущен, ждать не станем
            var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(budgetSeconds)));
            return done == task ? task.Result : null;
        }

        /// <summary>
        /// Снять сессию (перечит init.conf, выгрузка модуля): иначе в солвере копятся Chrome'ы.
        ///
        /// ⚠️ Не проверяет `_session != null`, как раньше: имя стабильное, и снести надо в том
        /// числе сессию, оставшуюся от прошлого процесса lampac. Именно эта проверка делала метод
        /// бесполезным ровно в том случае, ради которого он написан.
        /// </summary>
        public static async Task<bool> DropSession(FlareSolverrConf c)
        {
            try
            {
                if (c == null || string.IsNullOrWhiteSpace(c.url)) return false;
                return await DestroySession(c, SessionName(c));
            }
            catch { return false; }
            finally { _session = null; _forceNew = false; }
        }
        #endregion
    }
}
