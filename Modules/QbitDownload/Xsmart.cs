using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace QbitDownload;

// ─────────────────────────────────────────────────────────────────────────────
// Скачивание из раздела «XSMART» в общий раздел «Загрузки» — сетевой слой и имена.
//
// 🔥 Мы НЕ ходим в XSMART напрямую и НЕ знаем ни одного его хоста. Всё идёт через свой
// контейнер xsmart-proxy (сеть media, порт 9140): он держит сессию по нашему device id,
// резолвит потоки и проксирует байты. Это не «лишний хоп»:
//   • ссылки XSMART живут ~15 минут, а токен прокси несёт РЕЦЕПТ и перерезолвит поток
//     сам, посреди двухчасового фильма — качалке достаточно повторить запрос;
//   • дисциплина одного device id остаётся в одном месте (CONTRACT.md §3.3).
// Периметр контейнера нас пропускает: маркер X-D1V-Edge ставит только Caddy на внешних
// запросах, а изнутри сети media его нет (xsmart/service/src/access.js).
//
// ⚠️ ИНВАРИАНТ ИЗОЛЯЦИИ (пояс 1, как у jut.su): links/<hash>.json для xsmart НЕ создаём
// НИКОГДА. Без этого файла WatchAdd (Controller.cs) отвечает {"success":false,"no link"},
// то есть добавить такой тайтл в ТОРРЕНТНУЮ охоту физически невозможно.
//
// Контракт прокси — E:\Media-server\xsmart\service\CONTRACT.md
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Что качаем: серию сериала или фильм целиком.</summary>
enum XsmartKind { Episode, Film }

/// <summary>
/// Единица скачивания.
/// 🔴 Номер и ИДЕНТИФИКАТОР — разные вещи, и путать их нельзя. XSMART адресует серию
/// внутренними id (<c>?s=32215&amp;e=524438</c>), а человеку нужен порядковый номер
/// (сезон 1, серия 5). В ИМЯ ФАЙЛА идёт номер (иначе на экране «s32215e524438»),
/// в КЛЮЧ ТАЙМЛАЙНА — id (его строит плагин, и прогресс обязан совпасть).
/// </summary>
sealed class XsmartEp
{
    public XsmartKind kind = XsmartKind.Episode;
    public int seasonNo = 1, epNo;
    public string seasonId, epId;
    public string name;
    public bool playable = true;

    /// <summary>Ключ дедупа очереди и отметки «уже скачано» — по НОМЕРАМ (как имя файла).</summary>
    public string epkey => kind == XsmartKind.Film ? "film" : "s" + seasonNo + "e" + epNo;
}

sealed class XsmartTitle
{
    public int cat;
    public string id;
    public string title, titleOrig, poster, descr;
    public int year;
    public bool series;
    public string source;                       // источник XSMART (кино 2/14, сериалы 3)
    public List<XsmartEp> items = new();
}

/// <summary>Резолв одной единицы: наш URL потока + фактическое качество.</summary>
sealed class XsmartStream
{
    public string url;          // абсолютный адрес на xsmart-proxy
    public int quality;         // 0 = «Авто» (мастер-плейлист), иначе высота
    public string error;        // NO_STREAM | UPSTREAM_DOWN | …
}

static class XsmartNet
{
    #region адрес, клиенты, идентичность

    public static bool On => ModInit.conf?.xsmartEnable ?? true;

    public static string Api => (ModInit.conf?.xsmartApi ?? "http://xsmart-proxy:9140").TrimEnd('/');

