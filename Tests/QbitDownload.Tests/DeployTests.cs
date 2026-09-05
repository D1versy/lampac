using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Машина ролей бесшовного редеплоя (Deploy.cs, qdl 2.110). Всё на временном cachePath:
/// файл active, lock-файл аренды и маркер чистой передачи лежат в {cachePath}/deploy/.
/// Прогрев дежурного выключен (ходит в сеть), тайминги ужаты.
/// </summary>
public class DeployTests
{
    static string Fresh()
    {
        string dir = TestEnv.FreshCache();
        Deploy.StandbyWarm = false;
        Deploy.PollPeriod = TimeSpan.FromMilliseconds(50);
        Deploy.LeaseRetry = TimeSpan.FromMilliseconds(20);
        Deploy.QuietFor = TimeSpan.FromMilliseconds(50);
        Deploy.QuietCap = TimeSpan.FromMilliseconds(300);
        Deploy.WorkersWait = TimeSpan.FromMilliseconds(200);
        return dir;
    }

    static void Name(string dir, string color)
    {
        Directory.CreateDirectory(Path.Combine(dir, "deploy"));
        File.WriteAllText(Path.Combine(dir, "deploy", "active"), color);
    }

    [Fact]
    public void Legacy_without_color_activates_at_once_and_is_ready()
    {
        Fresh();
        int act = 0;
        try
        {
            Deploy.Start(() => act++, () => { });
            Assert.Equal(Deploy.Mode.Legacy, Deploy.Current);
            Assert.Equal(1, act);
            Assert.True(Deploy.Ready);
            Assert.True(Deploy.WarmSavesAllowed);
            Assert.True(JsonStore.WritesEnabled);
            Assert.False(Deploy.Enabled);
            Assert.Equal("legacy", Deploy.Status().Value<string>("mode"));
        }
        finally { Deploy.ResetForTests(); }
    }

    [Fact]
    public void Standby_when_other_color_is_named_then_promote_on_rename()
    {
        string dir = Fresh();
        Name(dir, "blue");
        int act = 0, deact = 0;
        try
        {
            Deploy.Start(() => act++, () => deact++, "green");
            Assert.Equal(Deploy.Mode.Standby, Deploy.Current);
            Assert.Equal(0, act);
            Assert.False(Deploy.Ready);
            Assert.False(Deploy.HoldsLease);
            Assert.False(JsonStore.WritesEnabled);
            Assert.False(Deploy.WarmSavesAllowed);
            Assert.True(Deploy.WarmDone);   // прогрев выключен → сразу done

            Assert.True(Deploy.WriteNamed("green"));
            Assert.Equal("green", Deploy.ReadNamed());
            Assert.Equal(Deploy.Mode.Active, Deploy.Current);
            Assert.Equal(1, act);
            Assert.Equal(0, deact);
            Assert.True(Deploy.Ready);
            Assert.True(Deploy.HoldsLease);
            Assert.True(JsonStore.WritesEnabled);
            Assert.True(Deploy.ColdStart);   // маркера чистой передачи не было
        }
        finally { Deploy.ResetForTests(); }
    }

    [Fact]
    public void Promote_forgets_stale_ram_without_flushing_it_to_disk()
    {
        string dir = Fresh();
        Name(dir, "blue");
        string path = Path.Combine(dir, "access.json");
        try
        {
            Deploy.Start(() => { }, () => { }, "green");
            long dropped0 = JsonStore.Dropped;

            // дежурный что-то «записал»: РАМ меняется, диск — нет
            JsonStore.WriteNow(path, new JObject { ["v"] = "stale" });
            JsonStore.Flush();
            Assert.Equal("stale", JsonStore.ReadObject(path).Value<string>("v"));
            Assert.False(File.Exists(path));
            Assert.True(JsonStore.Dropped > dropped0);

            // «другой процесс» дописал файл на диске
            File.WriteAllText(path, new JObject { ["v"] = "disk" }.ToString());

            Assert.True(Deploy.WriteNamed("green"));
            Assert.Equal(Deploy.Mode.Active, Deploy.Current);
            Assert.Equal("disk", JsonStore.ReadObject(path).Value<string>("v"));
            Assert.Equal("disk", JObject.Parse(File.ReadAllText(path)).Value<string>("v"));
        }
        finally { Deploy.ResetForTests(); }
    }

    [Fact]
    public async Task Freeze_is_two_phase_releases_lease_writes_marker_and_can_come_back()
    {
        string dir = Fresh();
        Name(dir, "green");
        int act = 0, deact = 0;
        try
        {
            Deploy.Start(() => act++, () => deact++, "green");
            Assert.Equal(Deploy.Mode.Active, Deploy.Current);
            Assert.True(Deploy.HoldsLease);
            Assert.Equal(1, act);

            Name(dir, "blue");
            Deploy.Tick();
            await Deploy.WaitFreezeForTests();

            Assert.Equal(Deploy.Mode.Frozen, Deploy.Current);
            Assert.False(Deploy.Ready);
            Assert.False(Deploy.HoldsLease);
            Assert.False(JsonStore.WritesEnabled);
            Assert.True(Deploy.Draining);
            Assert.Equal(1, deact);
            Assert.True(File.Exists(Deploy.HandoffPath));

            // откат: назвали снова нас → аренда свободна → promote, маркер чистой передачи съеден
            Name(dir, "green");
            Deploy.Tick();
            Assert.Equal(Deploy.Mode.Active, Deploy.Current);
            Assert.True(Deploy.HoldsLease);
            Assert.True(JsonStore.WritesEnabled);
            Assert.False(Deploy.Draining);
            Assert.False(Deploy.ColdStart);
            Assert.False(File.Exists(Deploy.HandoffPath));
            Assert.Equal(2, act);
        }
        finally { Deploy.ResetForTests(); }
    }

