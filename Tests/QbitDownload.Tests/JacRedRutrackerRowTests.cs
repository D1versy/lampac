using System.Text.RegularExpressions;
using System.Web;
using JacRed.Engine;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Паттерны строки выдачи rutracker (Modules/JacRed/Engine/RutrackerRow.cs) на РЕАЛЬНОМ куске
/// html, снятом с живого трекера 24.08.2026.
///
/// 🔥 Почему тест появился. Rutracker перевёл ссылку на раздел в абсолютную форму
/// (`href="https://rutracker.org/forum/tracker.php?f=796&amp;nm=..."`), а паттерн ждал
/// относительную. Раздел в парсере обязателен — значит отбрасывалась КАЖДАЯ строка. Симптом был
/// незаметный: солвер отдаёт залогиненный html, в логе ни одной ошибки, а раздач ноль. Ровно
/// такое молчаливое расхождение с разметкой и должен ловить этот файл.
///
/// ⚠️ Обе формы ссылки обязаны работать: сайт уже возвращался к относительным и вернётся ещё.
/// </summary>
public class JacRedRutrackerRowTests
{
    // Живая строка (24.08.2026): раздел — АБСОЛЮТНОЙ ссылкой, тема и скачивание — относительными,
    // стрелка размера — живым символом ↓, а не сущностью.
    const string ROW_LIVE = @"
 data-topic_id=""6603614"" role=""row"">
 <td class=""row1 f-name-col"">
 <div class=""f-name""><a class=""gen f ts-text"" href=""https://rutracker.org/forum/tracker.php?f=796&amp;nm=%D1%EB%EE%E2%EE+%EF%E0%F6%E0%ED%E0"">Неофициальные саундтреки</a></div>
 </td>
 <td class=""row4 med tLeft t-title-col tt"">
 <a data-topic_id=""6603614"" class=""med tLink tt-text ts-text hl-tags bold"" href=""viewtopic.php?t=6603614"">Слово пацана. Кровь на асфальте: Музыка из телесериала <span class=""brackets-pair"">[2023, MP3, 320 kbps]</span></a>
 </td>
 <td class=""row1 u-name-col"">
 <div class=""wbr u-name""><a class=""med ts-text"" href=""tracker.php?pid=12882498"">KENT_light</a></div>
 </td>
 <td class=""row4 small nowrap tor-size"" data-ts_text=""543127111"">
 <a class=""small tr-dl dl-stub"" href=""dl.php?t=6603614"">518&nbsp;MB ↓</a> </td>
 <td class=""row4 nowrap"" data-ts_text=""5"">
 <b class=""seedmed"">5</b> </td>
 <td class=""row4 leechmed bold"" title=""Личи"">1</td>
 <td class=""row4 small nowrap"" data-ts_text=""1732350645"">
 <p>23-Ноя-24</p>
 </td>";

    // Прежняя форма разметки: раздел относительной ссылкой, стрелка размера — сущностью.
    const string ROW_OLD = @"
 <div class=""f-name""><a class=""gen f ts-text"" href=""tracker.php?f=796&amp;nm=x"">Неофициальные саундтреки</a></div>
 <a class=""med tLink"" href=""viewtopic.php?t=6603614"">Слово пацана. Кровь на асфальте</a>
 <a class=""small tr-dl dl-stub"" href=""dl.php?t=6603614"">518&nbsp;MB &#8595;</a>
 <b class=""seedmed"">5</b>
 <td class=""row4 leechmed bold"" title=""Личи"">1</td>
 <p>23-Ноя-24</p>";

    /// <summary>Ровно то, что делает parsePage: одно совпадение + HtmlDecode + схлопывание пробелов.</summary>
    static string Match(string row, string pattern)
    {
        string res = HttpUtility.HtmlDecode(new Regex(pattern, RegexOptions.IgnoreCase).Match(row).Groups[1].Value.Trim());
        return Regex.Replace(res, "[\n\r\t ]+", " ").Trim();
    }

    [Theory]
    [InlineData(ROW_LIVE)]
    [InlineData(ROW_OLD)]
    public void Forum_id_is_parsed_from_both_link_forms(string row)
    {
        // 🔥 Раздел обязателен: пустая строка здесь = раздача выброшена, трекер отдаёт ноль.
        Assert.Equal("796", Match(row, RutrackerRow.Forum));
    }

    [Theory]
    [InlineData(ROW_LIVE)]
    [InlineData(ROW_OLD)]
    public void Topic_id_and_title_are_parsed(string row)
    {
        Assert.Equal("6603614", Match(row, RutrackerRow.Topic));

        string title = Regex.Replace(Match(row, RutrackerRow.Title), "<[^>]+>", "");
        Assert.StartsWith("Слово пацана. Кровь на асфальте", title);
    }

    [Theory]
    [InlineData(ROW_LIVE)]
    [InlineData(ROW_OLD)]
    public void Size_is_parsed_with_both_arrow_forms(string row)
    {
        // HtmlDecode превращает &nbsp; в U+00A0 — parsePage приводит его к обычному пробелу.
        string size = Match(row, RutrackerRow.Size).Replace("&nbsp;", " ").Replace('\u00A0', ' ');
        Assert.Equal("518 MB", size);
    }

    [Theory]
    [InlineData(ROW_LIVE)]
    [InlineData(ROW_OLD)]
    public void Seeds_peers_and_date_are_parsed(string row)
    {
        Assert.Equal("5", Match(row, RutrackerRow.Seeds));
        Assert.Equal("1", Match(row, RutrackerRow.Peers));
        Assert.Equal("23-Ноя-24", Match(row, RutrackerRow.Created));
    }

    /// <summary>
    /// Ссылка на профиль автора — тоже tracker.php, но с pid. Она не должна сойти за раздел:
    /// иначе тип раздачи определялся бы по случайному числу.
    /// </summary>
    [Fact]
    public void Author_link_is_not_mistaken_for_forum_id()
    {
        const string onlyAuthor = @"<a class=""med ts-text"" href=""tracker.php?pid=12882498"">KENT_light</a>";
        Assert.Equal("", Match(onlyAuthor, RutrackerRow.Forum));
    }
}
