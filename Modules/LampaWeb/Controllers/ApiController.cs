using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared;
using Shared.Attributes;
using Shared.Models.Events;
using Shared.Services;
using Shared.Services.Pools;
using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using IO = System.IO;

namespace LampaWeb;

public class ApiController : BaseController
{
    [HttpGet, AllowAnonymous]
    [Route("/personal.lampa")]
    [Route("/lampa-main/personal.lampa")]
    [Route("/{myfolder}/personal.lampa")]
    public ActionResult PersonalLampa(string myfolder)
        => StatusCode(200);


    [HttpGet, AllowAnonymous]
    [Route("/reqinfo")]
    public ActionResult Reqinfo() => Content(JsonConvert.SerializeObject(requestInfo, new JsonSerializerSettings()
    {
        NullValueHandling = NullValueHandling.Ignore,
        DefaultValueHandling = DefaultValueHandling.Ignore
    }), "application/json; charset=utf-8");


    #region Index
    // setHeadersNoCache + ?v= на lampainit.js: браузеры/WebView (ТВ) кэшировали вход и старый
    // lampainit.js (в нём наши патчи) — фиксы «не доезжали» до клиента без ручного Ctrl+F5.
    //
    // 🔴 d1v:index-no-staticache — серверного Staticache у входа НЕТ. Было
    // [Staticache(5, always: true, setHeadersNoCache: true)] с пояснением «каждый рестарт сбрасывает
    // кэш сам» — неверно: кеш дисковый (/lampac/cache/static) и рестарт переживает. В тело входа
    // вшит LamInitVersion(), то есть адреса ВСЕХ наших плагинов; закешированный вход после деплоя
    // до пяти минут раздавал клиентам старый bootstrap → старый music.js, и правка «не доезжала»
    // (телевизор загрузил 2.101 через две минуты после выкатки 2.102, при свежем ответе на прямой
    // запрос). Третья ловушка той же семьи после queryKeys у lampainit.js и состава LamInitVersion.
    // Цена отказа от кеша — генерация 3 КБ строки на запрос, это микросекунды.
    [HttpGet, AllowAnonymous]
    [Route("/")]
    public ActionResult Index()
    {
        SetHeadersNoCache();

        if (string.IsNullOrEmpty(ModInit.conf.index))
            return Content("api work", "text/plain; charset=utf-8");

        if (ModInit.conf.basetag && Regex.IsMatch(ModInit.conf.index, "/[^\\./]+\\.html$"))
        {
            string html = IO.File.ReadAllText($"wwwroot/{ModInit.conf.index}");
            html = html.Replace("<head>", $"<head><base href=\"/{Regex.Match(ModInit.conf.index, "^([^/]+)/").Groups[1].Value}/\" />");
            html = html.Replace("src=\"/lampainit.js\"", $"src=\"/lampainit.js?v={LamInitVersion()}\"");

            // ?v= у app.min.js/app.css: вместо апстримового 15-минутного тика — стабильная версия
            // (см. AppCacheVersion); в паре с immutable-заголовками LampaApp/LampaAppCss клиент
            // держит 2-МБ бандл в кэше до деплоя. Апстрим изменил сниппет → замена просто не
            // сработает и останется прежнее 15-минутное поведение (деградация к статус-кво).
            html = Regex.Replace(html, @"Math\.floor\(\(new Date\(\)\)\.getTime\(\)\s*/\s*9e5\)", "'" + AppCacheVersion() + "'");

            return ContentTo(html, "text/html; charset=utf-8");
        }

        return LocalRedirect($"/{ModInit.conf.index}");
    }
    #endregion

    #region Extensions
    [HttpGet, AllowAnonymous]
    [Staticache(5, always: true, setHeadersNoCache: true)]
    [Route("/extensions")]
    public ActionResult Extensions()
    {
        string extensions = FileCache.ReadAllText($"{ModInit.modpath}/plugins/extensions.json", "extensions.json", saveCache: false)
            .Replace("{localhost}", host)
            .Replace("\n", "")
            .Replace("\r", "");

        return ContentTo(extensions);
    }
    #endregion

    #region testaccsdb
    [HttpGet, HttpPost]
    [Route("/testaccsdb")]
    public ActionResult TestAccsdb(string account_email, string uid)
    {
        // uid совпадает с shared_passwd, возвращаем команду изменить uid
        if (!string.IsNullOrEmpty(CoreInit.conf.accsdb.shared_passwd) && uid == CoreInit.conf.accsdb.shared_passwd)
            return Content("{\"accsdb\": true, \"newuid\": true}", "application/json; charset=utf-8");

        #region shared_passwd
        if (!string.IsNullOrEmpty(uid) && !string.IsNullOrEmpty(account_email) && account_email == CoreInit.conf.accsdb.shared_passwd)
        {
            try
            {
                string file = "users.json";

                JArray arr = new JArray();

                if (IO.File.Exists(file))
                {
                    var txt = IO.File.ReadAllText(file);
                    if (!string.IsNullOrWhiteSpace(txt))
                        try { arr = JArray.Parse(txt); } catch { arr = new JArray(); }
                }

                bool exists = arr.Children<JObject>().Any(o =>
                    (o.Value<string>("id") != null && o.Value<string>("id").Equals(uid, StringComparison.OrdinalIgnoreCase)) ||
                    (o["ids"] != null && o["ids"].Any(t => t.ToString().Equals(uid, StringComparison.OrdinalIgnoreCase)))
                );

                if (exists)
                    return Content("{\"accsdb\": false}", "application/json; charset=utf-8");

                var obj = new JObject();
                obj["id"] = uid;
                obj["expires"] = DateTime.Now.AddDays(Math.Max(1, CoreInit.conf.accsdb.shared_daytime));

                arr.Add(obj);

                IO.File.WriteAllText(file, arr.ToString(Formatting.Indented));

                return Content("{\"accsdb\": false, \"success\": true, \"uid\": \"" + uid + "\"}", "application/json; charset=utf-8");
            }
            catch
            {
                return Content("{\"accsdb\": true}", "application/json; charset=utf-8");
            }
        }
        #endregion

        return Content("{\"accsdb\": false, \"success\": true}", "application/json; charset=utf-8");
    }
    #endregion


