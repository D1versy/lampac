using Newtonsoft.Json.Linq;
using System;
using System.IO;

namespace CubProxy;

// ── Чтение глобальной настройки фильтра каталога (qdl 2.89) ─────────────────────────────────
// 🔴 Почему через ФАЙЛ, а не через типы. Настройку пишет модуль QbitDownload (там Perms с правом
// «действия», админка и JsonStore), а читать её должен CubProxy. Оба модуля объявлены
// dynamic:true в своих manifest.json и компилируются Roslyn'ом в РАЗНЫЕ сборки
// (Shared/Services/CSharpEval.cs) — CubProxy до типов QbitDownload не дотянется в принципе.
// Общий том остаётся единственным каналом: QbitDownload пишет /qdl-data/catalog-filter.json
// через JsonStore.WriteNow, мы читаем его здесь.
//
// Дешевизна обязательна: Read() зовётся на КАЖДЫЙ запрос каталога. Поэтому значение держим в
// памяти и трогаем диск не чаще раза в CheckSeconds, да и то только чтобы сверить mtime.
public static class FilterStore
{
    const int CheckSeconds = 5;

    static readonly object _lock = new();
    static RowFilter.Conf _cached;
    static DateTime _checkedAt = DateTime.MinValue;
    static DateTime _mtime = DateTime.MinValue;

    /// <summary>
    /// Текущее значение. Файла нет или он битый — фильтр выключен: настройка, которую никто
    /// не включал, не должна начать резать каталог сама по себе.
    /// </summary>
    public static RowFilter.Conf Read(string path)
    {
        if (string.IsNullOrEmpty(path))
            return default;

        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if ((now - _checkedAt).TotalSeconds < CheckSeconds)
                return _cached;

            _checkedAt = now;

            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists)
                {
                    _mtime = DateTime.MinValue;
                    return _cached = default;
                }

                if (fi.LastWriteTimeUtc == _mtime)
                    return _cached;

                _mtime = fi.LastWriteTimeUtc;

                var o = JToken.Parse(File.ReadAllText(path)) as JObject;
                if (o == null)
                    return _cached = default;

                _cached = new RowFilter.Conf(
                    (bool?)o["enabled"] ?? false,
                    (int?)o["movieYear"] ?? 0,
                    (int?)o["tvYear"] ?? 0
                );

                // Порог 0 означал бы «резать всё, у чего год меньше нуля», то есть ничего —
                // но лучше честно выключить, чем притворяться работающим фильтром.
                if (_cached.movieYear <= 0 && _cached.tvYear <= 0)
                    _cached = default;

                return _cached;
            }
            catch
            {
                // Битый файл не должен ронять выдачу каталога — просто не фильтруем.
                return _cached = default;
            }
        }
    }
}
