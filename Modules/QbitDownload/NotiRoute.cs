using System;
using System.Collections.Generic;
using System.Linq;

namespace QbitDownload;

// ─────────────────────────────────────────────────────────────────────────────
// Кому какое уведомление (qdl 2.111) и какими словами.
//
// Запрос владельца: «допустим уведомить что вышла новая серия 10 и всё, а дальше уже под
// капотом обновлять, перекачивать и прокручивать всю логику. Если раздачи 5 раз поменялись
// или перекачивается новая раздача — не нужно об этом уведомлять».
//
// Замер боевой ленты на 05.09.2026 (50 строк): 21 из них — кухня (START «раздача обновилась»,
// SWITCH «найдена более полная раздача… переключение перекачает сезон заново», DIAG), плюс
// дубли, где одна серия давала до четырёх строк.
//
// Отсюда два потока:
//   • ЗРИТЕЛЮ (таблица noti, /qdl/notifications) — только «вышла новая серия/сезон» и итог
//     большой пачки;
//   • ВЛАДЕЛЬЦУ (EventLog.cs → events.json → вкладка «Уведомления» в /admin/d1v) — всё
//     остальное: постановка в очередь, смена раздачи, охота, доноры, качество, диагностика.
//
// 🔴 Виды здесь — ЕДИНСТВЕННЫЙ источник правды и для пишущей стороны (продюсеры не создают
// строку noti для демотированного вида), и для читающей (Notifications() фильтрует выдачу).
// Двойная защита нужна, потому что в таблице уже лежат СТАРЫЕ служебные строки: без фильтра
// на чтении лента очистилась бы только по мере ретенции, то есть неделями.
// ─────────────────────────────────────────────────────────────────────────────
internal static class NotiRoute
{
    /// <summary>Киллсвитч: notiSplit=false возвращает прежнее поведение — всё летит зрителю.</summary>
    internal static bool Enabled => ModInit.conf?.notiSplit != false;

    // kind == null — обычная докачавшаяся серия, самый частый случай.
    // NEW пишется в noti ТОЛЬКО тайтлами в режиме «только уведомляю» (там качать нечего, и
    // сообщить о выходе больше нечем); на автокачке зритель узнаёт о серии, когда её можно
    // смотреть, — решение владельца.
    // Массив, а не только множество: этот же список уходит в EF-запрос ленты
    // (Contains → SQL IN), а HashSet EF в SQLite не транслирует.
    internal static readonly string[] UserKinds =
    {
        "OVA", "ONA", "OAD", "SP", "SPECIAL", "FILM", "GAMEOVA", "GAME-OVA", "RANGE",
        "WAVE", "SEASON", "TITLE", "NEW"
    };

    static readonly HashSet<string> _user = new(UserKinds, StringComparer.OrdinalIgnoreCase);

    /// <summary>Видит ли этот вид зритель.</summary>
    internal static bool UserKind(string kind)
    {
        if (!Enabled) return true;
        if (string.IsNullOrEmpty(kind)) return true;
        return _user.Contains(kind);
    }

    #region формулировки

    /// <summary>
    /// Волна новых серий одним текстом. Сезон дописываем только при season > 1: у почти всех
    /// тайтлов он единственный, и «· сезон 1» — шум в каждой строке.
    /// </summary>
    internal static string Episodes(int season, IEnumerable<int> eps)
    {
        var e = (eps ?? Enumerable.Empty<int>()).Where(x => x >= 0).Distinct().OrderBy(x => x).ToList();
        if (e.Count == 0) return null;

        string tail = season > 1 ? " · сезон " + season : "";

        if (e.Count == 1) return "Вышла новая серия " + e[0] + tail;
        if (e[e.Count - 1] - e[0] + 1 == e.Count) return "Вышли новые серии " + e[0] + "–" + e[e.Count - 1] + tail;
        return "Вышло новых серий: " + e.Count + tail;
    }

    internal static string Season(int n) => "Вышел сезон " + n;

    /// <summary>Итог пачки. upTo > 0 — пачка была перекачкой ради качества.</summary>
    internal static string Batch(int done, int total, int upTo)
    {
        if (upTo > 0) return "Качество улучшено: " + done + " серий (до " + upTo + "p)";
        return total > 0 && total != done
            ? "Скачано серий: " + done + " из " + total
            : "Скачано серий: " + done;
    }

    #endregion

    /// <summary>
    /// Ключ дедупа волны. Собирается по МАКСИМАЛЬНОЙ серии волны — повторный прогон с тем же
    /// результатом (например, после отката SaveChanges) не создаст вторую строку, а следующая
    /// серия даст новый ключ. С эпизодными ключами (e7 / s3e7) не пересекается по префиксу,
    /// поэтому UNIQUE(seriesKey, epkey) не схлопнет волну со строкой самой серии.
    /// </summary>
    internal static string WaveKey(int season, IEnumerable<int> eps)
    {
        var e = (eps ?? Enumerable.Empty<int>()).Where(x => x >= 0).ToList();
        if (e.Count == 0) return null;
        return "wave-" + (season >= 0 ? "s" + season : "") + "e" + e.Max();
    }
}