    #region app.min.js
    [HttpGet, AllowAnonymous]
    // immutable: URL versioned (?v= из Index) → вечный клиентский кэш.
    // queryKeys "v" ОБЯЗАТЕЛЕН: без него старый и новый ?v делят одну запись серверного кеша,
    // и после автообновления фронта LampaCron'ом клиент навечно закешировал бы СТАРОЕ тело под НОВЫМ ?v.
    // TTL 20 мин → 7 суток: тело адресуется своим ?v, поэтому протухание по времени ничего не чинит,
    // зато КАЖДОЕ протухание стоило клиентам лишних сотен килобайт — .br-сайдкар (384 КБ) жмётся
    // фоном после первой записи, и до его готовности ответы шли динамическим сжатием: замер дал
    // 562 КБ на MISS и 644 КБ на HIT-без-сайдкара против 384 КБ на прогретом (br поверх SendFile
    // эмитит блок на каждый чанк — HIT выходит ТОЛЩЕ MISS, см. claude/06 §AS).
    [Staticache(10080, always: true, immutable: true, queryKeys: new[] { "v" })]
    [Route("/app.min.js")]
    [Route("{type}/app.min.js")]
    public ActionResult LampaApp(string type)
    {
        if (string.IsNullOrEmpty(type))
        {
            if (ModInit.conf.path != null)
            {
                type = ModInit.conf.path;
            }
            else
            {
                if (ModInit.conf.index == null || !ModInit.conf.index.Contains("/"))
                    return Content(string.Empty, "application/javascript; charset=utf-8");

                type = ModInit.conf.index.Split("/")[0];
            }
        }
        else
        {
            type = Regex.Replace(type, "[^a-z0-9\\-]", "");
        }

        string file = IO.File.ReadAllText($"wwwroot/{type}/app.min.js");

        #region appReplace
        if (ModInit.conf.appReplace != null)
        {
            string _host = Regex.Replace(host, "^https?://", "");

            foreach (var r in ModInit.conf.appReplace)
            {
                string val = r.Value;
                if (val.StartsWith("file:"))
                    val = IO.File.ReadAllText(val.Substring(5));

                val = val.Replace("{localhost}", host).Replace("{host}", _host);
                file = Regex.Replace(file, r.Key, val, RegexOptions.IgnoreCase);
            }
        }
        #endregion

        var bulder = new StringBuilder();
        bulder = bulder.Append(file);

        if (ModInit.conf.initPlugins.cubProxy)
        {
            bulder = bulder.Replace("protocol + mirror + '/api/checker'", $"'{host}/cub/api/checker'");

            string utlprotocol = file.Contains("Utils$1.protocol()") ?
                "Utils$1.protocol()" :
                "Utils$2.protocol()";

            bulder = bulder.Replace($"{utlprotocol} + 'tmdb.' + object$2.cub_domain + '/' + u,", $"'{host}/cub/tmdb./' + u,");
            bulder = bulder.Replace($"{utlprotocol} + object$2.cub_domain", $"'{host}/cub/red'");
            bulder = bulder.Replace("object$2.cub_domain", $"'{CoreInit.conf.cub.mirror}'");
        }

        bulder = bulder.Replace("http://lite.lampa.mx", $"{host}/{type}");
        bulder = bulder.Replace("https://yumata.github.io/lampa-lite", $"{host}/{type}");

        bulder = bulder.Replace("http://lampa.mx", $"{host}/{type}");
        bulder = bulder.Replace("https://yumata.github.io/lampa", $"{host}/{type}");

        bulder = bulder.Replace("window.lampa_settings.dcma = dcma;", "window.lampa_settings.fixdcma = true;");
        bulder = bulder.Replace("Storage.get('vpn_checked_ready', 'false')", "true");

        bulder = bulder.Replace("status$1 = false;", "status$1 = true;"); // local apk to personal.lampa
        bulder = bulder.Replace("return status$1;", "return true;"); // отключение рекламы
        bulder = bulder.Replace("if (!Storage.get('metric_uid', ''))", "return;"); // metric
        bulder = bulder.Replace("function log(data) {", "function log(data) { return;");
        bulder = bulder.Replace("function stat$1(method, name) {", "function stat$1(method, name) { return;");
        bulder = bulder.Replace("if (domain) {", "if (false) {");

        bulder = bulder.Replace("{localhost}", host);

        if (EventListener.AppReplace != null)
        {
            foreach (Func<string, EventAppReplace, StringBuilder> handler in EventListener.AppReplace.GetInvocationList())
                bulder = handler.Invoke("appjs", new EventAppReplace(bulder, null, type, host, requestInfo, HttpContext.Request));
        }

        return ContentTo(bulder, "application/javascript; charset=utf-8");
    }
    #endregion

    #region app.css
    [HttpGet, AllowAnonymous]
    [Staticache(10080, always: true, immutable: true, queryKeys: new[] { "v" })]   // см. комментарий у LampaApp
    [Route("/css/app.css")]
    [Route("{type}/css/app.css")]
    public ActionResult LampaAppCss(string type)
    {
        if (string.IsNullOrEmpty(type))
        {
            if (ModInit.conf.path != null)
            {
                type = ModInit.conf.path;
            }
            else
            {
                if (ModInit.conf.index == null || !ModInit.conf.index.Contains("/"))
                    return Content(string.Empty, "text/css; charset=utf-8");

                type = ModInit.conf.index.Split("/")[0];
            }
        }
        else
        {
            type = Regex.Replace(type, "[^a-z0-9\\-]", "");
        }

        if (ModInit.conf.cssReplace == null && EventListener.AppReplace == null)
        {
            return File($"/{type}/css/app.css", "text/css; charset=utf-8");
        }
        else
        {
            string css = IO.File.ReadAllText($"wwwroot/{type}/css/app.css");

            if (ModInit.conf.cssReplace != null)
            {
                string _host = Regex.Replace(host, "^https?://", "");
                foreach (var r in ModInit.conf.cssReplace)
                {
                    string val = r.Value;
                    if (val.StartsWith("file:"))
                        val = IO.File.ReadAllText(val.Substring(5));

                    val = val.Replace("{localhost}", host).Replace("{host}", _host);
                    css = Regex.Replace(css, r.Key, val, RegexOptions.IgnoreCase);
                }
            }

            if (EventListener.AppReplace == null)
                return ContentTo(css, "text/css; charset=utf-8");

            var bulder = new StringBuilder();
            bulder = bulder.Append(css);

            if (EventListener.AppReplace != null)
            {
                foreach (Func<string, EventAppReplace, StringBuilder> handler in EventListener.AppReplace.GetInvocationList())
                    bulder = handler.Invoke("appcss", new EventAppReplace(bulder, null, type, host, requestInfo, HttpContext.Request));
            }

            return ContentTo(bulder, "text/css; charset=utf-8");
        }
    }
    #endregion


