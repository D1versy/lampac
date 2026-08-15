using Newtonsoft.Json;
using Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Online;

/// <summary>
/// qdl 2.45: L2 на диске для результата checkOnlineSearch — набора «какие балансеры знают эту
/// карточку», который показывается кнопками «Онлайн».
///
/// Зачем. Замер: полный набор из 23 балансеров собирается 8.2 с (life-режим отдаёт memkey за 16 мс,
/// но кнопки доезжают по мере ответов: 45 мс — 7 из 23, 1.1 с — 18, 3.1 с — 21, 8.2 с — все).
/// Жил этот набор только в memoryCache процесса, поэтому терялся при каждом рестарте контейнера —
/// а хост падает по питанию ~23 раза в месяц. Теперь набор лежит на ext4-томе (/lampac/cache/…,
/// NVMe) и переживает рестарт: первый же клиент после старта получает ready=true сразу.
///
/// Что тут ВАЖНО не сломать (инварианты):
///  • ⚠️ Пишем ТОЛЬКО полный набор — все links[i] != null. Обрезанный снимок дал бы LifeEvents
///    вечный ready=true на неполном списке кнопок (там ready = onlineItems.Count == links.Count).
///  • ⚠️ Не пишем «массовый сбой». Если доля рабочих балансеров ниже порога — это упавший
///    flaresolverr/VPN/интернет, а не «источников нет». Закрепить такой снимок на сутки означало
///    бы самому себе отобрать все кнопки, причём надолго.
///  • memkey уже включает online.Count (OnlineApi: Fnv1a по id/serial/source/online.Count/uid),
///    поэтому включение или выключение балансера меняет ключ само — протухший набор не всплывёт.
///    Счётчик всё равно дублируется в файле и сверяется на чтении: дешёвая страховка от коллизии.
///  • В code живёт плейсхолдер {localhost}, он подставляется на ЧТЕНИИ (OnlineApi) — снимок
///    host-независим, один и тот же файл обслуживает и LAN, и tv.d1versy.com.
/// </summary>
public static class OnlineEventsCache
{
    public sealed class Item
    {
        public string code { get; set; }
        public int index { get; set; }
        public bool work { get; set; }
    }

    sealed class Snapshot
    {
        public int ver { get; set; }
        public int count { get; set; }         // online.Count на момент снимка
        public long at { get; set; }           // unix UTC
        public List<Item> items { get; set; }
    }

    /// <summary>Доля рабочих балансеров, ниже которой снимок считаем аварийным и не сохраняем.</summary>
    public const double MinWorkShare = 0.30;

    static string Dir()
    {
        string d = Path.Combine("cache", "onlinesearch");
        try { Directory.CreateDirectory(d); } catch { }
        return d;
    }

    static string PathFor(string memkey)
        => Path.Combine(Dir(), Sanitize(memkey) + ".json");

    // memkey приходит из Fnv1a.Base64Url — там возможны '-' и '_', но не разделители пути.
    // Гоним через белый список: ключ приходит из запроса, в имя файла его пускать без проверки нельзя.
    static string Sanitize(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        foreach (char c in key)
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
                return null;
        return key.Length > 64 ? null : key;
    }

    public static int TtlMinutes
    {
        get
        {
            try
            {
                int m = CoreInit.conf.online.checkOnlineSearchMinutes;
                return m > 0 ? m : 5;
            }
            catch { return 5; }
        }
    }

    /// <summary>
    /// Набор полон? Только такой можно сохранять: LifeEvents считает ready по совпадению длин.
    /// </summary>
    public static bool IsComplete(IReadOnlyList<Item> items, int expectedCount)
        => items != null && expectedCount > 0 && items.Count == expectedCount && items.All(i => i?.code != null);

    /// <summary>
    /// Похоже на массовую аварию (упал flaresolverr/VPN/интернет), а не на «источников нет»?
    /// Такой снимок сохранять нельзя — он отберёт кнопки на весь TTL.
    /// </summary>
    public static bool LooksLikeOutage(IReadOnlyList<Item> items)
    {
        if (items == null || items.Count == 0) return true;
        int work = items.Count(i => i != null && i.work);
        return work < items.Count * MinWorkShare;
    }

    public static bool TryLoad(string memkey, int expectedCount, out List<Item> items)
    {
        items = null;
        string path = PathFor(memkey);
        if (path == null || !File.Exists(path))
            return false;

        try
        {
            var snap = JsonConvert.DeserializeObject<Snapshot>(File.ReadAllText(path));
            if (snap?.items == null || snap.ver != 1)
                return false;

            if (expectedCount > 0 && snap.count != expectedCount)
                return false;   // состав балансеров сменился — снимок не про то

            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - snap.at > TtlMinutes * 60L)
            {
                try { File.Delete(path); } catch { }
                return false;
            }

            if (!IsComplete(snap.items, snap.count))
                return false;

            items = snap.items;
            return true;
        }
        catch { return false; }
    }

    public static void TrySave(string memkey, IReadOnlyList<Item> items, int expectedCount)
    {
        string path = PathFor(memkey);
        if (path == null)
            return;

        if (!IsComplete(items, expectedCount) || LooksLikeOutage(items))
            return;

        try
        {
            var snap = new Snapshot
            {
                ver = 1,
                count = expectedCount,
                at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                items = items.ToList()
            };

            // .tmp → Move: обрезанный после падения по питанию JSON должен читаться как «кеша нет»,
            // а не как пустой список кнопок.
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(snap));
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }

    /// <summary>Уборка протухших снимков — зовётся редко (раз в сутки из ModInit модуля).</summary>
    public static int Prune()
    {
        int removed = 0;
        try
        {
            long deadline = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - TtlMinutes * 60L;
            foreach (var f in Directory.EnumerateFiles(Dir(), "*.json"))
            {
                try
                {
                    var snap = JsonConvert.DeserializeObject<Snapshot>(File.ReadAllText(f));
                    if (snap == null || snap.at < deadline) { File.Delete(f); removed++; }
                }
                catch { try { File.Delete(f); removed++; } catch { } }
            }
        }
        catch { }
        return removed;
    }
}
