using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using System.Net;

namespace JacRed.Controllers
{
    /// <summary>
    /// Приём объявлений от узлов-помощников (репозиторий E:\D1vision-node).
    ///
    /// Почему ручка живёт в JacRed, хотя путь начинается с /qdl/: пользуется реестром
    /// исключительно JacRed (выходы для трекеров), а модули компилируются Roslyn'ом
    /// по отдельности — держать реестр и приёмник в разных модулях значило бы заводить
    /// межмодульную ссылку на ровном месте. Путь оставлен /qdl/nodes/*, потому что он уже
    /// зашит в агенте на узлах; конфликта маршрутов нет, шаблон уникальный.
    /// </summary>
    public class NodeController : JacBaseController
    {
        static Models.AppConf.NodesConf nodes => jackett.nodes;

        #region /qdl/nodes/hello
        /// <summary>
        /// «Я жива, вот что я умею». Тело:
        /// {"name":"node1","host":"192.168.87.60","egressPort":9121,"solverPort":0}.
        /// host — адрес, по которому дом сможет достучаться; узел определяет его у себя сам.
        /// Не прислали — берём адрес соединения (годится лишь без проброса портов, см. ниже).
        /// </summary>
        [HttpPost]
        [AllowAnonymous]
        [Route("qdl/nodes/hello")]
        async public Task<ActionResult> Hello()
        {
            if (nodes == null || !nodes.enable)
                return StatusCode(503, new { ok = false, reason = "nodes disabled" });

            // ⚠️ Запрос с маркером edge пришёл ИЗ ИНТЕРНЕТА через Caddy. Адрес источника у него —
            // приватный адрес контейнера Caddy, то есть проверку «свой ли ты» он бы прошёл.
            // Держатель ключа платформы мог бы так зарегистрировать несуществующий выход и
            // заставить дом гонять через него трафик. Узлы живут в LAN и через edge не ходят.
            string edgeHeader = CoreInit.conf?.d1v?.edgeHeader;
            if (!string.IsNullOrEmpty(edgeHeader) && HttpContext.Request.Headers.ContainsKey(edgeHeader))
                return StatusCode(403, new { ok = false, reason = "external" });

            var addr = HttpContext.Connection.RemoteIpAddress;
            if (!NodeRegistry.IsPrivate(addr))
                return StatusCode(403, new { ok = false, reason = "not a private address" });

            string name, announced;
            int egressPort, solverPort;

            try
            {
                string raw;
                using (var reader = new StreamReader(HttpContext.Request.Body))
                    raw = await reader.ReadToEndAsync();

                var j = JObject.Parse(raw);
                name = j.Value<string>("name");
                announced = j.Value<string>("host");
                egressPort = j.Value<int?>("egressPort") ?? 0;
                solverPort = j.Value<int?>("solverPort") ?? 0;
            }
            catch
            {
                // Кривое тело — это 400, а не 500: узел не должен уметь ронять ручку.
                return BadRequest(new { ok = false, reason = "bad body" });
            }

            // ⚠️ Адрес источника ненадёжен, и это выяснилось в бою. Docker Desktop
            // подменяет его на шлюз своей сети у ВСЕХ запросов на проброшенный порт:
            // и узел с другой машины, и локальный curl приходят как 172.24.0.1 — адрес,
            // по которому никакого прокси нет. Поэтому основной источник — поле host,
            // которое узел определяет у себя сам, а адрес соединения остаётся запасным
            // (годится, когда дом и узел в одной docker-сети без проброса).
            string connIp = (addr.IsIPv4MappedToIPv6 ? addr.MapToIPv4() : addr).ToString();
            string ip = connIp;

            if (!string.IsNullOrWhiteSpace(announced))
            {
                if (!IPAddress.TryParse(announced.Trim(), out var a) || !NodeRegistry.IsPrivate(a))
                    return BadRequest(new { ok = false, reason = "bad host" });

                ip = (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).ToString();
            }

            // Разовая проверка ДОСТИЖИМОСТИ на приёме объявления (не хелс-чек: живость
            // ролей потом отслеживает сам трафик). Без неё неверный адрес принимался бы
            // молча, дом гонял бы через него поиск и каждый раз откатывался — ровно то,
            // что пришлось откапывать руками. Теперь узел получает внятный отказ и пишет
            // его в свой лог.
            if (egressPort > 0 && !await NodeRegistry.Reachable(ip, egressPort))
                return BadRequest(new { ok = false, reason = "egress unreachable from home", ip, port = egressPort });

            if (!NodeRegistry.Hello(name, ip, egressPort, solverPort, nodes.maxNodes))
                return BadRequest(new { ok = false, reason = "rejected" });

            return Json(new { ok = true, ttlSeconds = nodes.ttlSeconds, ip });
        }
        #endregion

        #region /qdl/nodes/forget
        /// <summary>
        /// Убрать узел из реестра руками: `?name=X`, либо `?dead=1` — все неживые разом.
        /// Сам по себе узел исчезает лишь через час молчания, а следы тестов и
        /// переименований мешают читать диагностику раньше.
        ///
        /// Живой узел убрать можно, но он вернётся ближайшим ударом (30 с) — это не
        /// способ «отключить» узел, для этого его надо погасить на его машине.
        /// </summary>
        [HttpGet]
        [AllowAnonymous]
        [Route("qdl/nodes/forget")]
        public ActionResult Forget(string name, int dead = 0)
        {
            if (nodes == null)
                return Json(new { ok = false, reason = "nodes disabled" });

            // Управляющая ручка — только из LAN, как и регистрация.
            string edgeHeader = CoreInit.conf?.d1v?.edgeHeader;
            if (!string.IsNullOrEmpty(edgeHeader) && HttpContext.Request.Headers.ContainsKey(edgeHeader))
                return StatusCode(403, new { ok = false, reason = "external" });

            int removed = dead == 1
                ? NodeRegistry.ForgetDead(nodes.ttlSeconds)
                : NodeRegistry.Forget(name);

            return Json(new { ok = true, removed, items = NodeRegistry.Snapshot(nodes.ttlSeconds, maskIp: false) });
        }
        #endregion

        #region /qdl/nodes
        /// <summary>Кто сейчас в реестре. Снаружи адреса узлов маскируются.</summary>
        [HttpGet]
        [AllowAnonymous]
        [Route("qdl/nodes")]
        public ActionResult List()
        {
            if (nodes == null)
                return Json(new { enable = false, items = Array.Empty<object>() });

            string edgeHeader = CoreInit.conf?.d1v?.edgeHeader;
            bool external = !string.IsNullOrEmpty(edgeHeader) && HttpContext.Request.Headers.ContainsKey(edgeHeader);

            return Json(new
            {
                enable = nodes.enable,
                ttlSeconds = nodes.ttlSeconds,
                items = NodeRegistry.Snapshot(nodes.ttlSeconds, maskIp: external)
            });
        }
        #endregion
    }
}