    [Fact]
    public void Lease_is_exclusive_and_named_instance_promotes_once_it_is_free()
    {
        string dir = Fresh();
        Name(dir, "green");
        Directory.CreateDirectory(Path.Combine(dir, "deploy"));
        int act = 0;
        try
        {
            using (var other = new FileStream(Path.Combine(dir, "deploy", "lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                Deploy.Start(() => act++, () => { }, "green");
                Assert.Equal(Deploy.Mode.Standby, Deploy.Current);   // назван, но аренда у «предыдущего»
                Assert.False(Deploy.HoldsLease);
                Assert.Equal(0, act);
                Deploy.Tick();
                Assert.Equal(Deploy.Mode.Standby, Deploy.Current);
            }

            Deploy.Tick();
            Assert.Equal(Deploy.Mode.Active, Deploy.Current);
            Assert.True(Deploy.HoldsLease);
            Assert.Equal(1, act);
        }
        finally { Deploy.ResetForTests(); }
    }

    [Fact]
    public async Task Garbage_or_empty_active_file_changes_nothing_but_none_freezes()
    {
        string dir = Fresh();
        Name(dir, "green");
        try
        {
            Deploy.Start(() => { }, () => { }, "green");
            Assert.Equal(Deploy.Mode.Active, Deploy.Current);

            Name(dir, "purple");
            Deploy.Tick();
            Assert.Equal(Deploy.Mode.Active, Deploy.Current);
            Assert.True(Deploy.HoldsLease);

            Name(dir, "");
            Deploy.Tick();
            Assert.Equal(Deploy.Mode.Active, Deploy.Current);

            Name(dir, "none");
            Deploy.Tick();
            await Deploy.WaitFreezeForTests();
            Assert.Equal(Deploy.Mode.Frozen, Deploy.Current);
            Assert.False(Deploy.HoldsLease);
        }
        finally { Deploy.ResetForTests(); }
    }

    [Fact]
    public void Missing_active_file_means_blue()
    {
        string dir = Fresh();
        try
        {
            Assert.False(File.Exists(Path.Combine(dir, "deploy", "active")));
            Deploy.Start(() => { }, () => { }, "green");
            Assert.Equal(Deploy.Mode.Standby, Deploy.Current);
            Assert.Equal("blue", Deploy.Status().Value<string>("named"));
        }
        finally { Deploy.ResetForTests(); }
    }

    [Fact]
    public void WriteNamed_rejects_unknown_colors()
    {
        string dir = Fresh();
        Name(dir, "green");
        try
        {
            Deploy.Start(() => { }, () => { }, "green");
            Assert.False(Deploy.WriteNamed("red"));
            Assert.False(Deploy.WriteNamed(null));
            Assert.Equal("green", Deploy.ReadNamed());
        }
        finally { Deploy.ResetForTests(); }
    }

    [Fact]
    public void JsonStore_gate_keeps_ram_and_drops_disk_until_forget()
    {
        string dir = Fresh();
        string path = Path.Combine(dir, "groups.json");
        try
        {
            JsonStore.WritesEnabled = false;
            long d0 = JsonStore.Dropped;
            JsonStore.Write(path, new JObject { ["a"] = 1 });
            JsonStore.Flush();
            Assert.Equal(1, JsonStore.ReadObject(path).Value<int>("a"));
            Assert.False(File.Exists(path));
            Assert.Equal(d0 + 1, JsonStore.Dropped);

            JsonStore.ForgetAllNoFlush();
            Assert.Null(JsonStore.ReadObject(path));
            Assert.False(File.Exists(path));

            JsonStore.WritesEnabled = true;
            JsonStore.WriteNow(path, new JObject { ["a"] = 2 });
            Assert.True(File.Exists(path));
        }
        finally { JsonStore.WritesEnabled = true; }
    }

    [Fact]
    public void Hls_foreign_writer_is_detected_only_by_fresh_segments()
    {
        string dir = Path.Combine(Fresh(), "hls", "k");
        Directory.CreateDirectory(dir);
        Assert.False((bool)Access.Call("HlsForeignWriter", Path.Combine(dir, "nope")));
        Assert.False((bool)Access.Call("HlsForeignWriter", dir));

        string seg = Path.Combine(dir, "seg00007.ts");
        File.WriteAllBytes(seg, new byte[] { 1, 2, 3 });
        Assert.True((bool)Access.Call("HlsForeignWriter", dir));

        File.SetLastWriteTimeUtc(seg, DateTime.UtcNow.AddSeconds(-60));
        Assert.False((bool)Access.Call("HlsForeignWriter", dir));
    }
}
