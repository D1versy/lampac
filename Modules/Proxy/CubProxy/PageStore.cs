using Shared.Services.Utilities;
using System;
using System.IO;

namespace CubProxy;

// ── Копия «последней верной страницы» ряда каталога (qdl 2.112) ──────────────────────────────
// Вторая половина сторожа PageGuard. Когда CUB отдал чужую страницу и повтор мимо его кеша не
// помог, зрителю подставляется последний ответ, у которого номер страницы совпадал с
// запрошенным. Решение владельца: «чужая страница не должна попасть в топ даже на один показ».
//
// Образец — Online/OnlineEventsCache.cs: тот же класс задачи (снимок на томе, переживающий
// рестарт) и те же два инварианта:
//   • пишем ТОЛЬКО заведомо верный ответ — сбой не закрепляем;
//   • .tmp → File.Move(overwrite): обрезанный после падения по питанию файл обязан читаться как
//     «копии нет», а не как пустой ряд (хост падает по питанию ~23 раза в месяц).
//
// Формат намеренно вырожденный: в файле лежит СЫРОЕ тело ответа и ничего больше.
//   • номер страницы достаём обратно из тела (PageGuard.Shape) — дублировать незачем;
//   • возраст копии — mtime файла, отдельного поля не нужно;
//   • нет обёртки — нет двойного экранирования json внутри json и удвоения размера.
//
// ⚠️ Пишем в `cache/` (том lampac-cache), а НЕ в `/qdl-data`. Второе при blue/green принадлежит
// ведущему цвету (JsonStore.WritesEnabled), а CubProxy своей роли не знает и этот инвариант
// нарушил бы. В `cache/` уже пишет Online/OnlineEventsCache — прецедент и правило одни и те же.
//
// 🔴 Этот файл НЕ линкуется в тесты — по той же причине, что FilterStore.cs: он ходит на диск и
// держит статику, которая протекала бы между тестами. Под тестами чистый PageGuard.cs.
public static class PageStore
{
    /// <summary>Тело больше этого не храним: ряд каталога — 7–22 КБ по замеру боевого.</summary>
    const int MaxBodyBytes = 2 * 1024 * 1024;

    static string Dir()
    {
        string d = Path.Combine("cache", "cubrows");
        try { Directory.CreateDirectory(d); } catch { }
        return d;
    }

    // Ключ — апстримный адрес ряда (PageGuard.StoreKey), в нём есть '?', '&', '/' и '='.
    // В имя файла он идти не может, поэтому гоним через тот же Fnv1a.HashName, что и Staticache:
    // на выходе Base64Url, то есть только буквы, цифры, '-' и '_'.
    static string PathFor(string key)
        => string.IsNullOrEmpty(key) ? null : Path.Combine(Dir(), Fnv1a.HashName(key) + ".json");

    /// <summary>Запомнить заведомо верный ответ. Зовётся только на вердикте Match.</summary>
    public static void Save(string key, string body)
    {
        if (string.IsNullOrEmpty(body) || body.Length > MaxBodyBytes)
            return;

        string path = PathFor(key);
        if (path == null)
            return;

        try
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, body);
            File.Move(tmp, path, overwrite: true);
        }
        catch { }   // копия — страховка, а не обязанность
    }

    /// <summary>
    /// Достать копию. Отдаём только ту, у которой номер страницы совпадает с запрошенным и
    /// возраст в пределах keepMinutes — решает чистая PageGuard.Usable.
    /// </summary>
    public static string Load(string key, int wanted, int keepMinutes)
    {
        string path = PathFor(key);
        if (path == null)
            return null;

        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists)
                return null;

            string body = File.ReadAllText(path);
            var at = new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero);
            var (page, _, _) = PageGuard.Shape(body);

            return PageGuard.Usable(page, at, wanted, DateTimeOffset.UtcNow, keepMinutes) ? body : null;
        }
        catch { return null; }
    }

    /// <summary>Уборка протухших копий — раз в сутки таймером из ModInit.</summary>
    public static int Prune(int keepMinutes)
    {
        if (keepMinutes <= 0)
            return 0;

        int removed = 0;

        try
        {
            var deadline = DateTime.UtcNow - TimeSpan.FromMinutes(keepMinutes);

            foreach (var f in Directory.EnumerateFiles(Dir(), "*.json"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(f) < deadline)
                    {
                        File.Delete(f);
                        removed++;
                    }
                }
                catch { }
            }
        }
        catch { }

        return removed;
    }
}
