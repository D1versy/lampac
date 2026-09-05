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
// рестарт) и те же инварианты: пишем ТОЛЬКО заведомо верный и непустой ответ (сбой и пустую
// ленту не закрепляем); публикуем через `.tmp → File.Move(overwrite)`, чтобы обрезанный после
// падения по питанию файл читался как «копии нет», а не как пустой ряд.
//
// Формат намеренно вырожденный: в файле лежит СЫРОЕ тело ответа и ничего больше — номер страницы
// достаём обратно из тела (PageGuard.Shape), возраст — mtime файла. Нет обёртки — нет двойного
// экранирования json внутри json.
//
// ⚠️ Пишем в `cache/` (том lampac-cache), а НЕ в `/qdl-data`: второе при blue/green принадлежит
// ведущему цвету (JsonStore.WritesEnabled), а CubProxy своей роли не знает. В `cache/` уже пишет
// Online/OnlineEventsCache — прецедент и правило одни. Оба цвета пишут сюда одновременно и ничего
// не ломают: имя временного файла уникально, публикация атомарна.
//
// 🔴 Этот файл НЕ линкуется в тесты — по той же причине, что FilterStore.cs: он ходит на диск и
// держит статику, которая протекала бы между тестами. Под тестами чистый PageGuard.cs.
public static class PageStore
{
    /// <summary>Ниже этого копии не сметаем даже при выключенной подстановке — иначе каталог рос бы без потолка.</summary>
    const int PruneFloorMinutes = 1440;

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

    /// <summary>Запомнить заведомо верный ответ. Зовётся только на вердикте Match с непустыми results.</summary>
    public static void Save(string key, string body)
    {
        if (string.IsNullOrEmpty(body) || body.Length > PageGuard.MaxBodyBytes)
            return;

        string path = PathFor(key);
        if (path == null)
            return;

        try
        {
            // 🔴 Имя временного файла УНИКАЛЬНОЕ. Один ключ получают три входа плюс два цвета при
            // деплое; с детерминированным `path + ".tmp"` два параллельных Save писали бы в один
            // файл, и rename первого публиковал бы то, что второй ещё дописывает — Load прочёл бы
            // обрезанный json, решил «копии нет», и зритель получил бы чужую страницу.
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, body);
            File.Move(tmp, path, overwrite: true);
        }
        catch { }   // копия — страховка, а не обязанность
    }

    /// <summary>
    /// Достать копию. Возраст проверяем по mtime ДО чтения файла; номер страницы — после, как
    /// страховку от коллизии хеша ключа (по построению он совпадает: ключ включает page=N, а
    /// Save зовётся только на Match).
    /// </summary>
    public static string Load(string key, int wanted, int keepMinutes)
    {
        if (keepMinutes <= 0)
            return null;

        string path = PathFor(key);
        if (path == null)
            return null;

        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists)
                return null;

            var at = new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero);
            if (DateTimeOffset.UtcNow - at > TimeSpan.FromMinutes(keepMinutes))
                return null;

            string body = File.ReadAllText(path);
            var (page, _, results) = PageGuard.Shape(body);

            return results > 0 && PageGuard.Usable(page, at, wanted, DateTimeOffset.UtcNow, keepMinutes) ? body : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Уборка — раз в сутки таймером из ModInit. Копии старше keepMinutes (но не моложе суток:
    /// при keepMinutes=0 копии всё равно пишутся, и без пола каталог рос бы бесконечно) плюс
    /// осиротевшие .tmp — их оставляет падение по питанию между WriteAllText и Move.
    /// </summary>
    public static int Prune(int keepMinutes)
    {
        int removed = 0;
        var now = DateTime.UtcNow;
        var deadline = now - TimeSpan.FromMinutes(Math.Max(keepMinutes, PruneFloorMinutes));
        var tmpDeadline = now - TimeSpan.FromHours(1);

        try
        {
            foreach (var f in Directory.EnumerateFiles(Dir()))
            {
                try
                {
                    bool tmp = f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
                    if (!tmp && !f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (File.GetLastWriteTimeUtc(f) < (tmp ? tmpDeadline : deadline))
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
