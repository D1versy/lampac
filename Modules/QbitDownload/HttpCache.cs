using Shared.Services.Utilities;
using System;

namespace QbitDownload;

/// <summary>
/// qdl 2.45: ETag/304 для горячих JSON-ручек модуля (/qdl/list, /qdl/collections, /qdl/notifications).
///
/// Зачем. /qdl/list дёргается на КАЖДОЕ открытие любой карточки (qdl.js подписан на 'full') и весит
/// 110 КБ сырых / ~45 КБ br при 65 загрузках. Ответ при этом меняется редко — раз в несколько минут,
/// когда что-то докачалось. Ревалидация по ETag превращает эти 45 КБ в ~150 байт 304-го ответа и
/// снимает с клиента JSON.parse на 110 КБ (заметно на слабом SoC телевизора).
///
/// Почему клиент править не нужно: Lampa.Reguest ходит через $.ajax БЕЗ cache:false и без параметра
/// `_=`, поэтому браузер/WebView сам хранит ответ и сам присылает If-None-Match.
///
/// ⚠️ Заголовок обязан быть `Cache-Control: no-cache`, а НЕ `no-store` (который ставит общий
/// SetHeadersNoCache). Разница принципиальная: no-cache = «храни, но каждый раз перепроверяй» —
/// это и включает ревалидацию; no-store = «не храни вовсе» → клиенту нечего валидировать и 304
/// никогда не случится.
///
/// ⚠️ ETag намеренно СЛАБЫЙ (W/): тело уходит через динамическое сжатие, побайтовой идентичности
/// представления по сети нет — сильный ETag тут был бы враньём.
/// </summary>
internal static class HttpCache
{
    /// <summary>ETag тела ответа. null для null-тела (нечего валидировать).</summary>
    public static string Etag(string body)
        => body == null ? null : "W/\"" + Fnv1a.HashName(body) + "\"";

    /// <summary>
    /// Слабое сравнение If-None-Match по RFC 9110 §8.8.3.2: сопоставляются только «непрозрачные»
    /// части тегов, префикс W/ при этом игнорируется. Заголовок может нести список через запятую
    /// и спецзначение "*".
    /// </summary>
    public static bool IfNoneMatchHit(string header, string etag)
    {
        if (string.IsNullOrWhiteSpace(header) || string.IsNullOrEmpty(etag))
            return false;

        string mine = Opaque(etag);
        if (mine == null)
            return false;

        if (header.Trim() == "*")
            return true;

        foreach (var part in header.Split(','))
        {
            string other = Opaque(part);
            if (other != null && string.Equals(other, mine, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>`W/"abc"` и `"abc"` → `abc`; всё, что не похоже на entity-tag → null.</summary>
    static string Opaque(string tag)
    {
        if (tag == null)
            return null;

        string s = tag.Trim();
        if (s.StartsWith("W/", StringComparison.Ordinal))
            s = s.Substring(2).TrimStart();

        // Пустой тег `""` тоже валиден по грамматике, но как валидатор бесполезен — гоним в null,
        // иначе два разных ответа с пустым ETag «совпали» бы между собой.
        if (s.Length < 3 || s[0] != '"' || s[s.Length - 1] != '"')
            return null;

        return s.Substring(1, s.Length - 2);
    }
}
