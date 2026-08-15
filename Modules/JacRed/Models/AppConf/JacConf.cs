namespace JacRed.Models.AppConf
{
    public class JacConf
    {
        public int cacheToMinutes = 5;

        public string search_lang = "query";

        public int timeoutSeconds = 8;

        // FlareSolverr — один на все трекеры (сервис один). Включение по трекеру:
        // TrackerSettings.useflaresolverr, как это сделано у useproxy.
        public FlareSolverrConf flaresolverr = new FlareSolverrConf();

        /// <summary>
        /// Прокси не ответил (или отдал не тот сайт) — сходить тем же запросом напрямую.
        /// Ровно ОДИН ретрай. При useproxy:false ветка недостижима, поведение не меняется.
        /// Персональное отключение на трекере — TrackerSettings.proxyFallbackDirect.
        /// </summary>
        public bool proxyFallbackDirect = true;

        /// <summary>
        /// Сколько секунд помнить вердикт: удачный прямой путь → ходим сразу мимо прокси;
        /// оба пути мертвы → ретрай выключен (лежит трекер, а не прокси). Клампится снизу до 30.
        /// </summary>
        public int proxyFallbackCooldownSeconds = 300;

        /// <summary>
        /// Узлы-помощники: выходы, о которых вспомогательные машины объявляют сами.
        /// В init.conf прописывать ничего не нужно — см. NodesConf.
        /// </summary>
        public NodesConf nodes = new NodesConf();


        public TrackerSettings Rutor = new TrackerSettings("https://rutor.info"/*, priority: "torrent"*/);

        public TrackerSettings Megapeer = new TrackerSettings("https://megapeer.vip", enable: false);

        public TrackerSettings TorrentBy = new TrackerSettings("https://torrent.by"/*, priority: "torrent"*/);

        public TrackerSettings Kinozal = new TrackerSettings("https://kinozal.tv");

        public TrackerSettings NNMClub = new TrackerSettings("https://nnmclub.to");

        public TrackerSettings Bitru = new TrackerSettings("https://bitru.org");

        public TrackerSettings Toloka = new TrackerSettings("https://toloka.to");

        public TrackerSettings Rutracker = new TrackerSettings("https://rutracker.org"/*, priority: "torrent"*/);

        public TrackerSettings BigFanGroup = new TrackerSettings("https://bigfangroup.org");

        public TrackerSettings Selezen = new TrackerSettings("https://open.selezen.org"/*, priority: "torrent"*/);

        public TrackerSettings Lostfilm = new TrackerSettings("https://www.lostfilm.tv");

        public TrackerSettings Anilibria = new TrackerSettings("https://www.anilibria.tv");

        public TrackerSettings Animelayer = new TrackerSettings("http://animelayer.ru");

        public TrackerSettings Anifilm = new TrackerSettings("https://anifilm.pro");
    }
}
