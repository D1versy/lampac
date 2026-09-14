using Newtonsoft.Json.Linq;
using Npgsql;
using Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace QbitDownload;

// ─────────────────────────────────────────────────────────────────────────────
// Псевдонимы названий (qdl 2.118).
//
// Зачем: трекеры спрашиваются РУССКИМ именем карточки с TMDB (qdl.js шлёт query=title, у JacRed
// search_lang="query"). Когда трекеры знают тайтл под другим переводом, выдача с трекеров — ноль,
// и в карточке остаётся один bitmagnet (он ищет по TMDB id, имя ему не нужно). Живой пример:
// «Истребитель демонов: Бесконечный замок» (tmdb 1311031) — на rutracker/nnmclub он «Клинок,
// рассекающий демонов: Бесконечный замок», русская раздача с 189 сидами лежала в одном запросе от
// нас. А title_original иероглифами после SearchNameTo.Convert — пустая строка, то есть бесполезен
// и как запрос, и как эталон имени. Разбор — медиасервер claude/06 §DW.
//
// Что делает файл:
//  • резолвер псевдонимов карточки: память → БД (title_alias) → TMDB (alternative_titles + en-US
//    title) → Shikimori (аниме/дунхуа: russian/name/english/synonyms/license_name_ru);
//  • журнал промахов (title_miss): карточки, где трекеры дали 0 даже с псевдонимами, — рабочий
//    список периодического прогона Claude (/title-aliases в репо медиасервера) и вкладки «Названия»;
//  • данные для админки и строки хелса.
//
// Инварианты:
//  1. Сеть (TMDB/Shikimori) — ТОЛЬКО когда трекеры дали ноль строк (см. SearchScored) или по кнопке
//     «перечитать» в админке. Обычная карточка резолвера не видит вовсе.
//  2. Ни один сбой (БД, TMDB, Shikimori) не роняет и не задерживает поиск: везде fail-open, пустой
//     список в худшем случае. Без БД (реплика) псевдонимы живут в памяти процесса.
//  3. Гейты имени становятся ШИРЕ по числу эталонов, но не мягче: сравнение остаётся точным
//     равенством/вхождением нормализованной строки (см. TorrentScoring.Score, NameMatchesSeries).
//  4. Промах пишется только когда индексатор ЖИВ (авария ≠ промах) и только по TMDB id: без id
//     резолвить нечего.
//
// Чистые функции (AliasesFromTmdb, AliasesFromShikimori, AliasesMerge, AliasesForSearch,
// ShikiKindOk) — без IO, под тестами TitleAliasesTests.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Ещё одно имя карточки, под которым её знают трекеры.</summary>
public sealed class TitleAlias
{
    public string alias;    // как есть — этим ищем
    public string norm;     // SearchNameTo.Convert(alias) — этим сверяем имя
    public string source;   // tmdb | shikimori | claude | manual
    public string note;
    public int hits;
    public DateTime? addedAt;

    public const string SrcTmdb = "tmdb", SrcShikimori = "shikimori", SrcClaude = "claude", SrcManual = "manual";

    public static TitleAlias Make(string alias, string source, string note = null)
    {
        string a = (alias ?? "").Trim();
        if (a.Length == 0) return null;
        string n = Shared.Services.Utilities.SearchNameTo.Convert(a);
        if (string.IsNullOrEmpty(n)) return null;
        return new TitleAlias { alias = a, norm = n, source = source ?? SrcManual, note = note };
    }

    public JObject ToJson() => new JObject
    {
        ["alias"] = alias, ["norm"] = norm, ["source"] = source, ["note"] = note, ["hits"] = hits,
        ["added_at"] = addedAt?.ToString("o")
    };
}

public partial class QbitController
{
    #region чистые функции

