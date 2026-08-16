using JacRed.Models.AppConf;
using System.Net;

namespace JacRed.Engine
{
    public class JacBaseController : BaseController
    {
        public static RedConf red => ModInit.conf.Red;

        public static JacConf jackett => ModInit.conf.Jackett;


        async public static Task<bool> Joinparse(ConcurrentBag<TorrentDetails> torrents, Func<ValueTask<List<TorrentDetails>>> parse)
        {
            // Страховка на весь веер трекеров: JackettApi ждёт их через Task.WhenAll, и любое
            // исключение одного парсера роняло бы весь поиск в HTTP 500 (и в пустую выдачу
            // у клиента). Один сломанный трекер должен стоить только своих раздач.
            try
            {
                var result = await parse();

                if (result != null && result.Count > 0)
                {
                    foreach (TorrentDetails torrent in result)
                        torrents.Add(torrent);

                    return true;
                }
            }
            catch (System.Exception ex)
            {
                Console.WriteLine($"JacRed: parse crash - {ex.GetType().Name}: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Гард открытого редиректа в parseMagnet у rutor и torrent.by: параметр <c>magnet</c>
        /// приходит из query и уезжает прямо в <c>Location:</c>. Ссылку формируем мы сами и
        /// клиент возвращает её обратно, но ручка <c>[AllowAnonymous]</c> — подставить туда можно
        /// что угодно, и это работало бы как редиректор с нашего домена, а заодно проносило бы
        /// произвольный магнет мимо санитайза в /qdl/add.
        ///
        /// Пропускаем только настоящий магнет с btih.
        /// </summary>
        public static bool IsSafeMagnet(string magnet)
            => !string.IsNullOrEmpty(magnet)
               && magnet.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
               && Regex.IsMatch(magnet, "xt=urn:btih:([0-9a-fA-F]{40}|[0-9a-zA-Z]{32})", RegexOptions.IgnoreCase);

        // reason нужен потому, что раньше сюда сходились 8 разных причин (таймаут, 403,
        // Cloudflare, DNS, протухшая кука, пустая выдача...) под одной строкой — по логу было
        // невозможно понять, что именно сломалось.
        public static void consoleErrorLog(string plugin, string reason = null)
        {
            Console.WriteLine($"JacRed: InternalServerError - {plugin}" + (reason != null ? $" ({reason})" : ""));
        }

        /// <summary>
        /// Запрос с фолбэком на прямой IP: прокси не ответил (или отдал не тот сайт) — ровно один
        /// прямой ретрай. Вся политика и состояние — в <see cref="ProxyFallback"/>; обёртка живёт
        /// здесь, чтобы формат лога (consoleErrorLog) остался в ОДНОМ месте, а ProxyFallback не
        /// тянул за собой ни ModInit, ни BaseController и линковался в тесты одной строкой.
        ///
        /// Выключатели: Jackett.proxyFallbackDirect (глобально) + TrackerSettings.proxyFallbackDirect
        /// (персонально, null = наследовать) — у трекера, забанившего наш собственный IP, должно
        /// быть право отказаться от прямого пути.
        /// </summary>
        public static Task<T> HttpOrDirect<T>(string plugin, TrackerSettings tracker, ProxyManager proxyManager,
                                              Func<T, bool> ok, Func<WebProxy, Task<T>> send) where T : class
            => ProxyFallback.Run(plugin,
                                 NodeOrProxy(proxyManager),
                                 send, ok,
                                 tracker?.proxyFallbackDirect ?? jackett.proxyFallbackDirect,
                                 jackett.proxyFallbackCooldownSeconds,
                                 consoleErrorLog);

        /// <summary>
        /// Единственная точка, где узлы-помощники вклиниваются в поход на трекер: есть живой
        /// узел — идём его адресом, нет — всё как было (штатный proxyManager, а при useproxy:false
        /// и он вернёт null, то есть прямой путь).
        ///
        /// Почему именно здесь, а не через globalproxy/globalnameproxy в init.conf: те ручки
        /// работают только при useproxy:true, который стоит ровно у одного трекера, — остальные
        /// пришлось бы включать руками. Здесь узлы работают МИМО всех конфиг-флагов, и на сервере
        /// действительно не нужно настраивать ничего.
        ///
        /// ⚠️ Тонкость к инварианту 2 ProxyFallback («прокси нет → ветка ретрая недостижима»).
        /// С узлом ветка ретрая становится достижимой и для трекеров с useproxy:false. Смысл
        /// инварианта при этом цел: он запрещал ВТОРОЙ ПОБАЙТОВО ТОТ ЖЕ запрос, а теперь первый
        /// заход идёт чужим адресом, второй — своим. Это и есть нужное поведение: узел отвалился
        /// (или отдал не ту страницу) — дом молча доделывает работу сам.
        ///
        /// ⚠️ Единственное место, где смерть узла ощутима: трекер с proxyFallbackDirect:false
        /// (сейчас torrent.by — у него забанен наш домашний IP, и прямой путь запрещён осознанно).
        /// Там прямого ретрая нет, и до истечения TTL реестра (90с ≈ 3 пропущенных удара) его
        /// раздачи будут теряться. Остальные 13 трекеров переживают смерть узла без следа.
        /// </summary>
        static Func<WebProxy> NodeOrProxy(ProxyManager proxyManager)
        {
            var nodes = jackett.nodes;

            if (nodes != null && nodes.enable)
            {
                var viaNode = NodeRegistry.ProxyOrNull(nodes.ttlSeconds);
                if (viaNode != null)
                    return viaNode;
            }

            return proxyManager != null ? proxyManager.Get : null;
        }
    }
}
