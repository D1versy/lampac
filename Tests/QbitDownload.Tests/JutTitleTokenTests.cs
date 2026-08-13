using System.Linq;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Xunit;

namespace QbitDownload.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Токен потока на каждую серию в ответе /qdl/jut/title (qdl 2.42).
//
// Зачем: плеер строит плейлист сезона ЗАРАНЕЕ, а /qdl/jut/stream резолвит ссылку по токену.
// Раньше клиент подставлял соседним сериям пустой t= — автопереход N→N+1 онлайн упирался
// в NotFound, хотя таймлайн у элементов был проставлен.
//
// Цена решения — один HMAC на серию, сети тут нет. Условие пригодности: подпись обязана быть
// ДЕТЕРМИНИРОВАННОЙ, иначе закешированный JSON тайтла протухал бы вместе с токенами.
// ─────────────────────────────────────────────────────────────────────────────
public class JutTitleTokenTests
{
    static JutTitle Title() => new JutTitle
    {
        slug = "liar-game",
        titleRu = "Игра лжецов",
        items =
        {
            new JutEp { kind = JutEpKind.Episode, season = 1, num = 1 },
            new JutEp { kind = JutEpKind.Episode, season = 1, num = 7 },
            new JutEp { kind = JutEpKind.Film, season = 1, num = 2 },
            new JutEp { kind = JutEpKind.GameOva, season = 1, num = 1 },
        }
    };

    static JArray Items(JutTitle t) => (JArray)((JObject)Access.Call("JutTitleJson", t))["items"];

    [Fact]
    public void Токен_есть_у_каждой_серии_и_разбирается_обратно()
    {
        var t = Title();
        var items = Items(t);
        Assert.Equal(t.items.Count, items.Count);

        for (int i = 0; i < items.Count; i++)
        {
            var src = t.items[i];
            string tok = items[i].Value<string>("tok");
            Assert.False(string.IsNullOrEmpty(tok));

            var link = JutNet.ParseToken(tok);
            Assert.NotNull(link);
            Assert.Equal(t.slug, link.slug);
            Assert.Equal(src.num, link.ep);
            if (src.kind == JutEpKind.Episode) Assert.Equal(src.season, link.season);
            // kind в токене — параметр роутов (game-ova через дефис), а не имя enum
            Assert.Equal(QbitController.JutKindParam(src.kind), link.kind);
        }
    }

    [Fact]
    public void Токен_детерминирован_иначе_кеш_тайтла_отдавал_бы_мёртвые_ссылки()
    {
        var a = Items(Title()).Select(x => x.Value<string>("tok")).ToArray();
        var b = Items(Title()).Select(x => x.Value<string>("tok")).ToArray();
        Assert.Equal(a, b);
    }

    [Fact]
    public void Разные_серии_разные_токены()
    {
        var toks = Items(Title()).Select(x => x.Value<string>("tok")).ToArray();
        Assert.Equal(toks.Length, toks.Distinct().Count());
    }
}