    // Языки, у которых оригинал — не латиница: только у них romaji из TMDB и Shikimori имеют смысл.
    static readonly HashSet<string> _asianLangs = new(StringComparer.OrdinalIgnoreCase) { "ja", "zh", "ko", "cn" };
    static readonly Regex _cyrRx = new(@"[а-яё]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool AliasLooksAsian(string originalLanguage)
        => !string.IsNullOrEmpty(originalLanguage) && _asianLangs.Contains(originalLanguage);

    /// <summary>
    /// Псевдонимы из ответа TMDB (детали с append_to_response=alternative_titles, language=ru) плюс
    /// английское название (отдельный запрос language=en-US; null = не спрашивали).
    /// Порядок = приоритет для поиска: русские записи → русский title TMDB → английское → romaji.
    /// Romaji берётся ТОЛЬКО когда оригинал карточки не нормализуется (иероглифы): для латинского
    /// оригинала это шум, а у японского это единственное имя, которое знают nnmclub/rutracker.
    /// </summary>
    public static List<TitleAlias> AliasesFromTmdb(JObject ruDetail, string enTitle, string cardTitle, string cardOriginal)
    {
        var list = new List<TitleAlias>();
        if (ruDetail == null) return list;

        // фильм: alternative_titles.titles[]; сериал: alternative_titles.results[]
        var alt = ruDetail["alternative_titles"] as JObject;
        var arr = (alt?["titles"] as JArray) ?? (alt?["results"] as JArray) ?? new JArray();

        foreach (var t in arr.OfType<JObject>())
            if (string.Equals(t.Value<string>("iso_3166_1"), "RU", StringComparison.OrdinalIgnoreCase))
                Add(list, t.Value<string>("title"), TitleAlias.SrcTmdb, "RU alternative_titles");

        Add(list, ruDetail.Value<string>("title") ?? ruDetail.Value<string>("name"), TitleAlias.SrcTmdb, "TMDB ru title");
        Add(list, enTitle, TitleAlias.SrcTmdb, "TMDB en title");

        bool originalOpaque = string.IsNullOrEmpty(Shared.Services.Utilities.SearchNameTo.Convert(cardOriginal))
                              || AliasLooksAsian(ruDetail.Value<string>("original_language"));
        if (originalOpaque)
        {
            foreach (var t in arr.OfType<JObject>())
            {
                string type = t.Value<string>("type") ?? "";
                if (type.IndexOf("roma", StringComparison.OrdinalIgnoreCase) >= 0)
                    Add(list, t.Value<string>("title"), TitleAlias.SrcTmdb, "romaji");
            }
        }

        return list;
    }

    /// <summary>Псевдонимы из выбранного кандидата Shikimori и его деталей (/api/animes/{id}; null = не спрашивали).</summary>
    public static List<TitleAlias> AliasesFromShikimori(JutAnimeCandidate pick, JObject detail)
    {
        var list = new List<TitleAlias>();
        if (pick == null) return list;

        Add(list, pick.russian, TitleAlias.SrcShikimori, "shikimori russian");
        Add(list, detail?.Value<string>("license_name_ru"), TitleAlias.SrcShikimori, "shikimori license_name_ru");
        Add(list, pick.name, TitleAlias.SrcShikimori, "shikimori romaji");
        foreach (var e in (detail?["english"] as JArray)?.Select(x => x.Value<string>()) ?? Enumerable.Empty<string>())
            Add(list, e, TitleAlias.SrcShikimori, "shikimori english");
        foreach (var s in (detail?["synonyms"] as JArray)?.Select(x => x.Value<string>()) ?? Enumerable.Empty<string>())
            Add(list, s, TitleAlias.SrcShikimori, "shikimori synonym");
        return list;
    }

    // Фильм ищем среди фильмов, сериал — среди сериальных типов; иначе Pick сравнивает
    // «Бесконечный замок» с одноимённым спешлом или сиквелом.
    public static bool ShikiKindOk(string kind, bool tv)
    {
        string k = (kind ?? "").ToLowerInvariant();
        if (k.Length == 0) return true;   // тип не заявлен — не отсекаем
        return tv ? k is "tv" or "ona" or "ova" or "special" or "tv_special"
                  : k is "movie";
    }

    /// <summary>
    /// Слияние списков: порядок первого вхождения, дедуп по норме, имена самой карточки и слишком
    /// короткие (норма &lt; 3) выкидываются, кап сверху.
    /// </summary>
    public static List<TitleAlias> AliasesMerge(IEnumerable<TitleAlias> src, string cardTitleNorm, string cardOriginalNorm, int cap = 8)
    {
        var res = new List<TitleAlias>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in src ?? Enumerable.Empty<TitleAlias>())
        {
            if (a == null || string.IsNullOrEmpty(a.norm) || a.norm.Length < 3) continue;
            if (a.norm == cardTitleNorm || a.norm == cardOriginalNorm) continue;
            if (!seen.Add(a.norm)) continue;
            res.Add(a);
            if (res.Count >= cap) break;
        }
        return res;
    }

    /// <summary>
    /// Кем добирать трекеры, когда основной запрос дал ноль. Ручные и подтверждённые прогоном идут
    /// первыми (их уже проверяли живым запросом), затем русские (трекеры русскоязычные), затем
    /// латиница. Не больше max — каждый псевдоним = веер по всем трекерам.
    /// </summary>
    public static List<TitleAlias> AliasesForSearch(IEnumerable<TitleAlias> aliases, int max)
    {
        if (max <= 0) return new List<TitleAlias>();
        int Rank(TitleAlias a)
        {
            if (a.source is TitleAlias.SrcClaude or TitleAlias.SrcManual) return 0;
            if (_cyrRx.IsMatch(a.alias ?? "")) return 1;
            return 2;
        }
        return (aliases ?? Enumerable.Empty<TitleAlias>())
            .Where(a => a != null && !string.IsNullOrEmpty(a.alias))
            .Select((a, i) => (a, i))
            .OrderBy(x => Rank(x.a)).ThenBy(x => x.i)
            .Select(x => x.a)
            .Take(max)
            .ToList();
    }

    static void Add(List<TitleAlias> list, string alias, string source, string note)
    {
        var a = TitleAlias.Make(alias, source, note);
        if (a != null) list.Add(a);
    }

    static string AliasKey(int tmdb, bool tv) => (tv ? "t" : "m") + tmdb;

    #endregion

    #region память

    internal static bool TitleAliasesOn => ModInit.conf?.titleAliases != false;

    // Ключ → (список, когда положили). TTL 6 ч: БД перечитывается редко, сеть — только по правилам выше.
    static readonly ConcurrentDictionary<string, (List<TitleAlias> list, DateTime at)> _aliasMem = new();
    static readonly ConcurrentDictionary<string, Task<List<TitleAlias>>> _aliasInflight = new();
    const int AliasMemHours = 6;

    /// <summary>Нормы псевдонимов из памяти — для скоринга и охоты (синхронно, без IO). Пусто = не знаем.</summary>
    internal static List<string> AliasNormsCached(int tmdb, bool tv)
    {
        if (tmdb <= 0 || !TitleAliasesOn) return null;
        if (!_aliasMem.TryGetValue(AliasKey(tmdb, tv), out var e) || e.list == null || e.list.Count == 0) return null;
        return e.list.Select(a => a.norm).Where(n => !string.IsNullOrEmpty(n)).ToList();
    }

