using QbitDownload;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace QbitDownload.Tests;

// Тесты чистого парсера jut.su. Сети нет — только фикстуры реальных страниц
// (fixtures/jut/, сохранены в UTF-8; derou= затёрт, чтобы id аккаунта не лежал в git).
//
// Карта «поле ↔ маркер ↔ фикстура ↔ тест» — E:\Media-server\claude\jut\04-parser-map.md
// Факты о сайте                            — E:\Media-server\claude\jut\01-recon.md
public class JutSuParseTests
{
    static string Fx(string name)
    {
        // База — папка сборки; фикстуры копируются рядом (см. csproj) либо берутся из исходников.
        string[] probe =
        {
            Path.Combine(AppContext.BaseDirectory, "fixtures", "jut", name),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "fixtures", "jut", name)
        };
        foreach (string p in probe)
            if (File.Exists(p)) return File.ReadAllText(p);
        throw new FileNotFoundException("нет фикстуры " + name + " (искал в " + string.Join(" ; ", probe) + ")");
    }

    #region предобработка

    [Fact]
    public void Strip_вырезает_script_и_style()
    {
        // Инлайн-CSS содержит films_title / short-btn.black — без вырезания это ложные срабатывания
        string html = "<style>.short-btn.black{x}</style><b>ok</b><script>var films_title=1;</script>";
        string s = JutSuParse.Strip(html);
        Assert.DoesNotContain("short-btn", s);
        Assert.DoesNotContain("films_title", s);
        Assert.Contains("ok", s);
    }

    [Fact]
    public void StripItalics_убирает_скрытый_SEO()
    {
        // Без этого жанр читается как «Аниме боевик»
        Assert.Equal("боевик", JutSuParse.Text(JutSuParse.StripItalics("<i>Аниме </i>боевик")));
    }

    [Fact]
    public void Реальная_страница_содержит_ловушки_в_CSS()
    {
        // Страховка: если сайт перестанет слать жирный инлайн-CSS, Strip перестанет быть критичным,
        // но пока — вот доказательство, что он нужен.
        string raw = Fx("title-spy-family.html");
        Assert.Contains("<style", raw, StringComparison.OrdinalIgnoreCase);
        Assert.True(raw.Length > 40_000, "страница тайтла должна быть жирной (инлайн-CSS)");
    }

    #endregion

    #region каталог

    [Fact]
    public void Карточка_каталога_разбирается_целиком()
    {
        var page = JutSuParse.ParseCatalog(Fx("catalog-ajax.html"));

        Assert.Equal(30, page.items.Count);          // страница каталога — всегда 30
        Assert.True(page.hasNext);                   // var anime_page_next = true

        var c = page.items[0];
        Assert.Equal("hanaori-san", c.slug);
        Assert.Equal(1365, c.id);
        Assert.False(string.IsNullOrWhiteSpace(c.titleRu));
        // ⚠️ постер — CSS background, не <img>; и имя файла ≠ слаг → только парсить
        Assert.StartsWith("https://gen.jut.su/uploads/animethumbs/", c.poster);
        Assert.False(string.IsNullOrWhiteSpace(c.titleOrig));
    }

    [Fact]
    public void Постер_никогда_не_конструируется_из_слага()
    {
        // /boku-hero-academia/ → anime_boku-no-hero-academia.jpg, /oneepiece/ → anime_onepiece.jpg
        var page = JutSuParse.ParseCatalog(Fx("catalog-ajax.html"));
        foreach (var c in page.items)
            Assert.False(string.IsNullOrEmpty(c.poster), "постер обязан быть распарсен: " + c.slug);
    }

    [Fact]
    public void Aailines_считает_серии_сезоны_фильмы()
    {
        var card = JutSuParse.ParseCard("", 1,
            @"<a href=""/x/""><div class=""aaname"">X</div><div class=""aailines"">10 сезонов<br>197 серий<br>4 фильма</div>");
        Assert.Equal(10, card.seasons);
        Assert.Equal(197, card.episodes);
        Assert.Equal(4, card.films);
    }

    [Theory]
    [InlineData("12 серий", 0, 12, 0)]
    [InlineData("2 сезона<br>23 серии", 2, 23, 0)]
    [InlineData("1173 серии<br>14 фильмов", 0, 1173, 14)]
    [InlineData("170 серий<br>1 фильм", 0, 170, 1)]
    [InlineData("10 сезонов", 10, 0, 0)]
    public void Aailines_переживает_склонения(string lines, int seasons, int eps, int films)
    {
        var card = JutSuParse.ParseCard("", 1, @"<a href=""/x/""><div class=""aailines"">" + lines + "</div>");
        Assert.Equal(seasons, card.seasons);
        Assert.Equal(eps, card.episodes);
        Assert.Equal(films, card.films);
    }

    [Fact]
    public void Онгоинг_детектится_двумя_признаками()
    {
        Assert.True(JutSuParse.ParseCard("anime_year_ongoing", 1, @"<a href=""/x/"">").ongoing);
        Assert.True(JutSuParse.ParseCard("", 1, @"<a href=""/x/""><div class=""all_anime_ongoing""></div>").ongoing);
        Assert.False(JutSuParse.ParseCard("anime_year_2026", 1, @"<a href=""/x/"">").ongoing);
    }

    [Fact]
    public void Жанры_читаются_с_опечаткой_ganre()
    {
        // ⚠️ на сайте именно ganre, не genre
        var c = JutSuParse.ParseCard("anime_ganre_comedy anime_ganre_romance anime_type_shonen anime_year_2026",
                                     1, @"<a href=""/x/"">");
        Assert.Equal(new[] { "comedy", "romance" }, c.genres);
        Assert.Equal(new[] { "shonen" }, c.types);
        Assert.Equal(new[] { 2026 }, c.years);
    }

    [Fact]
    public void Годы_разворачивают_диапазоны()
    {
        var c = JutSuParse.ParseCard("anime_year_2015-2023 anime_year_ongoing", 1, @"<a href=""/x/"">");
        Assert.Contains(2015, c.years);
        Assert.Contains(2023, c.years);
    }

    [Fact]
    public void Просмотрено_меняет_класс()
    {
        Assert.True(JutSuParse.ParseCard("this_anime_is_viewed", 1, @"<a href=""/x/"">").viewed);
        Assert.False(JutSuParse.ParseCard("anime_mark_viewed_id_52d", 1, @"<a href=""/x/"">").viewed);
    }

    [Fact]
    public void Конец_списка_три_признака()
    {
        Assert.False(JutSuParse.ParseCatalog(@"<script>var anime_page_next = false;</script>").hasNext);
        Assert.False(JutSuParse.ParseCatalog(@"<a class=""vnright"" href=""#"">").hasNext);
        Assert.False(JutSuParse.ParseCatalog("").hasNext);   // пусто → конец
    }

    #endregion

    #region страница тайтла

    [Fact]
    public void Метаданные_из_microdata()
    {
        // og:* и JSON-LD на сайте ОТСУТСТВУЮТ — только Microdata
        var t = JutSuParse.ParseTitle(Fx("title-spy-family.html"));
        Assert.Equal("spy-family", t.slug);
        Assert.Equal("Семья шпиона", t.titleRu);
        Assert.Equal("Spy x Family", t.titleOrig);
        Assert.True(t.rating > 0, "рейтинг из itemprop=ratingValue");
        Assert.True(t.ratingCount > 0);
        Assert.True(t.id > 0, "внутренний id из anime_fs_N");
        Assert.StartsWith("https://gen.jut.su/", t.poster);
    }

    [Fact]
    public void Жанры_тайтла_без_SEO_вкраплений()
    {
        var t = JutSuParse.ParseTitle(Fx("title-spy-family.html"));
        Assert.NotEmpty(t.genres);
        foreach (string g in t.genres)
            Assert.DoesNotContain("Аниме", g);      // <i>Аниме </i> обязан быть вырезан
        Assert.Contains(2022, t.years);             // ⚠️ год из ТЕКСТА ссылки, href — диапазон
    }

    [Fact]
    public void Описание_без_SEO_вкраплений()
    {
        var t = JutSuParse.ParseTitle(Fx("title-spy-family.html"));
        Assert.False(string.IsNullOrWhiteSpace(t.descr));
        Assert.DoesNotContain("анидаб", t.descr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("смотреть онлайн", t.descr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Онгоинг_на_странице_тайтла()
    {
        Assert.True(JutSuParse.ParseTitle(Fx("title-yani-neko.html")).ongoing);
        Assert.False(JutSuParse.ParseTitle(Fx("title-spy-family.html")).ongoing);
    }

    [Fact]
    public void Единственное_и_множественное_число()
    {
        // «Жанр:» / «Год выпуска:» (ед.) против «Жанры:» / «Годы выпуска:» (мн.)
        var t = JutSuParse.ParseTitle(Fx("title-yani-neko.html"));
        Assert.NotEmpty(t.genres);
        Assert.NotEmpty(t.years);
    }

    [Fact]
    public void Список_серий_разбирается()
    {
        var t = JutSuParse.ParseTitle(Fx("title-spy-family.html"));
        Assert.NotEmpty(t.items);
        Assert.Contains(t.items, e => e.kind == JutEpKind.Episode && e.season == 1 && e.num == 1);
        Assert.Contains(t.items, e => e.kind == JutEpKind.Film);      // фильмы всегда в корне слага
        Assert.Contains(t.items, e => e.season == 2);
    }

    [Fact]
    public void Сезон_берётся_ТОЛЬКО_из_URL()
    {
        // 🔥 Главный регресс: у One Piece 54 заголовка-арки и НОЛЬ season-N в URL.
        // Если начать выводить сезон из <h2>, получим 54 «сезона» вместо одного.
        var t = JutSuParse.ParseTitle(Fx("title-oneepiece-content.html"), "oneepiece");
        var eps = t.items.Where(e => e.kind == JutEpKind.Episode).ToList();

        Assert.Equal(1173, eps.Count);
        Assert.Equal(14, t.items.Count(e => e.kind == JutEpKind.Film));
        Assert.All(eps, e => Assert.Equal(1, e.season));
        Assert.Contains(t.items, e => !string.IsNullOrEmpty(e.arcRu));   // арки распознаны, но сезоном не стали
    }

    [Fact]
    public void Классы_green_black_игнорируются()
    {
        // Декор: старт чередования нестабилен, семантики нет
        string html = @"<a href=""/x/episode-1.html"" class=""short-btn green video the_hildi"">1 серия</a>
                        <a href=""/x/episode-2.html"" class=""short-btn black video the_hildi"">2 серия</a>";
        var eps = JutSuParse.ParseEpisodeList(html);
        Assert.Equal(2, eps.Count);
        Assert.All(eps, e => Assert.Equal(JutEpKind.Episode, e.kind));
    }

    [Fact]
    public void Абсолютные_href_нормализуются()
    {
        // На страницах Наруто href абсолютные, везде ещё — относительные
        string html = @"<a href=""https://jut.su/naruuto/ova-1.html"" class=""short-btn green video the_hildi"">OVA</a>";
        var eps = JutSuParse.ParseEpisodeList(html);
        Assert.Single(eps);
        Assert.Equal("/naruuto/ova-1.html", eps[0].url);
        Assert.Equal(JutEpKind.Ova, eps[0].kind);
    }

    [Fact]
    public void Ссылка_на_страницу_сезона_не_серия()
    {
        string html = @"<a href=""/spy-family/season-1/"" class=""short-btn green video the_hildi"">1 сезон</a>";
        Assert.Empty(JutSuParse.ParseEpisodeList(html));
    }

    [Fact]
    public void Второй_layout_с_названиями_серий()
    {
        string html = @"<div class=""watch_list_item""><ul>
            <li><a href=""https://jut.su/naruuto/ova-1.html"" class=""pos_rel"">Наруто OVA 1: В поисках клевера</a></li>
            </ul></div>";
        var eps = JutSuParse.ParseEpisodeList(html);
        Assert.Single(eps);
        Assert.Contains("клевера", eps[0].name);
    }

    [Fact]
    public void Отметка_просмотра_и_процент()
    {
        string html = @"<a href=""/x/episode-1.html"" class=""short-btn green video the_hildi this_anime_was_watched"">1 серия<span class=""a_dur_line""><span style=""width: 37%; ""></span></span></a>";
        var e = Assert.Single(JutSuParse.ParseEpisodeList(html));
        Assert.True(e.watched);
        Assert.Equal(37, e.percent);
    }

    [Fact]
    public void Хаб_вёрстка_детектится_по_h1()
    {
        // Наруто: страница тайтла = каталог разделов, ни одной short-btn
        var t = JutSuParse.ParseTitle(Fx("title-naruuto-hub.html"), "naruuto");
        Assert.True(t.isHub);
        Assert.Empty(t.items);
        Assert.NotEmpty(t.hubSections);
        Assert.Contains(t.hubSections, s => s.Contains("season-1"));
    }

    [Fact]
    public void Обычная_вёрстка_не_считается_хабом()
    {
        Assert.False(JutSuParse.ParseTitle(Fx("title-spy-family.html")).isHub);
    }

    [Theory]
    [InlineData("/x/season-2/episode-7.html", JutEpKind.Episode, 2, 7, "s2e7")]
    [InlineData("/x/episode-7.html", JutEpKind.Episode, 1, 7, "s1e7")]
    [InlineData("/x/film-3.html", JutEpKind.Film, 1, 3, "film3")]
    [InlineData("/x/ova-2.html", JutEpKind.Ova, 1, 2, "ova2")]
    [InlineData("/x/game-ova-5.html", JutEpKind.GameOva, 1, 5, "gameova5")]
    public void URL_серий_все_пять_шаблонов(string url, JutEpKind kind, int season, int num, string key)
    {
        var e = JutSuParse.ParseEpUrl(url);
        Assert.NotNull(e);
        Assert.Equal(kind, e.kind);
        Assert.Equal(season, e.season);
        Assert.Equal(num, e.num);
        Assert.Equal(key, e.epkey);
    }

    [Fact]
    public void Мусорные_URL_отбиваются()
    {
        Assert.Null(JutSuParse.ParseEpUrl("/x/season-1/"));
        Assert.Null(JutSuParse.ParseEpUrl("/anime/comedy/"));
        Assert.Null(JutSuParse.ParseEpUrl(null));
    }

    #endregion

    #region страница серии

    [Fact]
    public void Выбор_max_качества_и_чистка_url()
    {
        var r = JutSuParse.ParseEpisode(Fx("episode-authorized.html"));
        Assert.Null(r.error);
        Assert.True(r.ok);

        Assert.Equal(new[] { 1080, 720, 480, 360 }, r.videos.Select(v => v.res).ToArray());
        var best = JutSuParse.PickQuality(r.videos, 0);
        Assert.Equal(1080, best.res);

        // derou (= dle_user_id) и hash2 вырезаны; hash обязан остаться
        Assert.DoesNotContain("derou=", best.url);
        Assert.DoesNotContain("hash2=", best.url);
        Assert.Contains("hash=", best.url);
        Assert.Contains(".mp4", best.url);
    }

    [Fact]
    public void Pixel_png_даёт_NOT_AUTHORIZED()
    {
        // 🔥 Без кук разметка ЦЕЛАЯ (label/res на месте) — меняется только src.
        // Без явного детекта это выглядело бы как «успешный парсинг с битыми ссылками».
        var r = JutSuParse.ParseEpisode(Fx("episode-anon.html"));
        Assert.Equal("NOT_AUTHORIZED", r.error);
        Assert.False(r.ok);
        Assert.Empty(r.videos);
    }

    [Fact]
    public void Неполный_набор_качеств_не_ломает_выбор()
    {
        // OVA Наруто отдаёт только 480/360 — предполагать наличие 1080 нельзя
        var r = JutSuParse.ParseEpisode(Fx("episode-ova-480.html"));
        Assert.Null(r.error);
        Assert.Equal(new[] { 480, 360 }, r.videos.Select(v => v.res).ToArray());
        Assert.Equal(480, JutSuParse.PickQuality(r.videos, 0).res);
        Assert.Equal(480, JutSuParse.PickQuality(r.videos, 1080).res);   // потолок выше доступного
    }

    [Fact]
    public void Потолок_качества_соблюдается()
    {
        var vids = new[] { 1080, 720, 480, 360 }
            .Select(r => new JutVideo { res = r, url = "u" + r }).ToList();
        Assert.Equal(1080, JutSuParse.PickQuality(vids, 0).res);      // 0 = всегда максимум
        Assert.Equal(720, JutSuParse.PickQuality(vids, 720).res);
        Assert.Equal(480, JutSuParse.PickQuality(vids, 600).res);     // ближайшее не выше потолка
        Assert.Equal(360, JutSuParse.PickQuality(vids, 100).res);     // все выше → наименьшее
        Assert.Null(JutSuParse.PickQuality(new System.Collections.Generic.List<JutVideo>(), 0));
    }

    [Fact]
    public void Длительность_и_эндинг_вытаскиваются()
    {
        var r = JutSuParse.ParseEpisode(Fx("episode-authorized.html"));
        Assert.Equal(1450, r.duration);      // <meta itemprop="duration" content="T24M10S">
        Assert.True(r.outro > 0, "video_outro_start лежит в base64 внутри <script>");
        Assert.True(r.outro < r.duration);
    }

    [Fact]
    public void Одна_озвучка_на_реальной_странице()
    {
        // Механика мультиозвучек в HTML заложена, но контента пока нет.
        // Правило при >1: берём ПЕРВЫЙ блок (зафиксировано в 04-parser-map.md).
        Assert.Equal(1, JutSuParse.ParseEpisode(Fx("episode-authorized.html")).voiceCount);
    }

    [Fact]
    public void Две_озвучки_выбирают_первую()
    {
        string html =
            @"<span class=""wap_player wap_active"" id=""wap_player_1"" data-player-1080=""https://cdn/a.mp4?hash=1"">Озвучка A</span>" +
            @"<span class=""wap_player"" id=""wap_player_2"" data-player-1080=""https://cdn/b.mp4?hash=2"">Озвучка B</span>";
        var r = JutSuParse.ParseEpisode(html);
        Assert.Equal(2, r.voiceCount);
        Assert.Contains("a.mp4", r.videos[0].url);
    }

    [Fact]
    public void Пустая_страница_даёт_PARSE()
    {
        Assert.Equal("PARSE", JutSuParse.ParseEpisode("<html><body>тут ничего нет</body></html>").error);
        Assert.Equal("PARSE", JutSuParse.ParseEpisode("").error);
    }

    [Fact]
    public void Source_используется_как_фолбэк()
    {
        // wap_player нет — берём <source>
        string html = @"<video id=""my-player""><source src=""https://cdn/x.mp4?hash=9"" type=""video/mp4"" lang=""ru"" label=""720p"" res=""720""/></video>";
        var r = JutSuParse.ParseEpisode(html);
        Assert.Null(r.error);
        Assert.Equal(720, r.videos[0].res);
    }

    #endregion

    #region маркеры reached и утилиты

    [Fact]
    public void Reached_отвечает_дошли_ли_до_сайта_а_не_залогинены_ли()
    {
        // 🔥 ИНВАРИАНТ: страница с pixel.png — это reached=true, authorized=false.
        // Смешаешь — протухшие куки сожгут бюджет прокси-выходов за один тик.
        string anon = Fx("episode-anon.html");
        Assert.True(JutSuParse.ReachedEpisode(anon));
        Assert.Equal("NOT_AUTHORIZED", JutSuParse.ParseEpisode(anon).error);
    }

    [Fact]
    public void Reached_маркеры_на_реальных_страницах()
    {
        Assert.True(JutSuParse.ReachedCatalog(Fx("catalog-ajax.html")));
        Assert.True(JutSuParse.ReachedTitle(Fx("title-spy-family.html")));
        Assert.True(JutSuParse.ReachedSection(Fx("title-naruuto-hub.html")));
        Assert.True(JutSuParse.ReachedEpisode(Fx("episode-authorized.html")));

        // заглушка провайдера / чужая страница
        Assert.False(JutSuParse.ReachedCatalog("<html><body>Access denied</body></html>"));
        Assert.False(JutSuParse.ReachedEpisode("<html><body>captcha</body></html>"));
    }

    [Fact]
    public void Канонический_слаг_из_canonical()
    {
        Assert.Equal("spy-family", JutSuParse.CanonicalSlug(Fx("title-spy-family.html")));
        Assert.Null(JutSuParse.CanonicalSlug("<html></html>"));
    }

    [Theory]
    [InlineData("spy-family", true)]
    [InlineData("oneepiece", true)]
    [InlineData("../../etc/passwd", false)]
    [InlineData("a/b", false)]
    [InlineData("A-Upper", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Slug_traversal_отбивается(string slug, bool ok)
    {
        // slug идёт в пути на диске (/qdl-data/jut/, /downloads/jutsu/<slug>/) и в URL к сайту
        Assert.Equal(ok, JutSuParse.IsValidSlug(slug));
    }

    [Fact]
    public void CleanCdnUrl_оставляет_только_hash()
    {
        string u = "https://r1.yandexwebcache.org/x/1.1080.abc.mp4?derou=123&hash=deadbeef&hash2=cafe";
        string c = JutSuParse.CleanCdnUrl(u);
        Assert.Equal("https://r1.yandexwebcache.org/x/1.1080.abc.mp4?hash=deadbeef", c);
    }

    #endregion
}