    #region MSX
    [HttpGet, AllowAnonymous]
    [Staticache(20, always: true, setHeadersNoCache: true)]
    [Route("msx/start.json")]
    public ActionResult MSX()
    {
        string msx = FileCache.ReadAllText($"{ModInit.modpath}/msx.json", "msx.json", saveCache: false)
            .Replace("{localhost}", host);

        return ContentTo(msx);
    }
    #endregion

    #region Widgets
    // Генерация .wgt: два одновременных запроса /samsung.wgt работали бы в одной
    // publish-директории (перетирание файлов между заменой плейсхолдеров и подсчётом
    // хэшей → битая подпись; CreateNew-режим зипа → 500). Запрос редкий — простой lock.
    static readonly object wgtGenLock = new object();

    [HttpGet, AllowAnonymous]
    [Route("samsung.wgt")]
    public ActionResult SamsWgt(string overwritehost)
    {
        if (!ModInit.conf.widgets.samsung)
            return NotFound();

        // D1Vision: ключ периметра платформы tizen — подставляется в loader.js ({d1vkey}),
        // чтобы пробы/скрипты виджета проходили периметр на внешних хостах (tv/tv2).
        // Ключа нет → пустая строка: LAN работает без ключа, WAN закрыт (как и раньше).
        // conf читается ОДИН раз в локальную переменную: init.conf перечитывается на лету,
        // повторное обращение к CoreInit.conf может увидеть уже другой объект (NRE).
        string tizenKey = "";
        var d1vKeys = CoreInit.conf?.d1v?.keys;
        if (d1vKeys != null && d1vKeys.TryGetValue("tizen", out string tk) && !string.IsNullOrEmpty(tk))
        {
            // Ключ уходит сырым Replace в одинарно-кавычный JS-литерал loader.js —
            // кавычка/бэкслеш в значении синтаксически убили бы весь loader.js
            // (виджет умер бы даже в LAN). Пропускаем только безопасный алфавит.
            if (Regex.IsMatch(tk, @"^[0-9A-Za-z_\-]+$"))
                tizenKey = tk;
        }

        // Версия v5 (гонка хостов + {d1vkey}); ключ в кэш-ключе — смена ключа в init.conf
        // перегенерит .wgt без ручной чистки. Скобки обязательны: раньше "v4" клеился только
        // к host и при ?overwritehost= версия в кэш-ключе не участвовала вовсе.
        string cache = $"cache/widgets/samsung/{Shared.Services.Utilities.CrypTo.md5((overwritehost ?? host) + ":v5:" + Shared.Services.Utilities.CrypTo.md5(tizenKey))}";
        string wgt = $"{cache}.wgt";

        if (IO.File.Exists(wgt))
            return File(IO.File.OpenRead(wgt), "application/octet-stream");

        lock (wgtGenLock)
        {
            if (!IO.File.Exists(wgt))
            {
                var widgetDirectory = $"{ModInit.modpath}/widgets/samsung";
                var publishDirectory = $"{cache}/publish";

                IO.Directory.CreateDirectory(publishDirectory);

                foreach (string inFilePath in IO.Directory.GetFiles(widgetDirectory, "*", IO.SearchOption.AllDirectories))
                {
                    string outFile = inFilePath.Replace(widgetDirectory, publishDirectory);
                    IO.Directory.CreateDirectory(IO.Path.GetDirectoryName(outFile));
                    IO.File.Copy(inFilePath, outFile, true);
                }

                string index = IO.File.ReadAllText($"{publishDirectory}/index.html");
                IO.File.WriteAllText($"{publishDirectory}/index.html", index.Replace("{localhost}", overwritehost ?? host));

                string loader = IO.File.ReadAllText($"{publishDirectory}/loader.js");
                IO.File.WriteAllText($"{publishDirectory}/loader.js", loader.Replace("{localhost}", overwritehost ?? host).Replace("{d1vkey}", tizenKey));

                string app = IO.File.ReadAllText($"{publishDirectory}/app.js");
                IO.File.WriteAllText($"{publishDirectory}/app.js", app.Replace("{localhost}", overwritehost ?? host));

                string gethash(string file)
                {
                    using (var sha = System.Security.Cryptography.SHA512.Create())
                    {
                        return Convert.ToBase64String(sha.ComputeHash(IO.File.ReadAllBytes(file)));
                    }
                }

                string indexhashsha512 = gethash($"{publishDirectory}/index.html");
                string loaderhashsha512 = gethash($"{publishDirectory}/loader.js");
                string apphashsha512 = gethash($"{publishDirectory}/app.js");
                string confighashsha512 = gethash($"{publishDirectory}/config.xml");
                string iconhashsha512 = gethash($"{publishDirectory}/icon.png");
                string logohashsha512 = gethash($"{publishDirectory}/logo_appname_fg.png");

                string author_sigxml = IO.File.ReadAllText($"{widgetDirectory}/author-signature.xml");
                author_sigxml = author_sigxml
                    .Replace("loaderhashsha512", loaderhashsha512)
                    .Replace("apphashsha512", apphashsha512)
                    .Replace("iconhashsha512", iconhashsha512)
                    .Replace("logohashsha512", logohashsha512)
                    .Replace("confighashsha512", confighashsha512)
                    .Replace("indexhashsha512", indexhashsha512);

                IO.File.WriteAllText($"{publishDirectory}/author-signature.xml", author_sigxml);

                string authorsignaturehashsha512 = gethash($"{publishDirectory}/author-signature.xml");
                string sigxml1 = IO.File.ReadAllText($"{publishDirectory}/signature1.xml");
                sigxml1 = sigxml1
                    .Replace("loaderhashsha512", loaderhashsha512)
                    .Replace("apphashsha512", apphashsha512)
                    .Replace("confighashsha512", confighashsha512)
                    .Replace("authorsignaturehashsha512", authorsignaturehashsha512)
                    .Replace("iconhashsha512", iconhashsha512)
                    .Replace("logohashsha512", logohashsha512)
                    .Replace("indexhashsha512", indexhashsha512);

                IO.File.WriteAllText($"{publishDirectory}/signature1.xml", sigxml1);

                // Зипуем во временный файл и переименовываем атомарно: читатель, увидевший
                // File.Exists(wgt), никогда не откроет недописанный архив.
                string tmp = $"{wgt}.tmp";
                if (IO.File.Exists(tmp))
                    IO.File.Delete(tmp);
                IO.Compression.ZipFile.CreateFromDirectory(publishDirectory, tmp);
                IO.File.Move(tmp, wgt);

                IO.Directory.Delete(cache, true);
            }
        }

        return File(IO.File.OpenRead(wgt), "application/octet-stream");
    }