    static void AliasMemPut(int tmdb, bool tv, List<TitleAlias> list)
        => _aliasMem[AliasKey(tmdb, tv)] = (list ?? new List<TitleAlias>(), DateTime.UtcNow);

    static void AliasMemDrop(int tmdb, bool tv) => _aliasMem.TryRemove(AliasKey(tmdb, tv), out _);

    /// <summary>
    /// Что о карточке уже известно: память → БД. В сеть НЕ ходит никогда — этим греют скоринг
    /// обычные поиски и охота. Пустой список кешируется тоже (иначе каждый поиск ходил бы в БД).
    /// </summary>
    internal static async Task<List<TitleAlias>> AliasesKnown(int tmdb, bool tv)
    {
        if (tmdb <= 0 || !TitleAliasesOn) return new List<TitleAlias>();
        string key = AliasKey(tmdb, tv);
        if (_aliasMem.TryGetValue(key, out var e) && (DateTime.UtcNow - e.at).TotalHours < AliasMemHours)
            return e.list;

        var fromDb = await AliasesDbRead(tmdb, tv);
        // Без БД память не трогаем: там может лежать резолв из сети, и затирать его пустотой нельзя.
        if (fromDb == null) return _aliasMem.TryGetValue(key, out e) ? e.list : new List<TitleAlias>();
        AliasMemPut(tmdb, tv, fromDb);
        return fromDb;
    }

    /// <summary>
    /// Полный резолв: память → БД → TMDB → Shikimori. Сеть — только когда ни память, ни БД ничего
    /// не знают (или refresh). Параллельные запросы одной карточки складываются в один.
    /// </summary>
    internal static Task<List<TitleAlias>> AliasesResolve(int tmdb, bool tv, string title, string original, int year, bool refresh = false)
    {
        if (tmdb <= 0 || !TitleAliasesOn) return Task.FromResult(new List<TitleAlias>());
        string key = AliasKey(tmdb, tv);
        return _aliasInflight.GetOrAdd(key, k => Task.Run(async () =>
        {
            try { return await AliasesResolveCore(tmdb, tv, title, original, year, refresh); }
            catch (Exception ex)
            {
                Console.WriteLine($"[QbitDownload] псевдонимы tmdb={tmdb}: {ex.GetType().Name}: {ex.Message}");
                return _aliasMem.TryGetValue(k, out var e) ? e.list : new List<TitleAlias>();
            }
            finally { _aliasInflight.TryRemove(k, out _); }
        }));
    }

    static async Task<List<TitleAlias>> AliasesResolveCore(int tmdb, bool tv, string title, string original, int year, bool refresh)
    {
        if (!refresh)
        {
            var known = await AliasesKnown(tmdb, tv);
            if (known.Count > 0) return known;
        }

        string cardTitleNorm = Shared.Services.Utilities.SearchNameTo.Convert(title) ?? "";
        string cardOriginalNorm = Shared.Services.Utilities.SearchNameTo.Convert(original) ?? "";
        var found = new List<TitleAlias>();

        // ── TMDB ──
        JObject ru = await TmdbDetail(tmdb, tv, "ru");
        if (ru != null)
        {
            JObject en = await TmdbDetail(tmdb, tv, "en-US");
            string enTitle = en?.Value<string>("title") ?? en?.Value<string>("name");
            found.AddRange(AliasesFromTmdb(ru, enTitle, title, original));
        }

        // ── Shikimori: только аниме/дунхуа ──
        if (ModInit.conf?.titleAliasShikimori != false && ru != null && AliasLooksAsian(ru.Value<string>("original_language")))
        {
            try { found.AddRange(await AliasesFromShikimoriLive(found, title, year, tv)); }
            catch (Exception ex) { Console.WriteLine($"[QbitDownload] псевдонимы tmdb={tmdb}: Shikimori — {ex.GetType().Name}: {ex.Message}"); }
        }

        var merged = AliasesMerge(found, cardTitleNorm, cardOriginalNorm, ModInit.conf?.titleAliasCap ?? 8);

        // Ручные/подтверждённые из БД при refresh не теряем: они главнее автомата.
        List<TitleAlias> result = merged;
        if (refresh)
        {
            var db = await AliasesDbRead(tmdb, tv) ?? new List<TitleAlias>();
            var keep = db.Where(a => a.source is TitleAlias.SrcClaude or TitleAlias.SrcManual).ToList();
            result = AliasesMerge(keep.Concat(merged), cardTitleNorm, cardOriginalNorm, Math.Max(keep.Count, ModInit.conf?.titleAliasCap ?? 8) + keep.Count);
        }

        await AliasesDbWrite(tmdb, tv, merged);
        // После записи перечитываем из БД: там уже могут лежать ручные, плюс hits/добавления.
        var fresh = await AliasesDbRead(tmdb, tv);
        if (fresh != null && fresh.Count > 0) result = fresh;

        AliasMemPut(tmdb, tv, result);
        if (result.Count > 0)
            Console.WriteLine($"[QbitDownload] псевдонимы tmdb={tmdb}{(tv ? " tv" : "")} «{title}»: {string.Join(" · ", result.Select(a => a.alias + " [" + a.source + "]"))}");
        else
            Console.WriteLine($"[QbitDownload] псевдонимы tmdb={tmdb}{(tv ? " tv" : "")} «{title}»: не нашлось ни одного");
        return result;
    }

    #endregion

    #region TMDB / Shikimori

