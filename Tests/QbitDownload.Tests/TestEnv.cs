using System;
using System.IO;
using Shared;
using QbitDownload;
using Xunit;

// The suite mutates process-wide statics (ModInit.conf, CoreInit.conf) and writes a shared SQLite db,
// so run tests serially to keep them deterministic.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace QbitDownload.Tests;

/// <summary>Shared setup for module config + per-test SQLite isolation.</summary>
public static class TestEnv
{
    static readonly object _gate = new();

    /// <summary>Make sure ModInit.conf and CoreInit.conf are populated (defaults).</summary>
    public static void EnsureConf()
    {
        lock (_gate)
        {
            if (ModInit.conf == null) ModInit.conf = new ModuleConf();
            if (CoreInit.conf == null) CoreInit.conf = new CoreInit();
        }
    }

    /// <summary>Point the module cache (and therefore the SQLite db) at a fresh temp dir; returns it.</summary>
    public static string FreshCache()
    {
        lock (_gate)
        {
            EnsureConf();
            string dir = Path.Combine(Path.GetTempPath(), "qdl-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            ModInit.conf.cachePath = dir;
            // ⚠️ Горячий слой JSON и кеш ответа /qdl/list статические и переживают тест.
            // Без сброса следующий тест читал бы РАМ и готовый ответ предыдущего.
            // В проде это делает updateConf при смене cachePath.
            JsonStore.ResetForConfigReload();
            // ⚠️ Журнал намерений тоже статический. Сбрасываем БЕЗ флаша: запись уронила бы
            // состояние прошлого кейса в новый временный cachePath. Без этого ключи вида
            // «3-102:s1e3» повторяются от теста к тесту, и долг предыдущего кейса воскресал
            // в следующем — 20 красных на пустом месте.
            // Сейчас это ВТОРОЙ пояс: те же сбросы стоят в XsAccess/JutGrabAccess/JutWatchAccess,
            // и негативный прогон снятия только этой строки уже не краснеет. Оставлено намеренно —
            // сеть для будущих тестов, которые берут FreshCache, но не трогают харнессы очереди.
            DownloadWants.ResetForTests();
            QualityCaches.ResetForTests();   // кеш решений об апгрейде — тоже статика
            Perms.ResetForConfigReload();   // троттлинг «когда видели» относился к прежнему cachePath
            Groups.ResetForConfigReload();  // индекс uid → группа — тоже статика поверх cachePath
            QbitController.DropListCache();
            QbitController.SeriesIndexDrop();   // индекс групп сезонов — тоже статика поверх cachePath
            // Снимок «что уже скачано» держится 10 с, а тесты меняют каталог загрузок
            // быстрее — без сброса следующий тест читал бы снимок предыдущего.
            QbitController.JutDropAllDiskKeys();
            QbitController.XsmartDropAllDiskKeys();
            return dir;
        }
    }

    /// <summary>Set the loopback resolver identity used by SSRF checks (IsLoopbackSelf/IsSelfResolver).</summary>
    public static void SetListen(int port, string localhost)
    {
        EnsureConf();
        CoreInit.conf.listen.port = port;
        CoreInit.conf.listen.localhost = localhost;
    }
}
