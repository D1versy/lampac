using System;

namespace QbitDownload;

// ─────────────────────────────────────────────────────────────────────────────
// СДВИГ ВРЕМЕННЫХ МЕТОК MPEG-TS — «весь день одним таймлайном» для D1versy Rec.
//
// ЗАЧЕМ. Сутки склеиваются из N записей, и каждую регистратор ремуксит ОТДЕЛЬНЫМ прогоном
// (ffmpeg -c copy -f hls, vod_service.py), поэтому PTS каждого куска начинается заново (~1.4 с).
// Раньше куски сшивались тегом EXT-X-DISCONTINUITY — и libVLC на таком плейлисте отдаёт
// позицию ВНУТРИ текущего куска, а не глобальную.
//
// Замерено настоящим VLC на боевом плейлисте (камера 6, 2026-08-21, length=7552 c):
//   seek → 6300 c  ⇒  отчёт времени 6300 → 0 → 2 → 4 → 7 → 9 → 11 …
// 6300 c приходится на 7-й кусок, локально 4 мин 40 с — ровно то, что видел владелец
// («перемотал на 1:45, а на таймлайне 6 минут»). На Android этим же отравлена относительная
// перемотка: seekBy считает цель от локальной позиции, base+30 с целится в начало суток.
//
// ⚠️ ДВА ДЕШЁВЫХ ЛЕЧЕНИЯ ПРОВЕРЕНЫ И ОТВЕРГНУТЫ (тем же VLC, на пропатченном плейлисте):
//   • EXT-X-PROGRAM-DATE-TIME на каждом блоке + EXT-X-DISCONTINUITY-SEQUENCE (RFC 8216
//     это даже SHOULD) — время всё равно схлопывается: 987 → 0 → 2 → 4 → 7;
//   • просто убрать EXT-X-DISCONTINUITY, оставив рваный PTS — СТАНОВИТСЯ ХУЖЕ: воспроизведение
//     виснет, второй seek роняет плеер в stopped.
// Поэтому лечим единственным работающим способом: делаем таймлайн суток непрерывным.
//
// ЧТО ДЕЛАЕМ. Сдвигаем PTS/DTS (в PES-заголовках) и PCR/OPCR (в adaptation field) каждого куска
// на его смещение в дне. Тогда разрыва во времени НЕТ, теги разрыва не нужны, и плеер видит одну
// сплошную ленту. Проверено прототипом на боевых сегментах:
//   seek → 6300: 6300 6300 6302 6305 6307 6309 6311 …   ✅
//   seek → 3500: 3500 3500 3503 3505 3507 3509           ✅
//
// ПОЧЕМУ ЭТО БЕЗОПАСНО. Разрыв был ТОЛЬКО во времени: все записи одной камеры структурно
// идентичны — видео PID 0x100 (h264 High L4.0 1920×1080 yuv420p), аудио PID 0x101 (AAC LC 44100),
// одинаковый start_time (проверено ffprobe по трём записям). Ни PID, ни кодеки, ни формат между
// кусками не меняются, так что EXT-X-DISCONTINUITY там был не нужен ни по какому другому
// признаку из RFC 8216 §4.3.2.3.
//
// ⚠️ ОБА ПОТОКА СДВИГАЮТСЯ НА ОДНУ И ТУ ЖЕ ДЕЛЬТУ — иначе разъехались бы звук и картинка.
// Поэтому база берётся ОДНА на запись (первая встреченная PTS её первого сегмента), а не своя
// на каждый поток и не своя на каждый сегмент.
// ─────────────────────────────────────────────────────────────────────────────
public static class LiveTs
{
    public const int PacketSize = 188;      // размер TS-пакета
    public const byte Sync = 0x47;          // sync_byte
    public const long Hz = 90000;           // тактовая PTS/DTS и базы PCR
    public const long Mod = 1L << 33;       // PTS/DTS/PCR_base — 33 бита, дальше заворот