    // Свой же прокси /tmdb/api/… на loopback — тот же приём, что CatalogWarmup и AiredEpisodes:
    // ответ кешируется Staticache, ключ TMDB подставляет прокси, внешнего доступа не нужно.
    static async Task<JObject> TmdbDetail(int tmdb, bool tv, string lang)
    {
        int port = 9118;
        try { if (CoreInit.conf.listen.port > 0) port = CoreInit.conf.listen.port; } catch { }
        try
        {
            using var rc = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            string url = $"http://127.0.0.1:{port}/tmdb/api/3/{(tv ? "tv" : "movie")}/{tmdb}?append_to_response=alternative_titles&language={lang}";
            string body = await rc.GetStringAsync(url);
            var o = JObject.Parse(body);
            return (o.Value<int?>("id") ?? 0) > 0 ? o : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[QbitDownload] псевдонимы: TMDB {(tv ? "tv" : "movie")}/{tmdb} ({lang}) недоступен — {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // Поиск в Shikimori по каждому латинскому имени, что уже есть (romaji TMDB, en title), кандидат
    // выбирается СТРОГО тем же JutSuMatch.Pick, что у постеров jut.su (не уверены → отказ), и только
    // среди своего типа (фильм/сериал). Не больше двух поисков: лимит Shikimori 5 rps / 90 rpm.
    static async Task<List<TitleAlias>> AliasesFromShikimoriLive(List<TitleAlias> latin, string cardTitle, int year, bool tv)
    {
        var res = new List<TitleAlias>();
        var queries = latin.Where(a => a != null && !_cyrRx.IsMatch(a.alias ?? "")).Select(a => a.alias)
                           .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
        if (queries.Count == 0) return res;

        var years = year > 0 ? new List<int> { year } : new List<int>();
        int pace = Math.Max(0, ModInit.conf?.jutPosterPaceMs ?? 350);
        JutAnimeCandidate pick = null;
        foreach (string q in queries)
        {
            var arr = await JutShikiSearch(q);
            if (arr == null) continue;
            var cands = JutSuMatch.ParseCandidates(arr).Where(c => ShikiKindOk(c.kind, tv)).ToList();
            var m = JutSuMatch.Pick(q, cardTitle, years, cands);
            if (m.ok) { pick = m.pick; break; }
            await Task.Delay(pace);
        }
        if (pick == null) return res;

        await Task.Delay(pace);
        var detail = await JutShikiDetail(pick.id);
        return AliasesFromShikimori(pick, detail);
    }

    static async Task<JObject> JutShikiDetail(int id)
    {
        if (id <= 0) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, JutShikiHost + "/api/animes/" + id);
            req.Headers.TryAddWithoutValidation("User-Agent", JutDbUa);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var resp = await _jutDbHttp.Value.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            return JObject.Parse(await resp.Content.ReadAsStringAsync());
        }
        catch { return null; }
    }

    #endregion

    #region БД: title_alias

    // Все методы: без БД (реплика) или при кулдауне — null/ничего; исключения глушатся LiFail.
    static async Task<List<TitleAlias>> AliasesDbRead(int tmdb, bool tv)
    {
        if (!LocalIndexEnabled || LiDown || tmdb <= 0) return null;
        if (!await EnsureSchema()) return null;
        try
        {
            await using var db = new NpgsqlConnection(ModInit.conf.localIndexConnection);
            await db.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
select alias, alias_norm, source, note, hits, added_at
from title_alias where tmdb_id = @t and is_tv = @tv
order by case source when 'manual' then 0 when 'claude' then 0 else 1 end, hits desc, added_at asc", db);
            cmd.CommandTimeout = Math.Max(2, ModInit.conf.localIndexTimeoutSec);
            cmd.Parameters.AddWithValue("t", tmdb);
            cmd.Parameters.AddWithValue("tv", tv);
            var list = new List<TitleAlias>();
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new TitleAlias
                {
                    alias = r.GetString(0), norm = r.GetString(1), source = r.GetString(2),
                    note = r.IsDBNull(3) ? null : r.GetString(3), hits = r.IsDBNull(4) ? 0 : r.GetInt32(4),
                    addedAt = r.IsDBNull(5) ? null : r.GetDateTime(5)
                });
            return list;
        }
        catch (Exception ex) { LiFail("псевдонимы(чтение)", ex); return null; }
    }

