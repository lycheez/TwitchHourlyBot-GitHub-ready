using Microsoft.EntityFrameworkCore;

namespace TwitchHourlyBot.Api;

public class ChannelEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public bool KeywordProtectionEnabled { get; set; } = true;
    public List<KeywordRuleEntity> Rules { get; set; } = new();
}

public class KeywordRuleEntity
{
    public int Id { get; set; }
    public int ChannelId { get; set; }
    public string Keyword { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 60;
}

public class BotSettingsEntity
{
    public int Id { get; set; } = 1;
    public string ClientId { get; set; } = "";
    /// <summary>Client secret protected with ASP.NET Core Data Protection. Never returned by the API.</summary>
    public string ProtectedClientSecret { get; set; } = "";
    public int IntervalHours { get; set; } = 1;
    public string MessageTemplate { get; set; } = "⏰ {channel} Live มาแล้ว {hours} ชั่วโมง! 🎉";
    public bool ActivityEnabled { get; set; } = true;
    public int ActivityIntervalMinutes { get; set; } = 30;
}

public class AuthSessionEntity
{
    public int Id { get; set; } = 1;
    /// <summary>JSON of the saved Twitch session, protected with Data Protection.</summary>
    public string ProtectedJson { get; set; } = "";
}

public class StreamStateEntity
{
    public string Channel { get; set; } = "";
    public string StreamId { get; set; } = "";
    public int LastAnnouncedHour { get; set; }
}

public class CooldownEntity
{
    public string Channel { get; set; } = "";
    public string Category { get; set; } = "";
    public DateTimeOffset LastSentUtc { get; set; }
    public string LastMessageId { get; set; } = "";
}

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<ChannelEntity> Channels => Set<ChannelEntity>();
    public DbSet<KeywordRuleEntity> Rules => Set<KeywordRuleEntity>();
    public DbSet<BotSettingsEntity> Settings => Set<BotSettingsEntity>();
    public DbSet<AuthSessionEntity> AuthSessions => Set<AuthSessionEntity>();
    public DbSet<StreamStateEntity> StreamStates => Set<StreamStateEntity>();
    public DbSet<CooldownEntity> Cooldowns => Set<CooldownEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChannelEntity>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(25).IsRequired();
            e.HasMany(x => x.Rules).WithOne().HasForeignKey(r => r.ChannelId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<KeywordRuleEntity>(e => e.Property(x => x.Keyword).HasMaxLength(100).IsRequired());
        modelBuilder.Entity<BotSettingsEntity>().HasKey(x => x.Id);
        modelBuilder.Entity<AuthSessionEntity>().HasKey(x => x.Id);
        modelBuilder.Entity<StreamStateEntity>().HasKey(x => x.Channel);
        modelBuilder.Entity<CooldownEntity>().HasKey(x => new { x.Channel, x.Category });
    }
}

public static class DbSetup
{
    /// <summary>
    /// Creates any missing tables. Unlike EnsureCreated, this also adds new tables to a database file
    /// created by an earlier version. Interim solution until EF Core migrations are introduced.
    /// </summary>
    public static void EnsureSchema(this AppDbContext db)
    {
        db.Database.OpenConnection();
        try
        {
            var script = db.Database.GenerateCreateScript();
            script = script
                .Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ")
                .Replace("CREATE UNIQUE INDEX ", "CREATE UNIQUE INDEX IF NOT EXISTS ")
                .Replace("CREATE INDEX ", "CREATE INDEX IF NOT EXISTS ");
            db.Database.ExecuteSqlRaw(script.Replace("{", "{{").Replace("}", "}}"));
        }
        finally
        {
            db.Database.CloseConnection();
        }
    }
}