    /// <summary>Дельта сдвига: «поставить начало записи ровно на targetTicks».</summary>
    /// <remarks>
    /// Считается по модулю 2^33 — арифметика заворота та же, что у самих меток. Сутки (86400 с =
    /// 7.78e9 тактов) в 33 бита (8.59e9) помещаются, так что внутри дня заворота не случается,
    /// но модуль оставлен: он бесплатен и снимает целый класс граблей.
    /// </remarks>
    public static long Delta(long targetTicks, long baseTicks)
    {
        long d = (targetTicks - baseTicks) % Mod;
        return d < 0 ? d + Mod : d;
    }

    /// <summary>Секунды → такты 90 кГц.</summary>
    public static long Ticks(double seconds) => (long)Math.Round(seconds * Hz);

    /// <summary>
    /// Первая PTS в куске (любого потока) — база для сдвига всей записи.
    /// null, если PES с меткой не нашёлся (тогда сдвигать нечего и звать Shift нельзя).
    /// </summary>
    public static long? FirstPts(byte[] ts, int length = -1)
    {
        if (ts == null) return null;
        int len = length < 0 ? ts.Length : Math.Min(length, ts.Length);

        for (int o = 0; o + PacketSize <= len; o += PacketSize)
        {
            if (ts[o] != Sync) continue;
            if ((ts[o + 1] & 0x40) == 0) continue;              // не начало PES

            int p = PayloadStart(ts, o);
            if (p < 0) continue;

            if (!IsPesWithTimestamps(ts, p, o + PacketSize)) continue;

            int flags = ts[p + 7];
            if ((flags & 0x80) == 0) continue;                  // PTS_DTS_flags без PTS
            if (p + 14 > o + PacketSize) continue;

            return ReadTs(ts, p + 9);
        }

        return null;
    }

    /// <summary>
    /// Сдвинуть ВСЕ метки куска на delta (правится буфер на месте).
    /// Возвращает число исправленных меток — 0 значит «кусок не разобрался», такой отдаём как есть.
    /// </summary>
    public static int Shift(byte[] ts, long delta, int length = -1)
    {
        if (ts == null) return 0;
        int len = length < 0 ? ts.Length : Math.Min(length, ts.Length);
        delta %= Mod;
        if (delta < 0) delta += Mod;
        if (delta == 0) return 0;

        int fixedCount = 0;

        for (int o = 0; o + PacketSize <= len; o += PacketSize)
        {
            if (ts[o] != Sync) continue;

            int afc = (ts[o + 3] >> 4) & 0x3;
            int p = o + 4;

            // ── adaptation field: PCR и OPCR ──
            if ((afc & 0x2) != 0)
            {
                int afLen = ts[p];
                if (afLen > 0 && p + 1 + afLen <= o + PacketSize)
                {
                    int af = ts[p + 1];
                    int q = p + 2;

                    if ((af & 0x10) != 0 && q + 6 <= o + PacketSize)   // PCR_flag
                    {
                        ShiftPcr(ts, q, delta);
                        fixedCount++;
                        q += 6;
                    }
                    if ((af & 0x08) != 0 && q + 6 <= o + PacketSize)   // OPCR_flag
                    {
                        ShiftPcr(ts, q, delta);
                        fixedCount++;
                    }
                }
                p += 1 + afLen;
            }

            // ── PES-заголовок: PTS и DTS (только в пакете с началом PES) ──
            if ((afc & 0x1) == 0) continue;
            if ((ts[o + 1] & 0x40) == 0) continue;
            if (p < o + 4 || p + 14 > o + PacketSize) continue;
            if (!IsPesWithTimestamps(ts, p, o + PacketSize)) continue;

            int flags2 = ts[p + 7];
            int hp = p + 9;

            if ((flags2 & 0x80) != 0)                                   // PTS
            {
                WriteTs(ts, hp, (ReadTs(ts, hp) + delta) % Mod);
                fixedCount++;

                if ((flags2 & 0x40) != 0 && hp + 10 <= o + PacketSize)  // DTS
                {
                    WriteTs(ts, hp + 5, (ReadTs(ts, hp + 5) + delta) % Mod);
                    fixedCount++;
                }
            }
        }

        return fixedCount;
    }