    static async Task<bool> AliasesDbWrite(int tmdb, bool tv, IEnumerable<TitleAlias> list)
    {
        if (!LocalIndexEnabled || LiDown || tmdb <= 0 || list == null) return false;
        if (!await EnsureSchema()) return false;
        try
        {
            await using var db = new NpgsqlConnection(ModInit.conf.localIndexConnection);
            await db.OpenAsync();
            foreach (var a in list)
            {
                if (a == null || string.IsNullOrEmpty(a.norm)) continue;
                await using var cmd = new NpgsqlCommand(@"
insert into title_alias (tmdb_id, is_tv, alias, alias_norm, source, note)
values (@t, @tv, @a, @n, @s, @note)
on conflict (tmdb_id, is_tv, alias_norm) do update set
  source = case when title_alias.source in ('manual','claude') then title_alias.source else excluded.source end,
  note   = case when title_alias.source in ('manual','claude') then title_alias.note   else excluded.note   end;", db);
                cmd.CommandTimeout = Math.Max(2, ModInit.conf.localIndexTimeoutSec);
                cmd.Parameters.AddWithValue("t", tmdb);
                cmd.Parameters.AddWithValue("tv", tv);
                cmd.Parameters.AddWithValue("a", a.alias);
                cmd.Parameters.AddWithValue("n", a.norm);
                cmd.Parameters.AddWithValue("s", a.source ?? TitleAlias.SrcManual);
                cmd.Parameters.AddWithValue("note", (object)a.note ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync();
            }
            return true;
        }
        catch (Exception ex) { LiFail("псевдонимы(запись)", ex); return false; }
    }

    static async Task<bool> AliasDbDelete(int tmdb, bool tv, string norm)
    {
        if (!LocalIndexEnabled || LiDown || tmdb <= 0 || string.IsNullOrEmpty(norm)) return false;
        if (!await EnsureSchema()) return false;
        try
        {
            await using var db = new NpgsqlConnection(ModInit.conf.localIndexConnection);
            await db.OpenAsync();
            await using var cmd = new NpgsqlCommand("delete from title_alias where tmdb_id=@t and is_tv=@tv and alias_norm=@n", db);
            cmd.Parameters.AddWithValue("t", tmdb);
            cmd.Parameters.AddWithValue("tv", tv);
            cmd.Parameters.AddWithValue("n", norm);
            return await cmd.ExecuteNonQueryAsync() > 0;
        }
        catch (Exception ex) { LiFail("псевдонимы(удаление)", ex); return false; }
    }

    // «Сработал» — этим именем трекеры ответили. Fire-and-forget: счётчик, не истина.
    static void AliasHitAsync(int tmdb, bool tv, string norm)
    {
        if (!LocalIndexEnabled || LiDown || tmdb <= 0 || string.IsNullOrEmpty(norm)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (!await EnsureSchema()) return;
                await using var db = new NpgsqlConnection(ModInit.conf.localIndexConnection);
                await db.OpenAsync();
                await using var cmd = new NpgsqlCommand("update title_alias set hits = hits + 1 where tmdb_id=@t and is_tv=@tv and alias_norm=@n", db);
                cmd.Parameters.AddWithValue("t", tmdb);
                cmd.Parameters.AddWithValue("tv", tv);
                cmd.Parameters.AddWithValue("n", norm);
                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex) { LiFail("псевдонимы(hit)", ex); }
        });
    }

    #endregion

    #region БД: title_miss (журнал промахов)

    // Открытые промахи — в памяти, чтобы авто-закрытие не стоило запроса в БД на каждом удачном поиске.
    static readonly ConcurrentDictionary<string, byte> _missOpen = new();
    static int _missLoaded;   // 0 = список открытых из БД ещё не читали

