using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;

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
        /// «Я жива, вот что я умею». Тело: {"name":"node1","egressPort":9121,"solverPort":0}.
        /// Хост узла из тела НЕ читаем — берём адрес источника (см. NodeRegistry).
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

            string name;
            int egressPort, solverPort;

            try
            {
                string raw;
                using (var reader = new StreamReader(HttpContext.Request.Body))
                    raw = await reader.ReadToEndAsync();

                var j = JObject.Parse(raw);
                name = j.Value<string>("name");
                egressPort = j.Value<int?>("egressPort") ?? 0;
                solverPort = j.Value<int?>("solverPort") ?? 0;
            }
            catch
            {
                // Кривое тело — это 400, а не 500: узел не должен уметь ронять ручку.
                return BadRequest(new { ok = false, reason = "bad body" });
            }

            string ip = (addr.IsIPv4MappedToIPv6 ? addr.MapToIPv4() : addr).ToString();

            if (!NodeRegistry.Hello(name, ip, egressPort, solverPort, nodes.maxNodes))
                return BadRequest(new { ok = false, reason = "rejected" });

            return Json(new { ok = true, ttlSeconds = nodes.ttlSeconds });
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
