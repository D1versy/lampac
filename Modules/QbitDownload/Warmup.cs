using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace QbitDownload;

// ── Прогрев кеша: GET /qdl/warmup?hash=&index= ──
// Клиент (qdl.js) дёргает эндпоинт при открытии карточки скачанного и при старте серии (греем
// следующую). Ответ мгновенный ({queued}); одиночный фоновый воркер (паттерн _tcQueue/KickWorker)
// читает голову+хвост файла в page cache WSL-VM и греет кеши резолва/ffprobe — старт плейбека
// и /qdl/audio после этого идут из RAM, без холодного HDD за 9p и без запуска ffprobe.
public partial class QbitController : BaseController
{
    sealed class WuItem { public string hash; public int index; }

    static readonly ConcurrentQueue<WuItem> _wuQueue = new();
    static readonly HashSet<string> _wuQueued = new(StringComparer.Ordinal);       // дедуп очереди (под _wuLock)
    static readonly object _wuLock = new();
    static readonly ConcurrentDictionary<string, DateTime> _wuWarmed = new();      // путь → когда прогрет
    static int _wuWorker = 0;
    const int WuQueueCap = 16;   // кап очереди: worst-case дисковая нагрузка ограничена (эндпоинт за периметром, но всё же)

    [HttpGet, AllowAnonymous]
    [Route("qdl/warmup")]
    public ActionResult Warmup(string hash, int index = -1)
    {
        if (ModInit.conf?.warmupEnabled != true) return Json(new { queued = false, disabled = true });
        if (!ValidHash(hash)) return BadRequest(new { error = "invalid hash" });

        string key = hash.ToLowerInvariant() + ":" + index;
        lock (_wuLock)
        {
            if (_wuQueued.Contains(key)) return Json(new { queued = true, dup = true });
            if (_wuQueue.Count >= WuQueueCap) return Json(new { queued = false, full = true });
            _wuQueued.Add(key);
            _wuQueue.Enqueue(new WuItem { hash = hash, index = index });
        }
        KickWarmup();
        return Json(new { queued = true });
    }

    static void KickWarmup()
    {
        if (Interlocked.CompareExchange(ref _wuWorker, 1, 0) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                while (_wuQueue.TryDequeue(out var it))
                {
                    string key = it.hash.ToLowerInvariant() + ":" + it.index;
                    try { await WarmupOne(it); }
                    catch (Exception ex) { Console.WriteLine("[QbitDownload] warmup: " + ex.Message); }
                    finally { lock (_wuLock) _wuQueued.Remove(key); }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _wuWorker, 0);
                if (!_wuQueue.IsEmpty) KickWarmup();   // страховка от гонки enqueue-после-выхода, как у _tcWorker
            }
        });
    }

    static async Task WarmupOne(WuItem it)
    {
        string path = await ResolveFileCached(it.hash, it.index);   // заодно греет кеш резолва
        if (path == null) return;

        int ttl = Math.Max(1, ModInit.conf.warmupTtlMin);
        if (_wuWarmed.TryGetValue(path, out var t) && (DateTime.UtcNow - t).TotalMinutes < ttl) return;

        ProbeAudioCached(path);   // /qdl/audio при последующем старте — из кеша, без запуска ffprobe

        long len;
        try { len = new FileInfo(path).Length; } catch { return; }   // файл исчез между резолвом и прогревом
        long head = Math.Max(0, ModInit.conf.warmupHeadMb) * 1048576L;
        long tail = Math.Max(0, ModInit.conf.warmupTailMb) * 1048576L;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        long got = await WarmRange(path, 0, Math.Min(head, len));
        if (tail > 0 && len > head)
        {
            long off = Math.Max(head, len - tail);
            got += await WarmRange(path, off, len - off);
        }

        _wuWarmed[path] = DateTime.UtcNow;
        if (_wuWarmed.Count > 512)   // мягкая уборка протухших меток
            foreach (var kv in _wuWarmed)
                if ((DateTime.UtcNow - kv.Value).TotalMinutes > ttl) _wuWarmed.TryRemove(kv.Key, out _);

        Console.WriteLine($"[QbitDownload] warmup {Path.GetFileName(path)}: {got / 1048576} МБ за {sw.ElapsedMilliseconds} мс");
    }

    // Последовательное чтение диапазона: FileShare.ReadWrite (файл может дописывать qBittorrent),
    // bufferSize:1 — без лишнего внутреннего буфера FileStream, читаем своим 1-МБ буфером.
    // Прочитанное оседает в page cache Linux-VM (9p смонтирован с cache=loose) — последующий
    // sendfile из /qdl/stream отдаёт эти байты из RAM.
    static async Task<long> WarmRange(string path, long offset, long count)
    {
        if (count <= 0) return 0;
        var buf = new byte[1048576];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        fs.Seek(offset, SeekOrigin.Begin);
        long total = 0;
        int pace = ModInit.conf?.warmupPaceMs ?? 0;
        while (total < count)
        {
            int n = await fs.ReadAsync(buf, 0, (int)Math.Min(buf.Length, count - total));
            if (n <= 0) break;
            total += n;
            if (pace > 0) await Task.Delay(pace);   // щадящий режим для занятого HDD (warmupPaceMs)
        }
        return total;
    }
}