    static async Task MissLoadOpen()
    {
        if (System.Threading.Volatile.Read(ref _missLoaded) == 1) return;
        if (!LocalIndexEnabled || LiDown) return;
        if (!await EnsureSchema()) return;
        try
        {
            await using var db = new NpgsqlConnection(ModInit.conf.localIndexConnection);
            await db.OpenAsync();
            await using var cmd = new NpgsqlCommand("select tmdb_id, is_tv from title_miss where resolved_at is null", db);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) _missOpen[AliasKey(r.GetInt32(0), r.GetBoolean(1))] = 1;
            System.Threading.Volatile.Write(ref _missLoaded, 1);
        }
        catch (Exception ex) { LiFail("промахи(чтение)", ex); }
    }

    public const string MissZero = "zero";   // трекеры дали 0 строк (даже с псевдонимами)
    public const string MissNoRu = "noru";   // строки есть, но ни одной русской, прошедшей гейт имени

    /// <summary>
    /// В журнал (count++): kind=zero — трекеры дали ноль даже с псевдонимами при живом индексаторе;
    /// kind=noru — трекеры ответили, но русской строки в выдаче нет (часто это тоже имя: ответили
    /// чужим одноимённым тайтлом, а русский релиз лежит под другим переводом).
    /// </summary>
    static void MissRecordAsync(int tmdb, bool tv, string title, string original, int year, string kind)
    {
        if (!LocalIndexEnabled || LiDown || tmdb <= 0 || ModInit.conf?.titleAliasMissJournal == false) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (!await EnsureSchema()) return;
                await MissLoadOpen();
                await using var db = new NpgsqlConnection(ModInit.conf.localIndexConnection);
                await db.OpenAsync();
                await using var cmd = new NpgsqlCommand(@"
insert into title_miss (tmdb_id, is_tv, title, title_original, year, kind)
values (@t, @tv, @title, @orig, @yr, @kind)
on conflict (tmdb_id, is_tv) do update set
  count = title_miss.count + 1, last_at = now(),
  title = coalesce(excluded.title, title_miss.title),
  title_original = coalesce(excluded.title_original, title_miss.title_original),
  year = case when excluded.year > 0 then excluded.year else title_miss.year end,
  kind = excluded.kind,
  -- закрытый вручную/прогоном промах, который повторился, открывается снова: имя всё ещё не работает
  resolved_at = null, resolved_by = null;", db);
                cmd.Parameters.AddWithValue("t", tmdb);
                cmd.Parameters.AddWithValue("tv", tv);
                cmd.Parameters.AddWithValue("title", (object)title ?? DBNull.Value);
                cmd.Parameters.AddWithValue("orig", (object)original ?? DBNull.Value);
                cmd.Parameters.AddWithValue("yr", year);
                cmd.Parameters.AddWithValue("kind", kind == MissNoRu ? MissNoRu : MissZero);
                await cmd.ExecuteNonQueryAsync();
                _missOpen[AliasKey(tmdb, tv)] = 1;
                _aliasHealthAt = DateTime.MinValue;
            }
            catch (Exception ex) { LiFail("промахи(запись)", ex); }
        });
    }

    /// <summary>Трекеры ответили — открытый промах этой карточки закрывается автоматом.</summary>
    static void MissResolveAutoAsync(int tmdb, bool tv)
    {
        if (tmdb <= 0 || !LocalIndexEnabled || LiDown) return;
        _ = Task.Run(async () =>
        {
            await MissLoadOpen();
            if (!_missOpen.ContainsKey(AliasKey(tmdb, tv))) return;
            await MissResolve(tmdb, tv, "auto", null);
        });
    }

    static async Task<bool> MissResolve(int tmdb, bool tv, string by, string note)
    {
        if (!LocalIndexEnabled || LiDown || tmdb <= 0) return false;
        if (!await EnsureSchema()) return false;
        try
        {
            await using var db = new NpgsqlConnection(ModInit.conf.localIndexConnection);
            await db.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
update title_miss set resolved_at = now(), resolved_by = @by, note = coalesce(@note, note)
where tmdb_id=@t and is_tv=@tv and resolved_at is null", db);
            cmd.Parameters.AddWithValue("t", tmdb);
            cmd.Parameters.AddWithValue("tv", tv);
            cmd.Parameters.AddWithValue("by", by ?? "manual");
            cmd.Parameters.AddWithValue("note", (object)note ?? DBNull.Value);
            int n = await cmd.ExecuteNonQueryAsync();
            _missOpen.TryRemove(AliasKey(tmdb, tv), out _);
            _aliasHealthAt = DateTime.MinValue;
            return n > 0;
        }
        catch (Exception ex) { LiFail("промахи(закрытие)", ex); return false; }
    }

    static async Task<bool> MissNote(int tmdb, bool tv, string note)
    {
        if (!LocalIndexEnabled || LiDown || tmdb <= 0) return false;
        if (!await EnsureSchema()) return false;
        try
        {
            await using var db = new NpgsqlConnection(ModInit.conf.localIndexConnection);
            await db.OpenAsync();
            await using var cmd = new NpgsqlCommand("update title_miss set note = @note where tmdb_id=@t and is_tv=@tv", db);
            cmd.Parameters.AddWithValue("t", tmdb);
            cmd.Parameters.AddWithValue("tv", tv);
            cmd.Parameters.AddWithValue("note", (object)note ?? DBNull.Value);
            return await cmd.ExecuteNonQueryAsync() > 0;
        }
        catch (Exception ex) { LiFail("промахи(пометка)", ex); return false; }
    }

    /// <summary>Журнал для админки/прогона: открытые первыми, по count desc.</summary>
    static async Task<JArray> MissList(bool openOnly, int limit)
    {
        var res = new JArray();
        if (!LocalIndexEnabled || LiDown) return res;
        if (!await EnsureSchema()) return res;
        try
        {
            await using var db = new NpgsqlConnection(ModInit.conf.localIndexConnection);
            await db.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
select tmdb_id, is_tv, title, title_original, year, count, first_at, last_at, resolved_at, resolved_by, note, kind
from title_miss
where (@open = false or resolved_at is null)
order by (resolved_at is null) desc, (kind = 'zero') desc, count desc, last_at desc
limit @lim", db);
            cmd.Parameters.AddWithValue("open", openOnly);
            cmd.Parameters.AddWithValue("lim", Math.Clamp(limit, 1, 2000));
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                res.Add(new JObject
                {
                    ["tmdb_id"] = r.GetInt32(0),
                    ["is_tv"] = r.GetBoolean(1),
                    ["title"] = r.IsDBNull(2) ? null : r.GetString(2),
                    ["title_original"] = r.IsDBNull(3) ? null : r.GetString(3),
                    ["year"] = r.IsDBNull(4) ? 0 : r.GetInt32(4),
                    ["count"] = r.IsDBNull(5) ? 0 : r.GetInt32(5),
                    ["first_at"] = r.IsDBNull(6) ? null : r.GetDateTime(6).ToString("o"),
                    ["last_at"] = r.IsDBNull(7) ? null : r.GetDateTime(7).ToString("o"),
                    ["resolved_at"] = r.IsDBNull(8) ? null : r.GetDateTime(8).ToString("o"),
                    ["resolved_by"] = r.IsDBNull(9) ? null : r.GetString(9),
                    ["note"] = r.IsDBNull(10) ? null : r.GetString(10),
                    ["kind"] = r.IsDBNull(11) ? MissZero : r.GetString(11)
                });
            }
        }
        catch (Exception ex) { LiFail("промахи(список)", ex); }
        return res;
    }

    #endregion

    #region хелс

    static (int open, int aliases, int titles) _aliasHealth;
    static DateTime _aliasHealthAt = DateTime.MinValue;
    static int _aliasHealthBusy;

    /// <summary>
    /// Счётчики для строки хелса: снимок раз в минуту, обновляется в фоне — сам хелс ждать БД не должен.
    /// </summary>
    internal static (int open, int aliases, int titles, bool fresh) AliasHealthSnapshot()
    {
        bool fresh = (DateTime.UtcNow - _aliasHealthAt).TotalSeconds < 60;
        if (!fresh && LocalIndexEnabled && !LiDown && System.Threading.Interlocked.CompareExchange(ref _aliasHealthBusy, 1, 0) == 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (!await EnsureSchema()) return;
                    await using var db = new NpgsqlConnection(ModInit.conf.localIndexConnection);
                    await db.OpenAsync();
                    await using var cmd = new NpgsqlCommand(@"
select (select count(*) from title_miss where resolved_at is null),
       (select count(*) from title_alias),
       (select count(distinct (tmdb_id, is_tv)) from title_alias)", db);
                    cmd.CommandTimeout = 5;
                    await using var r = await cmd.ExecuteReaderAsync();
                    if (await r.ReadAsync())
                        _aliasHealth = ((int)r.GetInt64(0), (int)r.GetInt64(1), (int)r.GetInt64(2));
                    _aliasHealthAt = DateTime.UtcNow;
                }
                catch (Exception ex) { LiFail("псевдонимы(хелс)", ex); }
                finally { System.Threading.Volatile.Write(ref _aliasHealthBusy, 0); }
            });
        }
        return (_aliasHealth.open, _aliasHealth.aliases, _aliasHealth.titles, _aliasHealthAt != DateTime.MinValue);
    }

    #endregion

    /// <summary>Строка хелса: статус + текст. off — киллсвитч; без БД — ok с оговоркой; иначе счётчики.</summary>
    internal static (string status, string detail) AliasesHealthVerdict()
    {
        if (!TitleAliasesOn) return ("off", "выключено (titleAliases:false)");
        if (!LocalIndexEnabled) return ("ok", "без своей БД: псевдонимы только в памяти процесса, журнала промахов нет");
        if (LiDown) return ("warn", "своя БД недоступна — добор только из памяти, промахи не пишутся");
        var s = AliasHealthSnapshot();
        if (!s.fresh) return ("ok", "считаю…");
        string detail = $"открытых промахов {s.open} · псевдонимов {s.aliases} у {s.titles} карточек";
        if (s.open > 0) detail += " — прогон /title-aliases или вкладка «Названия»";
        return ("ok", detail);
    }

    #region данные для админки

    /// <summary>Журнал промахов для вкладки/прогона: {enabled, db, open, items[]}.</summary>
    internal static async Task<JObject> AliasesAdminMisses(bool openOnly, int limit)
    {
        var res = new JObject
        {
            ["enabled"] = TitleAliasesOn,
            ["db"] = LocalIndexEnabled,
            ["journal"] = ModInit.conf?.titleAliasMissJournal != false
        };
        await MissLoadOpen();
        res["open"] = _missOpen.Count;
        res["items"] = await MissList(openOnly, limit);
        return res;
    }

    /// <summary>
    /// Кнопка «Фикс» во вкладке: готовый промпт для Claude — список открытых промахов с тем, что автомат
    /// уже перепробовал, и инструкция прогона. Владелец копирует его в Claude Code (репо медиасервера).
    /// </summary>
    internal static async Task<string> AliasesAdminPrompt(int limit)
    {
        var misses = await MissList(openOnly: true, limit);
        var tried = new Dictionary<string, List<TitleAlias>>();
        foreach (var m in misses.OfType<JObject>())
        {
            int id = m.Value<int?>("tmdb_id") ?? 0; bool tv = m.Value<bool?>("is_tv") == true;
            tried[AliasKey(id, tv)] = await AliasesKnown(id, tv);
        }
        return AliasesFixPrompt(misses, tried, DateTime.UtcNow);
    }

    /// <summary>Чистая сборка промпта (тестируется). tried — псевдонимы, которые автомат уже пробовал.</summary>
    public static string AliasesFixPrompt(JArray misses, IReadOnlyDictionary<string, List<TitleAlias>> tried, DateTime now)
    {
        var sb = new System.Text.StringBuilder();
        var list = (misses ?? new JArray()).OfType<JObject>().ToList();
        sb.AppendLine("Запусти скилл /title-aliases (репо E:\\Media-server, .claude/skills/title-aliases/SKILL.md) и разбери журнал промахов поиска раздач.");
        sb.AppendLine($"Снимок журнала: {now:yyyy-MM-dd HH:mm} UTC, открытых промахов {list.Count}. Сервер http://192.168.87.24:9118, ручки /admin/d1v/api/aliases/* (пароль — D:\\docker\\config\\lampac\\passwd, заголовок X-D1V-Admin: 1).");
        sb.AppendLine();
        sb.AppendLine("Что сделать для КАЖДОЙ карточки ниже:");
        sb.AppendLine("1. Подобрать имена, под которыми тайтл знают русские трекеры (лицензионное имя в РФ, имя у фансаб-групп, романдзи, имя первой части франшизы, как называют раздачи в bitmagnet/своём индексе). Имена из списка «уже пробовали» не повторять.");
        sb.AppendLine("2. ПРОВЕРИТЬ каждое живым запросом: GET /admin/d1v/api/aliases/check?query=<имя>&is_serial=0 — имя засчитано, только если rows > 0 И в sample тот самый тайтл (год ±1, оригинал в строке; для вида «русских нет» — в sample есть русская строка). Не больше 3 проверок на карточку, пауза 5 с между проверками.");
        sb.AppendLine("3. Подтверждённое — POST /admin/d1v/api/aliases/add {tmdb_id, is_tv, alias, source:\"claude\", note:\"подтверждено <дата>: <трекеры и число строк>\"}, затем POST /admin/d1v/api/aliases/resolve {tmdb_id, is_tv, by:\"claude\"}.");
        sb.AppendLine("4. Не нашлось — промах НЕ закрывать, оставить пометку: POST /admin/d1v/api/aliases/note {tmdb_id, is_tv, note:\"<дата>: пробовал …, в рунете раздач нет\"}.");
        sb.AppendLine("5. В конце — таблица владельцу: тайтл → добавленное имя → чем подтверждено; отдельно — что осталось открытым и почему.");
        sb.AppendLine("Русские запросы к серверу — только питоном с utf-8 (не curl из Git Bash).");
        sb.AppendLine();
        if (list.Count == 0)
        {
            sb.AppendLine("Открытых промахов нет — делать нечего.");
            return sb.ToString();
        }
        sb.AppendLine("Карточки (сверху — самые больные):");
        int n = 0;
        foreach (var m in list)
        {
            n++;
            int id = m.Value<int?>("tmdb_id") ?? 0; bool tv = m.Value<bool?>("is_tv") == true;
            string kind = m.Value<string>("kind") == MissNoRu ? "трекеры отвечают, но русских строк нет" : "трекеры дали 0 строк";
            sb.AppendLine($"{n}. «{m.Value<string>("title")}» ({(tv ? "сериал" : "фильм")}, {(m.Value<int?>("year") is int y && y > 0 ? y.ToString() : "год ?")}, tmdb_id={id}, is_tv={(tv ? "true" : "false")})");
            string orig = m.Value<string>("title_original");
            if (!string.IsNullOrWhiteSpace(orig)) sb.AppendLine($"   оригинал: {orig}");
            sb.AppendLine($"   вид промаха: {kind}; зритель упёрся {m.Value<int?>("count") ?? 0} раз, последний — {m.Value<string>("last_at")}");
            if (tried != null && tried.TryGetValue(AliasKey(id, tv), out var t) && t != null && t.Count > 0)
                sb.AppendLine("   уже пробовали (автомат): " + string.Join(" · ", t.Select(a => $"{a.alias} [{a.source}]")));
            else
                sb.AppendLine("   уже пробовали (автомат): ничего не нашлось ни в TMDB alternative_titles, ни в Shikimori");
            string note = m.Value<string>("note");
            if (!string.IsNullOrWhiteSpace(note)) sb.AppendLine("   пометка прошлого прогона: " + note);
        }
        return sb.ToString();
    }

    internal static Task<bool> AliasesAdminResolve(int tmdb, bool tv, string by, string note)
        => MissResolve(tmdb, tv, by is TitleAlias.SrcClaude ? TitleAlias.SrcClaude : TitleAlias.SrcManual, note);

    internal static Task<bool> AliasesAdminNote(int tmdb, bool tv, string note) => MissNote(tmdb, tv, note);

    internal static async Task<JObject> AliasesAdminList(int tmdb, bool tv)
    {
        // Админке — свежее из БД (hits растут там, память их не видит до перечитывания); без БД — память.
        var list = await AliasesDbRead(tmdb, tv) ?? await AliasesKnown(tmdb, tv);
        if (list.Count > 0) AliasMemPut(tmdb, tv, list);
        return new JObject
        {
            ["tmdb_id"] = tmdb, ["is_tv"] = tv,
            ["aliases"] = new JArray(list.Select(a => a.ToJson()))
        };
    }

    internal static async Task<(bool ok, string message, TitleAlias alias)> AliasesAdminAdd(int tmdb, bool tv, string alias, string source, string note)
    {
        if (tmdb <= 0) return (false, "нет TMDB id", null);
        var a = TitleAlias.Make(alias, source is TitleAlias.SrcClaude ? TitleAlias.SrcClaude : TitleAlias.SrcManual, note);
        if (a == null || a.norm.Length < 3) return (false, "имя пустое или слишком короткое", null);
        if (!LocalIndexEnabled) return (false, "своя БД не настроена (localIndexConnection) — сохранить некуда", null);
        // Ручное имя главнее автомата: пишем как есть, а если такое уже было от tmdb/shikimori —
        // источник поднимается до manual/claude (см. on conflict в AliasesDbWrite).
        bool ok = await AliasesDbWrite(tmdb, tv, new[] { a });
        if (!ok) return (false, "БД недоступна", null);
        AliasMemDrop(tmdb, tv);
        await AliasesKnown(tmdb, tv);
        return (true, null, a);
    }

    internal static async Task<bool> AliasesAdminDelete(int tmdb, bool tv, string norm)
    {
        bool ok = await AliasDbDelete(tmdb, tv, norm);
        if (ok) { AliasMemDrop(tmdb, tv); await AliasesKnown(tmdb, tv); }
        return ok;
    }

    internal static async Task<JObject> AliasesAdminRefresh(int tmdb, bool tv, string title, string original, int year)
    {
        AliasMemDrop(tmdb, tv);
        var list = await AliasesResolve(tmdb, tv, title, original, year, refresh: true);
        return new JObject { ["tmdb_id"] = tmdb, ["is_tv"] = tv, ["aliases"] = new JArray(list.Select(a => a.ToJson())) };
    }

    /// <summary>
    /// Живая проверка имени тем же индексатором, что и поиск: сколько строк дали трекеры. Это то
    /// самое подтверждение, без которого прогон не имеет права записать имя.
    /// </summary>
    internal static async Task<JObject> AliasesAdminCheck(string query, int isSerial)
    {
        var res = new JObject { ["query"] = query, ["rows"] = 0, ["ok"] = false };
        if (string.IsNullOrWhiteSpace(query)) { res["message"] = "пустой запрос"; return res; }
        var arr = await FetchIndexer(query.Trim(), null, null, 0, isSerial, ModInit.conf?.indexerApikey);
        if (arr == null) { res["message"] = "индексатор не ответил"; return res; }
        var byTracker = new JObject();
        foreach (var t in arr.OfType<JObject>())
        {
            string tr = t.Value<string>("tracker") ?? "?";
            byTracker[tr] = (byTracker.Value<int?>(tr) ?? 0) + 1;
        }
        res["ok"] = true;
        res["rows"] = arr.Count;
        res["trackers"] = byTracker;
        res["sample"] = new JArray(arr.OfType<JObject>().Take(12).Select(t => new JObject
        {
            ["title"] = t.Value<string>("title"), ["tracker"] = t.Value<string>("tracker"), ["sid"] = t.Value<int?>("sid") ?? 0
        }));
        return res;
    }

    #endregion
}
