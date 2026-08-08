using JacRed.Models.AppConf;

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

        // reason нужен потому, что раньше сюда сходились 8 разных причин (таймаут, 403,
        // Cloudflare, DNS, протухшая кука, пустая выдача...) под одной строкой — по логу было
        // невозможно понять, что именно сломалось.
        public static void consoleErrorLog(string plugin, string reason = null)
        {
            Console.WriteLine($"JacRed: InternalServerError - {plugin}" + (reason != null ? $" ({reason})" : ""));
        }
    }
}
