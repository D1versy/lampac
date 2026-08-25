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
            Perms.ResetForConfigReload();   // троттлинг «когда видели» относился к прежнему cachePath
            QbitController.DropListCache();
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