    #region разбор

    /// <summary>Начало payload пакета с учётом adaptation field; -1 — payload нет.</summary>
    static int PayloadStart(byte[] ts, int o)
    {
        int afc = (ts[o + 3] >> 4) & 0x3;
        if ((afc & 0x1) == 0) return -1;

        int p = o + 4;
        if ((afc & 0x2) != 0)
        {
            int afLen = ts[p];
            p += 1 + afLen;
            if (p >= o + PacketSize) return -1;
        }
        return p;
    }

    /// <summary>
    /// Это PES, у которого вообще бывают метки? Проверяем start code 00 00 01 и отсеиваем
    /// служебные stream_id (padding, private_2, PSM/PSD и прочие «без PTS» из ISO 13818-1).
    /// Start code заодно отсекает PSI: PAT/PMT после pointer_field идут как 00 00 B0.
    /// </summary>
    static bool IsPesWithTimestamps(byte[] ts, int p, int end)
    {
        if (p + 9 > end) return false;
        if (ts[p] != 0x00 || ts[p + 1] != 0x00 || ts[p + 2] != 0x01) return false;

        byte sid = ts[p + 3];
        if (sid == 0xBC || sid == 0xBE || sid == 0xBF) return false;    // PSM, padding, private_2
        if (sid == 0xF0 || sid == 0xF1 || sid == 0xF2) return false;    // ECM, EMM, DSMCC
        if (sid == 0xF8 || sid == 0xFF) return false;                   // H.222.1 E, PSD

        return (ts[p + 6] & 0xC0) == 0x80;                              // маркер 10b у PES-заголовка
    }

    /// <summary>33-битная метка из 5 байт PES-заголовка.</summary>
    static long ReadTs(byte[] b, int i) =>
        ((long)(b[i] >> 1 & 0x07) << 30) |
        ((long)b[i + 1] << 22) |
        ((long)(b[i + 2] >> 1 & 0x7F) << 15) |
        ((long)b[i + 3] << 7) |
        ((long)(b[i + 4] >> 1 & 0x7F));

    /// <summary>Записать 33-битную метку, сохранив 4 бита типа в старшем ниббле.</summary>
    static void WriteTs(byte[] b, int i, long v)
    {
        byte marker = (byte)(b[i] & 0xF0);
        b[i] = (byte)(marker | ((v >> 30) & 0x07) << 1 | 1);
        b[i + 1] = (byte)((v >> 22) & 0xFF);
        b[i + 2] = (byte)(((v >> 15) & 0x7F) << 1 | 1);
        b[i + 3] = (byte)((v >> 7) & 0xFF);
        b[i + 4] = (byte)((v & 0x7F) << 1 | 1);
    }

    /// <summary>PCR/OPCR: 33 бита базы (90 кГц) + 6 зарезервированных + 9 бит расширения (27 МГц).</summary>
    static void ShiftPcr(byte[] b, int q, long delta)
    {
        long basePcr =
            ((long)b[q] << 25) | ((long)b[q + 1] << 17) | ((long)b[q + 2] << 9) |
            ((long)b[q + 3] << 1) | ((long)b[q + 4] >> 7);
        int ext = ((b[q + 4] & 0x01) << 8) | b[q + 5];

        basePcr = (basePcr + delta) % Mod;

        b[q] = (byte)((basePcr >> 25) & 0xFF);
        b[q + 1] = (byte)((basePcr >> 17) & 0xFF);
        b[q + 2] = (byte)((basePcr >> 9) & 0xFF);
        b[q + 3] = (byte)((basePcr >> 1) & 0xFF);
        b[q + 4] = (byte)(((int)(basePcr & 1) << 7) | 0x7E | ((ext >> 8) & 0x01));
        b[q + 5] = (byte)(ext & 0xFF);
    }

    #endregion
}
