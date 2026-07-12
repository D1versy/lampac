using System.Collections.Generic;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Сериальная очередь транскода: дедуп по (hash, index), дозапись серий в существующий
/// элемент (авто-транскод докачавшихся), эскалация оверлея до финализации.
/// Воркер на время тестов блокируется (TcWorkerSet(1)) — ffmpeg не запускается.
/// </summary>
[Collection("tcqueue")]
public class TranscodeSeriesTests
{
    static int _seq = 7000;
    // уникальный hash на тест: _tcJobs — глобальный словарь, записи «queued» от прошлого
    // теста дедупятся новым гардом EnqueueTranscode
    static string UniqueHash() => System.Threading.Interlocked.Increment(ref _seq).ToString("D40");

    static void Reset()
    {
        Access.TcWorkerSet(1);   // воркер «занят» → KickWorker не стартует цикл
        Access.TcQueueClear();
    }

    static void Cleanup()
    {
        Access.TcQueueClear();
        Access.TcWorkerSet(0);
    }

    [Fact]
    public void Same_hash_same_index_not_duplicated()
    {
        Reset();
        string h = UniqueHash();
        try
        {
            var f0 = Access.MakeTcFile(0, "/downloads/s/e1.mkv", "/out/e1.mp4.part", "/out/e1.mp4", 100);
            Access.EnqueueTranscodeSeries(h, false, "S", "/out", Access.MakeTcFileList(f0));
            var f0dup = Access.MakeTcFile(0, "/downloads/s/e1.mkv", "/out/e1.mp4.part", "/out/e1.mp4", 100);
            var f1 = Access.MakeTcFile(1, "/downloads/s/e2.mkv", "/out/e2.mp4.part", "/out/e2.mp4", 200);
            Access.EnqueueTranscodeSeries(h, false, "S", "/out", Access.MakeTcFileList(f0dup, f1));

            var snap = Access.TcQueueSnapshot();
            Assert.Single(snap);                       // один элемент очереди на hash
            Assert.Equal(h, snap[0].hash);
            Assert.Equal(new List<int> { 0, 1 }, snap[0].indexes);   // index 0 не задублирован, 1 дописан
            Assert.Equal(2, Access.TcJob(h).filesTotal);            // статус видит рост списка
        }
        finally { Cleanup(); }
    }

    [Fact]
    public void Finalize_escalates_overlay_item()
    {
        Reset();
        string h = UniqueHash();
        try
        {
            var f0 = Access.MakeTcFile(0, "/downloads/s/e1.mkv", "/o/e1.part", "/o/e1.mp4", 1);
            Access.EnqueueTranscodeSeries(h, false, "S", "/o", Access.MakeTcFileList(f0));
            Assert.False(Access.TcQueueSnapshot()[0].finalize);

            var f1 = Access.MakeTcFile(1, "/downloads/s/e2.mkv", "/o/e2.part", "/o/e2.mp4", 1);
            Access.EnqueueTranscodeSeries(h, true, "S", "/o", Access.MakeTcFileList(f1));
            Assert.True(Access.TcQueueSnapshot()[0].finalize);       // оверлей повышен до финализации
        }
        finally { Cleanup(); }
    }

    [Fact]
    public void Different_hashes_queue_separately_with_positions()
    {
        Reset();
        string h = UniqueHash(), h2 = UniqueHash();
        try
        {
            Access.EnqueueTranscodeSeries(h, false, "A", "/a",
                Access.MakeTcFileList(Access.MakeTcFile(0, "/x/a.mkv", "/a/a.part", "/a/a.mp4", 1)));
            Access.EnqueueTranscodeSeries(h2, false, "B", "/b",
                Access.MakeTcFileList(Access.MakeTcFile(0, "/x/b.mkv", "/b/b.part", "/b/b.mp4", 1)));

            Assert.Equal(2, Access.TcQueueSnapshot().Count);
            Assert.Equal(1, Access.QueuePosition(h));
            Assert.Equal(2, Access.QueuePosition(h2));
        }
        finally { Cleanup(); }
    }

    [Fact]
    public void Movie_overload_wraps_into_single_file_item()
    {
        Reset();
        string h = UniqueHash();
        try
        {
            Access.EnqueueTranscode(h, "/downloads/movie.mkv", "/out/movie.mp4.part", "/out/movie.mp4", 5400);
            var snap = Access.TcQueueSnapshot();
            Assert.Single(snap);
            Assert.True(snap[0].finalize);                            // фильм всегда финализируется
            Assert.Equal(new List<int> { -1 }, snap[0].indexes);
            Assert.Equal(1, Access.TcJob(h).filesTotal);
        }
        finally { Cleanup(); }
    }
}
