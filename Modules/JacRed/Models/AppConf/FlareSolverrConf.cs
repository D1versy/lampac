namespace JacRed.Models.AppConf
{
    /// <summary>
    /// FlareSolverr — внешний сервис с реальным браузером, проходящий Cloudflare.
    /// Один на все трекеры (сервис один), включение — по трекеру через TrackerSettings.useflaresolverr.
    /// </summary>
    public class FlareSolverrConf
    {
        public bool enable { get; set; } = false;
        public string url { get; set; } = "http://flaresolverr:8191";

        /// <summary>HTTP-таймаут НАШЕГО запроса к солверу (должен быть больше maxTimeoutMs).</summary>
        public int timeoutSeconds { get; set; } = 180;

        /// <summary>maxTimeout, который солвер даёт браузеру на решение челленджа.</summary>
        public int maxTimeoutMs { get; set; } = 120000;

        /// <summary>Пересоздание сессии: браузер течёт, и кука логина не вечна.</summary>
        public int sessionTtlMinutes { get; set; } = 120;

        /// <summary>Солвер упал — не долбим его каждым поиском.</summary>
        public int cooldownSeconds { get; set; } = 300;

        /// <summary>TTL кеша решённого HTML поиска.</summary>
        public int htmlCacheMinutes { get; set; } = 60;

        /// <summary>
        /// Сколько ПОЛЬЗОВАТЕЛЬСКИЙ поиск готов ждать солвер. 0 = не ждать вовсе:
        /// раздачи rutracker подтянутся со следующего поиска, из кеша. Один solve — это
        /// Chrome и десятки секунд, а обычный поиск отвечает за 2 секунды.
        /// </summary>
        public int userWaitSeconds { get; set; } = 0;

        public string sessionName { get; set; } = "jacred";
    }
}