    [HttpGet]
    [AllowAnonymous]
    [Route("lg.ipk")]
    public ActionResult LgIpk(string overwritehost)
    {
        if (!ModInit.conf.widgets.lg)
            return NotFound();

        string cache = $"cache/widgets/lg/{Shared.Services.Utilities.CrypTo.md5(overwritehost ?? host + "v01")}";
        string ipk = $"{cache}.ipk";

        if (IO.File.Exists(ipk))
            return File(IO.File.OpenRead(ipk), "application/octet-stream");

        var widgetDirectory = $"{ModInit.modpath}/widgets/lg";
        if (!IO.Directory.Exists(widgetDirectory))
            return NotFound();

        if (!IO.Directory.Exists($"{widgetDirectory}/app") ||
            !IO.File.Exists($"{widgetDirectory}/service.tar.gz") ||
            !IO.File.Exists($"{widgetDirectory}/control/control"))
            return NotFound();

        const string appId = "com.lampac.tv";
        const string serviceId = appId + ".worker";

        string appSource = $"{widgetDirectory}/app";
        string serviceArchive = $"{widgetDirectory}/service.tar.gz";
        string controlSource = $"{widgetDirectory}/control";
        string replaceHost = overwritehost ?? host;

        #region packageinfo.json (mandatory for webOS)
        string appVersion = "1.0.0";
        try
        {
            var appInfo = JObject.Parse(IO.File.ReadAllText($"{appSource}/appinfo.json"));
            appVersion = appInfo.Value<string>("version") ?? appVersion;
        }
        catch { }

        byte[] packageInfoData = Encoding.UTF8.GetBytes(new JObject
        {
            ["id"] = appId,
            ["version"] = appVersion,
            ["app"] = appId,
            ["services"] = new JArray(serviceId)
        }.ToString(Formatting.Indented));
        #endregion

        // app files patched with the host (root level only, like the legacy build)
        var hostPatchedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "index.html", "loader.js", "app.js" };

        long installedSize = 0;

        // data.tar.gz — packed straight from the source folders, no intermediate copy
        byte[] dataTarGz = BuildTarGz(tar =>
        {
            void AddTree(string source, string prefix, bool patch)
            {
                foreach (string file in IO.Directory.GetFiles(source, "*", IO.SearchOption.AllDirectories))
                {
                    string rel = IO.Path.GetRelativePath(source, file).Replace('\\', '/');
                    string entryName = $"{prefix}/{rel}";

                    if (patch && !rel.Contains('/') && hostPatchedFiles.Contains(rel))
                    {
                        byte[] data = Encoding.UTF8.GetBytes(IO.File.ReadAllText(file).Replace("{localhost}", replaceHost));
                        WriteTarBytes(tar, entryName, data);
                        installedSize += data.Length;
                    }
                    else
                    {
                        tar.WriteEntry(file, entryName);
                        installedSize += new IO.FileInfo(file).Length;
                    }
                }
            }

            // The worker service ships as a single archive (1 file instead of ~1900 loose
            // files) — this keeps build-copy and antivirus on-access scanning cheap.
            void AddArchive(string archive, string prefix)
            {
                using var fs = IO.File.OpenRead(archive);
                using var gzip = new GZipStream(fs, CompressionMode.Decompress);
                using var reader = new TarReader(gzip);

                while (reader.GetNextEntry() is TarEntry entry)
                {
                    if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                        continue;

                    var data = new IO.MemoryStream();
                    entry.DataStream?.CopyTo(data); // null for empty files (e.g. 0-byte .gitmodules)
                    data.Position = 0;

                    WriteTarStream(tar, $"{prefix}/{entry.Name}", data);
                    installedSize += data.Length;
                }
            }

            AddTree(appSource, $"usr/palm/applications/{appId}", patch: true);
            AddArchive(serviceArchive, $"usr/palm/services/{serviceId}");

            WriteTarBytes(tar, $"usr/palm/packages/{appId}/packageinfo.json", packageInfoData);
            installedSize += packageInfoData.Length;
        });

        // control.tar.gz — Installed-Size patched in memory
        byte[] controlTarGz = BuildTarGz(tar =>
        {
            foreach (string file in IO.Directory.GetFiles(controlSource, "*", IO.SearchOption.AllDirectories))
            {
                string rel = IO.Path.GetRelativePath(controlSource, file).Replace('\\', '/');

                if (rel.Equals("control", StringComparison.OrdinalIgnoreCase))
                {
                    string text = Regex.Replace(IO.File.ReadAllText(file), "(?m)^Installed-Size:.*$", $"Installed-Size: {installedSize}");
                    WriteTarBytes(tar, rel, Encoding.UTF8.GetBytes(text));
                }
                else
                {
                    tar.WriteEntry(file, rel);
                }
            }
        });

        // debian-binary must be exactly "2.0\n" (LF) — normalize in case the on-disk file has CRLF
        byte[] debianBinary = IO.File.Exists($"{widgetDirectory}/debian-binary")
            ? Encoding.ASCII.GetBytes(IO.File.ReadAllText($"{widgetDirectory}/debian-binary").Replace("\r\n", "\n").Replace("\r", "\n"))
            : Encoding.ASCII.GetBytes("2.0\n");

        IO.Directory.CreateDirectory(IO.Path.GetDirectoryName(ipk));

        using (var stream = IO.File.Create(ipk))
        using (var writer = new IO.BinaryWriter(stream, Encoding.ASCII, leaveOpen: false))
        {
            writer.Write(Encoding.ASCII.GetBytes("!<arch>\n"));
            WriteArFile(writer, "debian-binary", debianBinary);
            WriteArFile(writer, "control.tar.gz", controlTarGz);
            WriteArFile(writer, "data.tar.gz", dataTarGz);
        }

        return File(IO.File.OpenRead(ipk), "application/octet-stream");

