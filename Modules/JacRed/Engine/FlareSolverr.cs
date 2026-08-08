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

        // Солвер — это один браузер: два параллельных solve его роняют.
        static readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        static readonly ConcurrentDictionary<string, Task<string>> _inflight = new();
        static readonly ConcurrentDictionary<string, (string html, DateTime until)> _cache = new();

        /// <summary>Подмена транспорта в тестах: (url, jsonBody, timeoutSec) → тело ответа.</summary>
        public static Func<string, string, int, Task<string>> Transport = RealTransport;

        static Task<string> RealTransport(string url, string json, int timeoutSec)
            // statusCodeOK:false обязателен — FlareSolverr кладёт осмысленный message в тело 500,
            // а при true Http вернул бы null и причина потерялась бы.
            => Http.Post(url, new System.Net.Http.StringContent(json, Encoding.UTF8, "application/json"),
                         timeoutSeconds: timeoutSec, statusCodeOK: false);

        public static bool Available(FlareSolverrConf c)
            => c != null && c.enable && !string.IsNullOrWhiteSpace(c.url) && DateTime.Now >= _downUntil;

        static void MarkDown(FlareSolverrConf c, string why)
        {
            _downUntil = DateTime.Now.AddSeconds(Math.Max(30, c?.cooldownSeconds ?? 300));
            _session = null;
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

        static async Task<string> EnsureSession(FlareSolverrConf c)
        {
            if (_session != null && (DateTime.Now - _sessionAt).TotalMinutes < Math.Max(5, c.sessionTtlMinutes))
                return _session;

            if (_session != null)
                await Cmd(c, new JObject { ["cmd"] = "sessions.destroy", ["session"] = _session });

            string name = (c.sessionName ?? "jacred") + "-" + DateTime.Now.Ticks;
            var r = await Cmd(c, new JObject { ["cmd"] = "sessions.create", ["session"] = name });
            if (r == null || r.Value<string>("status") != "ok") return null;

            _session = name;
            _sessionAt = DateTime.Now;
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
                    ["cmd"] = "request.get", ["session"] = s, ["url"] = url, ["maxTimeout"] = c.maxTimeoutMs
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
                    ["postData"] = sb.ToString(), ["maxTimeout"] = c.maxTimeoutMs
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

            if (_cache.TryGetValue(cacheKey, out var hit) && DateTime.Now < hit.until)
                return hit.html;

            // дедуп одинаковых запросов: пять клиентов на одном тайтле = один solve
            var task = _inflight.GetOrAdd(cacheKey, _ => Task.Run(async () =>
            {
                try
                {
                    var sol = await Get(c, url);
                    if (sol?.html != null)
                        _cache[cacheKey] = (sol.html, DateTime.Now.AddMinutes(Math.Max(1, c.htmlCacheMinutes)));
                    return sol?.html;
                }
                finally { _inflight.TryRemove(cacheKey, out Task<string> _); }
            }));

            if (budgetSeconds <= 0) return null;   // фон уже запущен, ждать не станем
            var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(budgetSeconds)));
            return done == task ? task.Result : null;
        }

        /// <summary>Снять сессию (при выгрузке модуля): иначе в солвере копятся Chrome'ы.</summary>
        public static async Task DropSession(FlareSolverrConf c)
        {
            try
            {
                if (_session == null || c == null || string.IsNullOrWhiteSpace(c.url)) return;
                await Cmd(c, new JObject { ["cmd"] = "sessions.destroy", ["session"] = _session });
            }
            catch { }
            finally { _session = null; }
        }
        #endregion
    }
}
