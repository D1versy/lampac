using Shared.Models.Base;

namespace JacRed.Models.AppConf
{
    public class TrackerSettings : Iproxy
    {
        public TrackerSettings(string host, bool enable = true, bool useproxy = false, LoginSettings login = null, string priority = null)
        {
            this.host = host;
            this.enable = enable;
            this.useproxy = useproxy;

            if (login != null)
                this.login = login;

            this.priority = priority;
        }


        public string host { get; set; }

        public bool enable { get; set; }

        public bool showdown { get; set; }

        public bool monitor_showdown { get; set; } = true;

        public string priority { get; set; }

        public LoginSettings login { get; set; } = new LoginSettings();

        public string cookie { get; set; }


        /// <summary>Гонять запросы этого трекера через FlareSolverr (обход Cloudflare).</summary>
        public bool useflaresolverr { get; set; }

        /// <summary>
        /// Персональный выключатель прямого фолбэка при отказе прокси.
        /// null (по умолчанию) = наследовать Jackett.proxyFallbackDirect.
        /// ⚠️ Именно bool?, а не bool: трёхзначность обязательна — трекер, у которого забанен наш
        /// собственный IP, должен уметь сказать false, а остальные — не переопределять дефолт.
        /// </summary>
        public bool? proxyFallbackDirect { get; set; }

        public bool useproxy { get; set; }

        public bool useproxystream { get; set; }

        public string globalnameproxy { get; set; }

        public ProxySettings proxy { get; set; }
    }
}
