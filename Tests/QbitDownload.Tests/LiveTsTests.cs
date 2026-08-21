using System;
using QbitDownload;
using Xunit;

namespace QbitDownload.Tests;

// Сдвиг меток MPEG-TS (LiveTs) — то, чем «весь день одной записью» превращается в один сквозной
// таймлайн. Проверяется на синтетических пакетах: их разметка и так полностью описана стандартом,
// а боевой сегмент в тесты не затащить (3 МБ и живой регистратор).
public class LiveTsTests
{
    const long Hz = 90000;

    // ── конструктор пакетов ──────────────────────────────────────────────────

    /// <summary>TS-пакет с началом PES: PTS (и по желанию DTS), опционально PCR в adaptation field.</summary>
    static byte[] PesPacket(long pts, long? dts = null, long? pcr = null, byte streamId = 0xE0)
    {
        var p = new byte[LiveTs.PacketSize];
        for (int i = 0; i < p.Length; i++) p[i] = 0xFF;   // stuffing

        p[0] = 0x47;
        p[1] = 0x40;                                       // payload_unit_start_indicator + PID hi
        p[2] = 0x00;                                       // PID = 0x100
        p[1] |= 0x01;

        int q;
        if (pcr != null)
        {
            p[3] = 0x30;                                   // adaptation + payload
            p[4] = 7;                                      // af length: flags + 6 байт PCR
            p[5] = 0x10;                                   // PCR_flag
            WritePcr(p, 6, pcr.Value, 0);
            q = 4 + 1 + 7;
        }
        else
        {
            p[3] = 0x10;                                   // только payload
            q = 4;
        }

        p[q] = 0x00; p[q + 1] = 0x00; p[q + 2] = 0x01;     // PES start code
        p[q + 3] = streamId;
        p[q + 4] = 0x00; p[q + 5] = 0x00;                  // PES packet length (0 = unbounded)
        p[q + 6] = 0x80;                                   // маркер 10b
        p[q + 7] = dts == null ? (byte)0x80 : (byte)0xC0;  // PTS / PTS+DTS
        p[q + 8] = dts == null ? (byte)5 : (byte)10;       // header data length

        WriteTsField(p, q + 9, pts, dts == null ? (byte)0x20 : (byte)0x30);
        if (dts != null)
            WriteTsField(p, q + 14, dts.Value, 0x10);

        return p;
    }

    static void WriteTsField(byte[] b, int i, long v, byte marker)
    {
        b[i] = (byte)(marker | ((v >> 30) & 0x07) << 1 | 1);
        b[i + 1] = (byte)((v >> 22) & 0xFF);
        b[i + 2] = (byte)(((v >> 15) & 0x7F) << 1 | 1);
        b[i + 3] = (byte)((v >> 7) & 0xFF);
        b[i + 4] = (byte)((v & 0x7F) << 1 | 1);
    }

    static void WritePcr(byte[] b, int q, long basePcr, int ext)
    {
        b[q] = (byte)((basePcr >> 25) & 0xFF);
        b[q + 1] = (byte)((basePcr >> 17) & 0xFF);
        b[q + 2] = (byte)((basePcr >> 9) & 0xFF);
        b[q + 3] = (byte)((basePcr >> 1) & 0xFF);
        b[q + 4] = (byte)(((int)(basePcr & 1) << 7) | 0x7E | ((ext >> 8) & 0x01));
        b[q + 5] = (byte)(ext & 0xFF);
    }

    static long ReadTsField(byte[] b, int i) =>
        ((long)(b[i] >> 1 & 0x07) << 30) | ((long)b[i + 1] << 22) |
        ((long)(b[i + 2] >> 1 & 0x7F) << 15) | ((long)b[i + 3] << 7) | ((long)(b[i + 4] >> 1 & 0x7F));

    static long ReadPcr(byte[] b, int q) =>
        ((long)b[q] << 25) | ((long)b[q + 1] << 17) | ((long)b[q + 2] << 9) |
        ((long)b[q + 3] << 1) | ((long)b[q + 4] >> 7);

    static byte[] Concat(params byte[][] parts)
    {
        var all = new byte[parts.Length * LiveTs.PacketSize];
        for (int i = 0; i < parts.Length; i++)
            Buffer.BlockCopy(parts[i], 0, all, i * LiveTs.PacketSize, LiveTs.PacketSize);
        return all;
    }

    // ── база записи ──────────────────────────────────────────────────────────

    [Fact]
    public void FirstPts_finds_the_first_timestamp()
    {
        var ts = Concat(PesPacket(126000), PesPacket(135000));
        Assert.Equal(126000, LiveTs.FirstPts(ts));
    }

    [Fact]
    public void FirstPts_is_null_when_there_is_no_pes()
    {
        var pat = new byte[LiveTs.PacketSize];
        pat[0] = 0x47; pat[1] = 0x40; pat[2] = 0x00; pat[3] = 0x10;
        pat[4] = 0x00; pat[5] = 0x00; pat[6] = 0xB0;      // pointer_field + table_id + section syntax

        Assert.Null(LiveTs.FirstPts(pat));
        Assert.Null(LiveTs.FirstPts(null));
        Assert.Null(LiveTs.FirstPts(new byte[10]));       // огрызок короче пакета
    }

