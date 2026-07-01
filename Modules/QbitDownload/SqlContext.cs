using Microsoft.EntityFrameworkCore;
using System;
using System.ComponentModel.DataAnnotations;
using System.IO;

namespace QbitDownload;

// Хранилище уведомлений о скачанных сериях (SQLite, EF Core — родной для Lampac механизм,
// см. образец Modules/Sync/TimeCode/SqlContext.cs). Без DI/миграций: EnsureCreated в ModInit.
public class SqlContext : DbContext
{
    // БД рядом с watch.json/meta/img на SSD-томе (переживает пересоздание контейнера).
    static string DbPath => Path.Combine(ModInit.conf?.cachePath ?? "/qdl-data", "qdl.db");

    public DbSet<SeenModel> seen { get; set; }
    public DbSet<NotiModel> noti { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(DbPath)); } catch { }
            optionsBuilder.UseSqlite($"Data Source={DbPath};Cache=Shared");
            optionsBuilder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // дедуп: серия учитывается один раз на сериал
        modelBuilder.Entity<SeenModel>().HasIndex(x => new { x.seriesKey, x.epkey }).IsUnique();
        modelBuilder.Entity<NotiModel>().HasIndex(x => x.seriesKey);
        modelBuilder.Entity<NotiModel>().HasIndex(x => new { x.seriesKey, x.epkey }).IsUnique();
    }
}

// «Уже учтённые» серии (база отсечения): то, что присутствовало на момент включения слежения,
// плюс всё, про что уже уведомили — чтобы не слать повторов.
public class SeenModel
{
    [Key] public long Id { get; set; }
    public string seriesKey { get; set; }   // стабильный ключ сериала (t<tmdbId> | l<hash link>)
    public string epkey { get; set; }        // стабильный ключ серии (s1e7 | e7 | ova1 | r1-8)
}

// Лента уведомлений (история).
public class NotiModel
{
    [Key] public long Id { get; set; }
    public string seriesKey { get; set; }
    public int seriesId { get; set; }        // TMDB id (0 если неизвестен)
    public string hash { get; set; }         // текущий infohash раздачи (для постера/открытия)
    public string title { get; set; }        // название сериала
    public int season { get; set; }          // -1 если неизвестен
    public int episode { get; set; }         // -1 если неизвестен
    public string kind { get; set; }         // null | OVA/ONA/OAD/SP/RANGE
    public string epkey { get; set; }
    public string label { get; set; }        // готовая строка: «Сезон 1 · серия 7»
    public DateTime created { get; set; }     // UTC
    public bool read { get; set; }
}