        static byte[] BuildTarGz(Action<TarWriter> write)
        {
            using var ms = new IO.MemoryStream();
            using (var gzip = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            using (var tar = new TarWriter(gzip, TarEntryFormat.Ustar, leaveOpen: true))
            {
                write(tar);
            }

            return ms.ToArray();
        }

        static void WriteTarBytes(TarWriter tar, string entryName, byte[] data)
            => WriteTarStream(tar, entryName, new IO.MemoryStream(data));

        static void WriteTarStream(TarWriter tar, string entryName, IO.Stream data)
        {
            var entry = new UstarTarEntry(TarEntryType.RegularFile, entryName)
            {
                Mode = IO.UnixFileMode.UserRead | IO.UnixFileMode.UserWrite |
                       IO.UnixFileMode.GroupRead | IO.UnixFileMode.OtherRead,
                DataStream = data
            };

            tar.WriteEntry(entry);
        }

        static void WriteArFile(IO.BinaryWriter writer, string name, byte[] data)
        {
            string fileName = name.EndsWith("/") ? name : $"{name}/";

            string header =
                PadRight(fileName, 16) +
                PadRight("0", 12) +
                PadRight("0", 6) +
                PadRight("0", 6) +
                PadRight("100644", 8) +
                PadRight(data.Length.ToString(), 10) +
                "`\n";

            writer.Write(Encoding.ASCII.GetBytes(header));
            writer.Write(data);

            if (data.Length % 2 != 0)
                writer.Write((byte)'\n');

            static string PadRight(string value, int len)
            {
                if (value.Length > len)
                    return value.Substring(0, len);

                return value.PadRight(len, ' ');
            }
        }
    }

    #endregion

    #region lampainit.js
    // Cache-buster for our client plugin URLs: recomputed on each process (re)start, so redeployed JS is
    // re-fetched even by clients that ignore no-cache headers (Tizen/webOS). Injected via the {version} token.
    static readonly string cacheVersion = System.DateTime.UtcNow.Ticks.ToString("x");

    // cache_version для index.html (?v= у app.min.js / css/app.css): апстримовый index генерит
    // Math.floor(Date/9e5) — новый ?v каждые 15 минут, т.е. браузер перекачивал 2-МБ app.min.js
    // при каждом запуске позже 15 мин. Версия — ТОЛЬКО из mtime+length файла (qdl 2.16: раньше
    // сюда входил cacheVersion=ticks процесса, и каждый рестарт контейнера заставлял всех клиентов
    // перекачивать бандл+css ~630 КБ br без единого изменённого байта). Меняется при реальном
    // обновлении вендора/LampaCron.
    static string _appVer;
    static long _appVerMtime = -1;
    static string AppCacheVersion()
    {
        try
        {
            string type = ModInit.conf.path ?? (ModInit.conf.index ?? "lampa-main/index.html").Split('/')[0];
            var fi = new IO.FileInfo($"wwwroot/{type}/app.min.js");
            long mt = fi.Exists ? fi.LastWriteTimeUtc.Ticks : 0;
            // Свежий mtime (<60 с) = LampaCron может ещё писать файл (распаковка не атомарна):
            // не выпускаем новый ?v, пока файл не «отлежался» — иначе клиент рискует навечно
            // (immutable) закешировать обрезанный бандл. До тех пор старый ?v → старая
            // серверная кеш-запись → консистентная старая пара версия+тело.
            if (_appVer != null && mt != _appVerMtime && DateTime.UtcNow.Ticks - mt < TimeSpan.TicksPerMinute)
                return _appVer;
            if (_appVer == null || mt != _appVerMtime) { _appVerMtime = mt; _appVer = $"{mt:x}-{(fi.Exists ? fi.Length : 0):x}"; }
            return _appVer;
        }
        catch { return cacheVersion; }
    }

    // qdl 2.45: стабильный ?v для НАШИХ плагинов (qdl.js, music.js) — тот же приём, что уже спас
    // app.min.js/app.css выше. До этого оба URL несли {version} = cacheVersion = тики старта
    // процесса, поэтому КАЖДЫЙ рестарт контейнера заставлял всех клиентов перекачивать ~221 КБ br
    // без единого изменённого байта. Хост падает по питанию ~23 раза в месяц — это был последний
    // крупный «рестартный налог» (из заметного остаётся только сам lampainit.js, ~19 КБ).
    //
    // Версия = mtime+length файла плагина, смешанные с mtime init.conf. Второе слагаемое обязательно:
    // тело music.js собирается ещё и из флагов init.conf ({client_debug_enabled} и соседние), и без
    // него правка флага без замены файла оставила бы клиентов с ВЕЧНЫМ (immutable год) старым телом.
    // Через mtime init.conf это ловится без обращения к чужому модулю: Music и QbitDownload —
    // отдельные Roslyn-сборки, их ModInit отсюда не виден.
    //
    // Гард «файл моложе 60 с» — как в AppCacheVersion: пока файл могут дописывать (docker cp при
    // быстрой итерации), новый ?v не выпускаем, иначе клиент рискует навечно закешировать обрезанный
    // плагин.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long stamp, string ver)> _pluginVer = new();

