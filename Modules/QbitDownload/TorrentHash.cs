using BencodeNET.Parsing;
using BencodeNET.Torrents;
using System;
using System.Text.RegularExpressions;

namespace QbitDownload;

// ───────────────────────────────────────────────────────────────────────────────
// Infohash из .torrent-ФАЙЛА.
//
// Зачем: login-трекеры (Кинозал, RuTracker при priority="torrent") отдают раздачу не магнитом,
// а телом .torrent. В /qdl/add такая ветка знала о торренте всё, кроме его хеша — а хеш там
// ключ ко всему: по нему клиент сохраняет карточку (/qdl/save), по нему пишется links/<hash>.json
// (слежение и охота за сериями) и по нему промоутится донор. Без него скачанное оставалось
// безымянным: имя файла вместо карточки, ни описания, ни постера (боевой разбор «Холод», 14.08.2026).
//
// Почему BencodeNET, а не свой парсер: та же DLL уже вендорится в JacRed и DLNA, и JacRed этим же
// приёмом делает магнит из .torrent login-трекеров (Modules/JacRed/Engine/BencodeTo.cs) — то есть
// код работает ровно на тех файлах, которые мы сюда приносим. DLL положена в references/ рядом
// с Npgsql.dll (модуль динамический, references/ подхватывается по manifest.json).
// Базовый тип объявлен в Controller.cs — в partial-классе он указывается один раз.
// ───────────────────────────────────────────────────────────────────────────────
public partial class QbitController
{
    static readonly Regex _btihHexRx = new Regex("^[0-9a-f]{40}$", RegexOptions.Compiled);

    /// <summary>infohash (40 hex, lowercase) из тела .torrent; null — не торрент/не v1/битый файл.</summary>
    internal static string TorrentInfoHash(byte[] torrent)
    {
        if (torrent == null || torrent.Length < 50)
            return null;

        try
        {
            var parsed = new BencodeParser().Parse<Torrent>(torrent);
            if (parsed == null)
                return null;

            // OriginalInfoHash — хеш ИСХОДНОГО info-словаря (не пересобранного), то есть ровно то,
            // чем торрент опознаётся в qBittorrent. Если его нет — вытаскиваем из магнита.
            string h = parsed.OriginalInfoHash;
            if (string.IsNullOrWhiteSpace(h))
                h = MagnetHash(parsed.GetMagnetLink());

            h = (h ?? "").Trim().ToLowerInvariant();

            // v2-only/hybrid дают btmh (64 hex) — по нему qBit торрент не найти, лучше честный null:
            // вызывающий останется на прежнем пути, а карточку донесёт MetaHeal по btih из qBit.
            return _btihHexRx.IsMatch(h) ? h : null;
        }
        catch { return null; }
    }
}
