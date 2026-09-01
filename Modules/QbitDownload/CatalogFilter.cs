using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;

namespace QbitDownload;

// ── Глобальная настройка фильтра каталога по году (qdl 2.89) ────────────────────────────────
// Владелец: «резать выдачу CUB по году, порог выставлять руками, отдельно кино и сериалы;
// меняю на одном клиенте — применяется всем; менять могут только те, у кого стоит чекбокс
// „действия“ в /admin/d1v, а если права нет и кто-то дёрнет руками — 403».
//
// Значение ОДНО на весь сервер и живёт на томе. Читает его модуль CubProxy (там же и режет
// тело ответа) — напрямую позвать его нельзя: оба модуля dynamic:true и компилируются
// Roslyn'ом в разные сборки. Общий файл — единственный канал между ними.
//
// 🔴 Пишем через JsonStore.WriteNow, а не Write: значение меняют раз в полгода, а хост падает
// по питанию ~23 раза в месяц (claude/06) — потерять правку в окне дебаунса нечего ради.
public static class CatalogFilter
{
    public const int DefMovieYear = 2020;
    public const int DefTvYear = 2010;

    static readonly object _lock = new();

    // ⚠️ Имя файла и дефолт пути обязаны совпадать с ModuleConf.catalogFilterFile у CubProxy.
    static string StorePath => Path.Combine(ModInit.conf?.cachePath ?? "/qdl-data", "catalog-filter.json");

    /// <summary>Текущее значение; файла нет — дефолты (фильтр выключен).</summary>
    public static JObject Load()
    {
        var root = JsonStore.ReadObject(StorePath);

        return new JObject
        {
            ["ver"] = 1,
            ["enabled"] = (bool?)root?["enabled"] ?? false,
            ["movieYear"] = (int?)root?["movieYear"] ?? DefMovieYear,
            ["tvYear"] = (int?)root?["tvYear"] ?? DefTvYear
        };
    }

    /// <summary>Единственная точка записи. Возвращает то, что легло на диск.</summary>
    public static JObject Save(bool enabled, int movieYear, int tvYear)
    {
        lock (_lock)
        {
            var root = new JObject
            {
                ["ver"] = 1,
                ["enabled"] = enabled,
                ["movieYear"] = movieYear,
                ["tvYear"] = tvYear
            };

            JsonStore.WriteNow(StorePath, root);
            return root;
        }
    }

    /// <summary>Год в разумных пределах. Верхняя граница — следующий год: анонсы уже датированы им.</summary>
    public static bool ValidYear(int year) => year >= 1900 && year <= DateTime.UtcNow.Year + 1;
}

public partial class QbitController
{
    /// <summary>
    /// Текущее значение — открыто всем: по нему клиент рисует поля, секрета там нет,
    /// а сам фильтр всё равно применяет сервер.
    /// </summary>
    [HttpGet, AllowAnonymous]
    [Route("qdl/catalog-filter")]
    public ActionResult CatalogFilterGet()
    {
        // ⚠️ Через Json() отдавать нельзя: MVC настроен DefaultIgnoreCondition=WhenWritingDefault
        // и выбрасывает enabled:false — клиент прочитал бы «поля нет» вместо «выключено».
        return ContentTo(JsonConvert.SerializeObject(CatalogFilter.Load()), "application/json; charset=utf-8");
    }

    /// <summary>
    /// Запись. 🔴 Гейт — первой строкой: право «действия» (manage) из /admin/d1v. Без него 403,
    /// как бы ручку ни дёрнули — из UI её и не покажут, но защита не в этом.
    /// </summary>
    [HttpPost, AllowAnonymous]
    [Route("qdl/catalog-filter")]
    public ActionResult CatalogFilterSet(bool enabled, int movieYear, int tvYear)
    {
        // 🔴 На реплике настройка НЕ редактируется: она приезжает из дома манифестом (qdl 2.90).
        // Иначе локальная правка жила бы до ближайшего тика и молча откатывалась — худший вид
        // поведения. Тот же принцип, что у бэкфилла истории: реплика не источник правды.
        if (ReplicaMode) return StatusCode(403, new { success = false, error = "на реплике настройка приезжает из дома" });

        var mg = ManageDenied(); if (mg != null) return mg;

        if (!CatalogFilter.ValidYear(movieYear) || !CatalogFilter.ValidYear(tvYear))
            return BadRequest(new { error = "год вне диапазона 1900…" + (DateTime.UtcNow.Year + 1) });

        try
        {
            var saved = CatalogFilter.Save(enabled, movieYear, tvYear);
            return ContentTo("{\"success\":true,\"filter\":" + saved.ToString(Formatting.None) + "}", "application/json; charset=utf-8");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] catalog-filter save: " + ex);
            return Json(new { success = false, error = "internal error" });
        }
    }
}