    static string PluginVersion(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path))
                return cacheVersion;

            var fi = new IO.FileInfo(path);
            long mt = fi.Exists ? fi.LastWriteTimeUtc.Ticks : 0;
            long len = fi.Exists ? fi.Length : 0;

            var ci = new IO.FileInfo("init.conf");
            long ct = ci.Exists ? ci.LastWriteTimeUtc.Ticks : 0;

            long stamp = mt ^ (len * 31) ^ (ct * 131);

            if (_pluginVer.TryGetValue(path, out var prev))
            {
                if (prev.stamp == stamp)
                    return prev.ver;

                // файл ещё «горячий» — отдаём прежнюю версию, пока не отлежится
                if (DateTime.UtcNow.Ticks - mt < TimeSpan.TicksPerMinute)
                    return prev.ver;
            }

            string ver = $"{mt:x}-{len:x}-{ct:x}";
            _pluginVer[path] = (stamp, ver);
            return ver;
        }
        catch { return cacheVersion; }
    }

    // qdl 2.53: стабильный ?v для самого lampainit.js — последний «рестартный налог». Он несёт
    // immutable-заголовок при наличии ?v (LamInit ниже), но версия была cacheVersion = тики старта
    // процесса, поэтому после каждого рестарта (хост падает по питанию ~23 раза в месяц) все
    // клиенты тянули ~22 КБ br заново. Тело ручки = lampainit.js + инлайн lampainit-invc.js, обе
    // версии считает PluginVersion, а он уже подмешивает mtime init.conf — то есть прежнее
    // возражение «тело зависит от читаемого на лету init.conf» закрыто.
    // ⚠️ Тело зависит ещё и от host, но клиентский кеш адресуется URL'ом, в котором host и есть.
    // 🔴 d1v:laminit-version-plugins — версия обязана включать ВСЕ файлы, чьи версии вшиты в тело.
    //
    // Было: только lampainit.js + lampainit-invc.js. Но ниже тело подставляет адресные ?v для
    // qdl.js, music.js и desktop.js — то есть их версии ЛЕЖАТ ВНУТРИ этого ответа. Правишь
    // music/plugin.js → его собственная версия меняется, а версия lampainit остаётся прежней →
    // закешированное тело продолжает раздавать клиентам СТАРУЮ ссылку на плагин, и правка не
    // доезжает никогда. Ни пересборка, ни рестарт не помогают: кеш дисковый.
    //
    // Это стоило нескольких кругов «выкатил — у владельца ничего не изменилось»: файл в образе
    // новый, прямой запрос отдаёт новое, а клиент грузит старое, потому что ссылку ему выдал
    // устаревший bootstrap.
    static string LamInitVersion()
        => PluginVersion($"{ModInit.modpath}/plugins/lampainit.js") + "-" +
           PluginVersion($"{ModInit.modpath}/plugins/lampainit-invc.js") + "-" +
           PluginVersion(ModuleFile("QbitDownload", "plugins/qdl.js")) + "-" +
           PluginVersion(ModuleFile("Music", "plugin.js")) + "-" +
           PluginVersion(ModuleFile("QbitDownload", "plugins/desktop.js"));

    /// <summary>Путь к файлу внутри каталога модуля; null, если модуль не загружен.</summary>
    static string ModuleFile(string moduleName, string relative)
    {
        var m = CoreInit.modules?.FirstOrDefault(x => x != null && string.Equals(x.name, moduleName, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(m?.path) ? null : IO.Path.Combine(m.path, relative);
    }

    [HttpGet, AllowAnonymous]
    // d1v:laminit-cache-key — ?v ОБЯЗАН входить в ключ серверного кеша.
    //
    // Было `[Staticache(20, always: true)]` без queryKeys: ключ = один только путь. Версия при
    // этом считалась правильно (LamInitVersion подмешивает и lampainit.js, и lampainit-invc.js),
    // но до ключа не доходила — и сервер продолжал отдавать СТАРОЕ тело новым клиентам. Правка
    // lampainit-invc.js молча не доезжала: файл в образе новый, ответ прежний, рестарт не
    // помогает (кеш дисковый, /lampac/cache/static). Потеряно на этом было полчаса вслепую.
    //
    // Комментарий ниже утверждает, что серверный кеш здесь «и так отключён через
    // StatiCacheDisabled» — это верно только когда в теле есть {country}; без него кеш работает.
    // Форма ключа взята у music.js: [Staticache(..., queryKeys = ["v"])].
    [Staticache(20, always: true, queryKeys = ["v"])]
    [Route("lampainit.js")]
    public ActionResult LamInit()
    {
        // qdl 2.16: ?v={cacheVersion} есть у всех клиентов (index.html) → versioned-URL кэшируем
        // навсегда, как qdl.js: −15 КБ br на каждом старте. Тело зависит от {country}/host, но это
        // КЛИЕНТСКИЙ кеш собственного ответа — безопасно (серверный Staticache для lampainit и так
        // отключён через StatiCacheDisabled ниже). Легаси-запрос без ?v — прежний no-cache.
        if (HttpContext.Request.Query.ContainsKey("v"))
            HttpContext.Response.Headers["Cache-Control"] = "public,max-age=31536000,immutable";
        else
            SetHeadersNoCache();

        var sb = StringBuilderPool.Rent();

        try
        {
            string lampainitjs = FileCache.ReadAllText($"{ModInit.modpath}/plugins/lampainit.js", "lampainit.js");
            if (lampainitjs.Contains("{country}"))
                StatiCacheDisabled = true;

            sb = sb.Append(lampainitjs);

            #region plugins
            List<LampaPlugin> plugins = new(20);

            if (ModInit.conf.initPlugins.dlna)
                plugins.Add(new("{localhost}/dlna.js", 1, "DLNA", "lampac"));

            if (ModInit.conf.initPlugins.tracks)
                plugins.Add(new("{localhost}/tracks.js", 1, "Tracks.js", "lampac"));

            if (ModInit.conf.initPlugins.transcoding)
                plugins.Add(new("{localhost}/transcoding.js", 1, "Transcoding video", "lampac"));

            if (ModInit.conf.initPlugins.tmdbProxy)
                plugins.Add(new("{localhost}/tmdbproxy.js", 1, "TMDB Proxy", "lampac"));

            if (ModInit.conf.initPlugins.cubProxy)
                plugins.Add(new("{localhost}/cubproxy.js", 1, "CUB Proxy", "lampac"));

            if (ModInit.conf.initPlugins.online)
                plugins.Add(new("{localhost}/online.js", 1, "Онлайн", "lampac"));

            if (ModInit.conf.initPlugins.watch_together)
                plugins.Add(new("{localhost}/watchtogether.js", 1, "Watch Together", "lampac"));

            if (ModInit.conf.initPlugins.catalog)
                plugins.Add(new("{localhost}/catalog.js", 1, "Альтернативные источники каталога", "lampac"));

            if (ModInit.conf.initPlugins.dorama)
                plugins.Add(new("{localhost}/dorama.js", 1, "Дорамы", "lampac"));

            if (ModInit.conf.initPlugins.sisi)
            {
                plugins.Add(new("{localhost}/sisi.js", 1, "Клубничка", "lampac"));
                plugins.Add(new("{localhost}/startpage.js", 1, "Стартовая страница", "lampac"));
            }

            if (ModInit.conf.initPlugins.sync)
                plugins.Add(new("{localhost}/sync.js", 1, "Синхронизация", "lampac"));

            if (ModInit.conf.initPlugins.timecode)
                plugins.Add(new("{localhost}/timecode.js", 1, "Синхронизация тайм-кодов", "lampac"));

            if (ModInit.conf.initPlugins.bookmark)
                plugins.Add(new("{localhost}/bookmark.js", 1, "Синхронизация закладок", "lampac"));

            if (ModInit.conf.initPlugins.torrserver)
                plugins.Add(new("{localhost}/ts.js", 1, "TorrServer", "lampac"));

            if (ModInit.conf.initPlugins.backup)
                plugins.Add(new("{localhost}/backup.js", 1, "Backup", "lampac"));

            if (ModInit.conf.customPlugins != null)
            {
                foreach (var p in ModInit.conf.customPlugins)
                {
                    if (p.status == 1)
                        plugins.Add(p);
                }
            }

            // qdl 2.82: сама подстановка {initiale} уехала НИЖЕ — после вклейки lampainit-invc.js.
            // Причина: наш файл тоже использует этот список (догоняющая установка плагинов), а
            // вклеивается он позже, и замена, сделанная здесь, до него бы не доехала.
            #endregion

            if (ModInit.conf.initPlugins.pirate_store)
            {
                string storejs = FileCache.ReadAllText($"{ModInit.modpath}/plugins/pirate_store.js", "pirate_store.js");
                if (storejs.Contains("{country}"))
                    StatiCacheDisabled = true;

                sb = sb.Replace("{pirate_store}", storejs);
            }

            if (CoreInit.conf.accsdb.enable)
            {
                string denyjs = FileCache.ReadAllText($"{ModInit.modpath}/plugins/deny.js", "deny.js");
                if (denyjs.Contains("{country}"))
                    StatiCacheDisabled = true;

                if (denyjs.Contains("{cubMesage}"))
                    denyjs = denyjs.Replace("{cubMesage}", CoreInit.conf.accsdb.authMesage);

                sb = sb.Replace("{deny}", denyjs);
            }

            string initinvcjs = FileCache.ReadAllText($"{ModInit.modpath}/plugins/lampainit-invc.js", "lampainit-invc.js");
            if (initinvcjs.Contains("{country}"))
                StatiCacheDisabled = true;

            sb = sb.Replace("{lampainit-invc}", initinvcjs);
            // 🔴 ПОСЛЕ вклейки invc и ДО замены {localhost}: список приезжает с плейсхолдерами
            // хоста внутри, их чинит общая замена ниже.
            sb = sb.Replace("{initiale}", JsonConvert.SerializeObject(plugins));
            sb = sb.Replace("{country}", requestInfo.Country ?? string.Empty);
            sb = sb.Replace("{localhost}", host);
            // qdl 2.45: адресные ?v для наших плагинов ДО общей замены — они получают версию по
            // содержимому файла, а не по тикам старта процесса (см. PluginVersion). Остальные
            // {version} остаются на cacheVersion: их тела зависят от init.conf/страны и вечный
            // клиентский кеш там опасен.
            sb = sb.Replace("/qdl.js?v={version}", "/qdl.js?v=" + PluginVersion(ModuleFile("QbitDownload", "plugins/qdl.js")));
            sb = sb.Replace("/music.js?v={version}", "/music.js?v=" + PluginVersion(ModuleFile("Music", "plugin.js")));
            sb = sb.Replace("/desktop.js?v={version}", "/desktop.js?v=" + PluginVersion(ModuleFile("QbitDownload", "plugins/desktop.js")));
            sb = sb.Replace("{version}", cacheVersion);
            sb = sb.Replace("{deny}", string.Empty);
            sb = sb.Replace("{pirate_store}", string.Empty);

            sb = sb.Replace("{ major: 0, minor: 0 }", $"{{major: 2, minor: 1}}");

            if (ModInit.conf.initPlugins.jacred)
                sb = sb.Replace("{jachost}", Regex.Replace(host, "^https?://", ""));
            else
                sb = sb.Replace("{jachost}", "jac.red");

            #region full_btn_priority_hash
            var onlineModule = CoreInit.modules?.FirstOrDefault(m => m != null && string.Equals(m.name, "Online", StringComparison.OrdinalIgnoreCase));
            string online_plugin_path = string.IsNullOrWhiteSpace(onlineModule?.path)
                ? null
                : IO.Path.Combine(onlineModule.path, "plugin.js");

            string online_version = string.IsNullOrEmpty(online_plugin_path)
                ? string.Empty
                : Regex.Match(FileCache.ReadAllText(online_plugin_path, "online.js"), "version: '([^']+)'").Groups[1].Value;

            string LampaUtilshash(string input)
            {
                if (!CoreInit.conf.online.version)
                    input = input.Replace($"v{online_version}", "");

                string str = input ?? string.Empty;
                int hash = 0;

                if (str.Length == 0)
                    return hash.ToString();

                for (int i = 0; i < str.Length; i++)
                {
                    int _char = str[i];

                    hash = (hash << 5) - hash + _char;
                    hash = hash & hash; // Преобразование в 32-битное целое число
                }

                return Math.Abs(hash).ToString();
            }

            string full_btn_priority_hash = LampaUtilshash($"<div class=\"full-start__button selector view--online lampac--button\" data-subtitle=\"{CoreInit.conf.online.name} v{online_version}\">\n        <svg xmlns=\"http://www.w3.org/2000/svg\" version=\"1.1\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" viewBox=\"0 0 392.697 392.697\" xml:space=\"preserve\">\n            <path d=\"M21.837,83.419l36.496,16.678L227.72,19.886c1.229-0.592,2.002-1.846,1.98-3.209c-0.021-1.365-0.834-2.592-2.082-3.145\n                L197.766,0.3c-0.903-0.4-1.933-0.4-2.837,0L21.873,77.036c-1.259,0.559-2.073,1.803-2.081,3.18\n                C19.784,81.593,20.584,82.847,21.837,83.419z\" fill=\"currentColor\"></path>\n            <path d=\"M185.689,177.261l-64.988-30.01v91.617c0,0.856-0.44,1.655-1.167,2.114c-0.406,0.257-0.869,0.386-1.333,0.386\n                c-0.368,0-0.736-0.082-1.079-0.244l-68.874-32.625c-0.869-0.416-1.421-1.293-1.421-2.256v-92.229L6.804,95.5\n                c-1.083-0.496-2.344-0.406-3.347,0.238c-1.002,0.645-1.608,1.754-1.608,2.944v208.744c0,1.371,0.799,2.615,2.045,3.185\n                l178.886,81.768c0.464,0.211,0.96,0.315,1.455,0.315c0.661,0,1.318-0.188,1.892-0.555c1.002-0.645,1.608-1.754,1.608-2.945\n                V180.445C187.735,179.076,186.936,177.831,185.689,177.261z\" fill=\"currentColor\"></path>\n            <path d=\"M389.24,95.74c-1.002-0.644-2.264-0.732-3.347-0.238l-178.876,81.76c-1.246,0.57-2.045,1.814-2.045,3.185v208.751\n                c0,1.191,0.606,2.302,1.608,2.945c0.572,0.367,1.23,0.555,1.892,0.555c0.495,0,0.991-0.104,1.455-0.315l178.876-81.768\n                c1.246-0.568,2.045-1.813,2.045-3.185V98.685C390.849,97.494,390.242,96.384,389.24,95.74z\" fill=\"currentColor\"></path>\n            <path d=\"M372.915,80.216c-0.009-1.377-0.823-2.621-2.082-3.18l-60.182-26.681c-0.938-0.418-2.013-0.399-2.938,0.045\n                l-173.755,82.992l60.933,29.117c0.462,0.211,0.958,0.316,1.455,0.316s0.993-0.105,1.455-0.316l173.066-79.092\n                C372.122,82.847,372.923,81.593,372.915,80.216z\" fill=\"currentColor\"></path>\n        </svg>\n\n        <span>Онлайн</span>\n    </div>");

            sb = sb.Replace("{full_btn_priority_hash}", full_btn_priority_hash)
                   .Replace("{btn_priority_forced}", CoreInit.conf.online.btn_priority_forced ? "true" : "false");
            #endregion

            #region domain token
            if (!string.IsNullOrEmpty(CoreInit.conf.accsdb.domainId_pattern))
            {
                string token = Regex.Match(HttpContext.Request.Host.Host, CoreInit.conf.accsdb.domainId_pattern).Groups[1].Value;
                sb = sb.Replace("{token}", token);
            }
            else
            {
                sb = sb.Replace("{token}", string.Empty);
            }
            #endregion

            return ContentTo(sb, "application/javascript; charset=utf-8");
        }
        finally
        {
            StringBuilderPool.Return(sb);
        }
    }
    #endregion

    #region on.js
    [HttpGet, AllowAnonymous]
    [Staticache(20, always: true, setHeadersNoCache: true)]
    [Route("on.js")]
    [Route("on/js/{token}")]
    [Route("on/h/{token}")]
    [Route("on/{token}")]
    public ActionResult LamOnInit(string token, bool adult = true)
    {
        var sb = StringBuilderPool.Rent();

        try
        {
            #region plugins
            if (adult && HttpContext.Request.Path.Value.StartsWith("/on/h/"))
                adult = false;

            var plugins = new List<string>(15);

            void send(string name, bool worktoken)
            {
                if (worktoken && !string.IsNullOrEmpty(token))
                {
                    plugins.Add($"\"{{localhost}}/{name}/js/{HttpUtility.UrlEncode(token)}\"");
                }
                else
                {
                    plugins.Add($"\"{{localhost}}/{name}.js\"");
                }
            }

            if (ModInit.conf.initPlugins.dlna)
                send("dlna", true);

            if (ModInit.conf.initPlugins.tracks)
                send("tracks", true);

            if (ModInit.conf.initPlugins.transcoding)
                send("transcoding", true);

            if (ModInit.conf.initPlugins.tmdbProxy)
                send("tmdbproxy", true);

            if (ModInit.conf.initPlugins.cubProxy)
                send("cubproxy", true);

            if (ModInit.conf.initPlugins.dorama)
                send("dorama", true);

            if (ModInit.conf.initPlugins.online)
                send("online", true);

            if (ModInit.conf.initPlugins.watch_together)
                send("watchtogether", false);

            if (adult && ModInit.conf.initPlugins.sisi)
            {
                send("sisi", true);
                send("startpage", false);
            }

            if (ModInit.conf.initPlugins.sync)
                send("sync", true);

            if (ModInit.conf.initPlugins.timecode)
                send("timecode", true);

            if (ModInit.conf.initPlugins.bookmark)
                send("bookmark", true);

            if (ModInit.conf.initPlugins.torrserver)
                send("ts", true);

            if (ModInit.conf.initPlugins.backup)
                send("backup", true);

            if (ModInit.conf.customPlugins != null)
            {
                foreach (var p in ModInit.conf.customPlugins)
                {
                    if (p.status == 1)
                        plugins.Add($"\"{p.url}\"");
                }
            }
            #endregion

            string onjs = FileCache.ReadAllText($"{ModInit.modpath}/plugins/on.js", "on.js");
            if (string.IsNullOrEmpty(onjs))
                return BadRequest();

            if (onjs.Contains("{country}"))
                StatiCacheDisabled = true;

            sb = sb.Append(onjs)
                .Replace("{plugins}", plugins.Count == 0 ? string.Empty : string.Join(",", plugins))
                .Replace("{country}", requestInfo.Country ?? string.Empty)
                .Replace("{localhost}", host);

            return ContentTo(sb, "application/javascript; charset=utf-8");
        }
        finally
        {
            StringBuilderPool.Return(sb);
        }
    }
    #endregion

    #region privateinit.js
    [HttpGet]
    [Route("privateinit.js")]
    public ActionResult PrivateInit()
    {
        SetHeadersNoCache();

        var user = requestInfo.user;
        if (user == null || user.ban || DateTime.UtcNow > user.expires)
            return Content(string.Empty, "application/javascript; charset=utf-8");

        var sb = StringBuilderPool.Rent();

        try
        {
            string privateinit = FileCache.ReadAllText($"{ModInit.modpath}/plugins/privateinit.js", "privateinit.js");

            sb.Append(privateinit)
              .Replace("{country}", requestInfo.Country ?? string.Empty)
              .Replace("{localhost}", host)
              .Replace("{jachost}", ModInit.conf.initPlugins.jacred ? Regex.Replace(host, "^https?://", "") : "jac.red");

            return ContentTo(sb, "application/javascript; charset=utf-8");
        }
        finally
        {
            StringBuilderPool.Return(sb);
        }
    }
    #endregion

    #region telegram_auth_gate.js
    [HttpGet, AllowAnonymous, Staticache(manually: true)]
    [Route("telegram_auth_gate.js")]
    public ActionResult TelegramAuthGate()
    {
        SetHeadersNoCache();

        string gate = FileCache.ReadAllText($"{ModInit.modpath}/plugins/telegram_auth_gate.js", "telegram_auth_gate.js")
            .Replace("{country}", requestInfo.Country)
            .Replace("{localhost}", host);

        return ContentTo(gate, "application/javascript; charset=utf-8");
    }
    #endregion

    #region dorama.js
    [HttpGet, AllowAnonymous, Staticache(manually: true)]
    [Route("dorama.js")]
    [Route("dorama/js/{token}")]
    public ActionResult Dorama(string token)
    {
        SetHeadersNoCache();

        string plugin = FileCache.ReadAllText($"{ModInit.modpath}/plugins/dorama.js", "dorama.js")
            .Replace("{localhost}", host)
            .Replace("{token}", HttpUtility.UrlEncode(token ?? string.Empty));

        return ContentTo(plugin, "application/javascript; charset=utf-8");
    }
    #endregion
}