    [Fact]
    public void FirstPts_skips_padding_stream()
    {
        // 0xBE (padding) меток не несёт — его нельзя принять за базу записи.
        var ts = Concat(PesPacket(1, streamId: 0xBE), PesPacket(126000));
        Assert.Equal(126000, LiveTs.FirstPts(ts));
    }

    // ── сдвиг ────────────────────────────────────────────────────────────────

    [Fact]
    public void Shift_moves_pts_dts_and_pcr_by_the_same_delta()
    {
        long basePts = 126000;                       // 1.4 c — столько ставит ремукс регистратора
        long target = LiveTs.Ticks(6024);            // кусок начинается на 6024-й секунде суток
        long delta = LiveTs.Delta(target, basePts);

        var ts = PesPacket(basePts, dts: basePts - 3000, pcr: basePts - 4500);
        Assert.Equal(3, LiveTs.Shift(ts, delta));    // PCR + PTS + DTS

        int q = 4 + 1 + 7;
        Assert.Equal(target, ReadTsField(ts, q + 9));
        Assert.Equal(target - 3000, ReadTsField(ts, q + 14));
        Assert.Equal(target - 4500, ReadPcr(ts, 6));
    }

    [Fact]
    public void Shift_puts_the_first_frame_exactly_on_the_block_offset()
    {
        // Инвариант всей затеи: после сдвига первая метка куска равна его смещению в сутках.
        // Ровно это и подтвердил ffprobe на боевом сегменте (pts_time = 6024.000000).
        foreach (double offset in new[] { 0d, 1000.00214, 6019.99777, 7023.99756 })
        {
            var ts = PesPacket(126000);
            long delta = LiveTs.Delta(LiveTs.Ticks(offset), 126000);
            LiveTs.Shift(ts, delta);

            Assert.Equal(LiveTs.Ticks(offset), ReadTsField(ts, 4 + 9));
        }
    }

    [Fact]
    public void Shift_keeps_av_sync_because_both_streams_get_one_delta()
    {
        // Видео и звук стартуют с разных меток; после сдвига их РАЗНИЦА обязана сохраниться,
        // иначе звук уедет относительно картинки.
        long video = 126000, audio = 124200;
        long delta = LiveTs.Delta(LiveTs.Ticks(3600), video);

        var ts = Concat(PesPacket(video), PesPacket(audio, streamId: 0xC0));
        LiveTs.Shift(ts, delta);

        long v = ReadTsField(ts, 4 + 9);
        long a = ReadTsField(ts, LiveTs.PacketSize + 4 + 9);
        Assert.Equal(video - audio, v - a);
    }

    [Fact]
    public void Shift_wraps_at_33_bits()
    {
        long near = LiveTs.Mod - 1000;
        var ts = PesPacket(near);
        LiveTs.Shift(ts, 5000);

        Assert.Equal(4000, ReadTsField(ts, 4 + 9));      // (2^33-1000 + 5000) mod 2^33
    }

    [Fact]
    public void Shift_is_a_noop_for_zero_delta_and_for_garbage()
    {
        var ts = PesPacket(126000);
        Assert.Equal(0, LiveTs.Shift(ts, 0));
        Assert.Equal(126000, ReadTsField(ts, 4 + 9));

        Assert.Equal(0, LiveTs.Shift(null, 90000));
        Assert.Equal(0, LiveTs.Shift(new byte[188], 90000));   // нет sync_byte — не наш поток
    }

    [Fact]
    public void Shift_leaves_psi_packets_alone()
    {
        // PAT/PMT попадают под payload_unit_start, но меток не несут: тронуть их — сломать поток.
        var pat = new byte[LiveTs.PacketSize];
        pat[0] = 0x47; pat[1] = 0x40; pat[2] = 0x00; pat[3] = 0x10;
        pat[4] = 0x00; pat[5] = 0x00; pat[6] = 0xB0; pat[7] = 0x0D;

        var copy = (byte[])pat.Clone();
        Assert.Equal(0, LiveTs.Shift(pat, 90000));
        Assert.Equal(copy, pat);
    }

    // ── арифметика дельты ────────────────────────────────────────────────────

    [Fact]
    public void Delta_is_never_negative()
    {
        // Кусок, начинающийся раньше своей базы (offset 0, база 1.4 c), обязан дать положительную
        // дельту по модулю 2^33 — иначе сдвиг ушёл бы в минус и метки стали бы мусором.
        long d = LiveTs.Delta(0, 126000);
        Assert.True(d > 0);
        Assert.Equal(LiveTs.Mod - 126000, d);
    }

    [Fact]
    public void Ticks_rounds_to_the_nearest_tick()
    {
        Assert.Equal(0, LiveTs.Ticks(0));
        Assert.Equal(Hz, LiveTs.Ticks(1));
        Assert.Equal(90000193, LiveTs.Ticks(1000.00214));   // 90000192.6 → к ближайшему
    }
}