    static readonly HttpClient _api = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };

    // ⚠️ Timeout.InfiniteTimeSpan обязателен (та же грабля, что у jut, §AL): общий таймаут
    // HttpClient рвёт УЖЕ ИДУЩУЮ отдачу тела, и 489-МиБ файл умирал бы на 100-й секунде.
    // Зависшее соединение ловится idle-токеном на каждом прочитанном чанке, а не здесь.
    static readonly HttpClient _media = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

    public static HttpClient Media => _media;

    public static string DataDir()
        => Path.Combine(ModInit.conf?.cachePath ?? "/qdl-data", "xsmart");

    /// <summary>
    /// Псевдо-infohash не-торрентного источника: 40 hex, проходит ValidHash → карточка
    /// бесплатно живёт в общем разделе «Загрузки» (/qdl/list, /qdl/stream, /qdl/episodes…).
    /// Соль «xsmart:» не пересекается с «jutsu:», поэтому столкновений с аниме не бывает.
    /// </summary>
    public static string Hash(int cat, string id)
    {
        using var sha = SHA1.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes("xsmart:" + cat + ":" + id)))
                      .ToLowerInvariant();
    }

    /// <summary>Читаемый ключ тайтла: имя папки, ключ подписки, префикс имён файлов.</summary>
    public static string Ref(int cat, string id) => cat + "-" + id;

    static readonly Regex _idRx = new(@"^[0-9]{1,12}$", RegexOptions.Compiled);
    static readonly Regex _srcRx = new(@"^[0-9]{1,3}$", RegexOptions.Compiled);

    // Список категорий — из вшитой таксономии прокси (CONTRACT.md §2.3). Гейт нужен не ради
    // красоты: cat уходит в ИМЯ ПАПКИ на диске и в соль хеша.
    static readonly int[] _cats = { 2, 3, 4, 5, 6, 7, 9, 10, 11, 12 };

    public static bool ValidId(string id) => !string.IsNullOrEmpty(id) && _idRx.IsMatch(id);
    public static bool ValidCat(int cat) => Array.IndexOf(_cats, cat) >= 0;
    public static bool Valid(int cat, string id) => ValidCat(cat) && ValidId(id);

    /// <summary>Источник XSMART: только цифры, уходит в query к прокси.</summary>
    public static bool ValidSource(string s) => string.IsNullOrEmpty(s) || _srcRx.IsMatch(s);

    internal static void Log(string tag, string msg)
        => Console.WriteLine("[QbitDownload] xsmart/" + tag + ": " + msg);

    #endregion

    #region запросы к прокси

    /// <summary>
    /// GET JSON у прокси. Исключение не бросаем: у всех вызывающих есть осмысленный путь
    /// «источник молчит» (кеш, пропуск тика слежения), а исключение его бы съело.
    /// </summary>
    public static async Task<JObject> GetJson(string path)
    {
        try
        {
            using var resp = await _api.GetAsync(Api + path);
            string body = await resp.Content.ReadAsStringAsync();
            // 404 с {"ok":false,…} — штатный ответ контракта, разбирать его умеет вызывающий.
            return JObject.Parse(body);
        }
        catch (Exception ex)
        {
            Log("api", path + " → " + ex.Message);
            return null;
        }
    }

    /// <summary>null = всё хорошо; иначе код ошибки контракта (для тоста клиенту).</summary>
    public static string ErrOf(JObject jo)
    {
        if (jo == null) return "UPSTREAM_DOWN";
        if (jo.Value<bool?>("ok") == true) return null;
        return jo.Value<string>("code") ?? "UPSTREAM_DOWN";
    }

    #endregion

    #region карточка тайтла и список серий

    /// <summary>
    /// Карточка + полный список единиц скачивания (все сезоны сериала или один фильм).
    /// Сетевая цена честная: 1 запрос на кино, 2 + N на сериал с N сезонами. Поэтому
    /// результат кладётся в кеш вызывающим (XsmartTitleFromCache) — «Скачать» с открытой
    /// карточки не должно платить за сеть второй раз.
    /// </summary>
    public static async Task<(XsmartTitle title, string error)> LoadTitle(int cat, string id, string source = null)
    {
        var jo = await GetJson("/xsmart/item/" + cat + "/" + Uri.EscapeDataString(id));
        string err = ErrOf(jo);
        if (err != null) return (null, err);
        if (jo["item"] is not JObject it) return (null, "UPSTREAM_EMPTY");

        var t = new XsmartTitle
        {
            cat = cat,
            id = id,
            title = it.Value<string>("title"),
            titleOrig = it.Value<string>("titleOrig"),
            year = it.Value<int?>("year") ?? 0,
            poster = it.Value<string>("poster"),
            descr = it.Value<string>("description"),
            series = it.Value<string>("type") == "series",
            source = !string.IsNullOrEmpty(source) ? source : it.Value<string>("defaultSource")
        };

        if (!t.series)
        {
            t.items.Add(new XsmartEp { kind = XsmartKind.Film, epNo = 1 });
            return (t, null);
        }

        string q = string.IsNullOrEmpty(t.source) ? "" : "?source=" + Uri.EscapeDataString(t.source);
        var sj = await GetJson("/xsmart/seasons/" + cat + "/" + Uri.EscapeDataString(id) + q);
        err = ErrOf(sj);
        if (err != null) return (null, err);
        if (!string.IsNullOrEmpty(sj.Value<string>("source"))) t.source = sj.Value<string>("source");

        var seasons = (sj["seasons"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
        if (seasons.Count == 0) return (null, "UPSTREAM_EMPTY");

        for (int i = 0; i < seasons.Count; i++)
        {
            string sid = seasons[i].Value<string>("id");
            if (string.IsNullOrEmpty(sid)) continue;
            int sno = seasons[i].Value<int?>("number") ?? (i + 1);
            t.items.AddRange(await LoadEpisodes(cat, id, sid, sno, t.source));
        }

        return t.items.Count > 0 ? (t, null) : (null, "UPSTREAM_EMPTY");
    }

    /// <summary>Серии одного сезона. Отдельным методом — им же ходит суточный тик слежения.</summary>
    public static async Task<List<XsmartEp>> LoadEpisodes(int cat, string id, string seasonId, int seasonNo,
                                                         string source)
    {
        var res = new List<XsmartEp>();
        string url = "/xsmart/episodes/" + cat + "/" + Uri.EscapeDataString(id)
                   + "?season=" + Uri.EscapeDataString(seasonId)
                   + (string.IsNullOrEmpty(source) ? "" : "&source=" + Uri.EscapeDataString(source));

        var ej = await GetJson(url);
        if (ErrOf(ej) != null) return res;
        if (ej["episodes"] is not JArray arr) return res;

        int n = 0;
        foreach (var e in arr.OfType<JObject>())
        {
            string eid = e.Value<string>("id");
            if (string.IsNullOrEmpty(eid)) continue;
            n++;
            res.Add(new XsmartEp
            {
                kind = XsmartKind.Episode,
                seasonNo = seasonNo,
                epNo = e.Value<int?>("number") ?? n,
                seasonId = seasonId,
                epId = eid,
                name = e.Value<string>("title"),
                // ⚠️ playable:false — узел ветки VCDN, который на нашей подписке не резолвится
                // вовсе. Ставить такое в очередь значит гарантированно получить NO_STREAM
                // и «ошибку» в статусе там, где качать было нечего.
                playable = e.Value<bool?>("playable") ?? true
            });
        }
        return res;
    }

    #endregion

    #region резолв потока

    /// <summary>
    /// Резолв единицы → НАШ адрес потока и качество.
    ///
    /// 🔴 Берём <c>default</c> из ответа прокси, а не «самую большую цифру в списке».
    /// Политика «максимум» уже закодирована там (CONTRACT.md §2.9): дорожка «Авто» — это
    /// мастер-плейлист, и его потолок ВЫШЕ именованных (в мастере есть 1080p, среди
    /// именованных максимум «Высокое» = 720p). Считать максимум самим значило бы выбрать
    /// 720p вместо 1080p — ровно наоборот к требованию владельца «всегда максимум».
    /// </summary>
    public static async Task<XsmartStream> Resolve(int cat, string id, XsmartEp e, string source)
    {
        string url = "/xsmart/resolve/" + cat + "/" + Uri.EscapeDataString(id);
        var qs = new List<string>();
        if (!string.IsNullOrEmpty(source)) qs.Add("source=" + Uri.EscapeDataString(source));
        if (e.kind == XsmartKind.Episode)
        {
            qs.Add("season=" + Uri.EscapeDataString(e.seasonId ?? ""));
            qs.Add("episode=" + Uri.EscapeDataString(e.epId ?? ""));
        }
        if (qs.Count > 0) url += "?" + string.Join("&", qs);

        var jo = await GetJson(url);
        string err = ErrOf(jo);
        if (err != null) return new XsmartStream { error = err };

        if (jo["variants"] is not JArray variants || variants.Count == 0)
            return new XsmartStream { error = "NO_STREAM" };

        var def = jo["default"] as JObject;
        int vi = def?.Value<int?>("variant") ?? 0;
        int ti = def?.Value<int?>("track") ?? 0;
        if (vi < 0 || vi >= variants.Count) vi = 0;

        if (variants[vi]?["tracks"] is not JArray tracks || tracks.Count == 0)
            return new XsmartStream { error = "NO_STREAM" };
        if (ti < 0 || ti >= tracks.Count) ti = 0;

        string rel = tracks[ti]?.Value<string>("url");
        // Адрес обязан быть НАШИМ относительным путём. Абсолютный означал бы, что прокси
        // выпустил наружу ссылку CDN, — качать по ней мы всё равно не станем (инвариант №1).
        if (string.IsNullOrEmpty(rel) || !rel.StartsWith("/xsmart/stream/", StringComparison.Ordinal))
            return new XsmartStream { error = "NO_STREAM" };

        return new XsmartStream
        {
            url = Api + rel,
            quality = tracks[ti]?.Value<int?>("quality") ?? 0
        };
    }

    #endregion
}
