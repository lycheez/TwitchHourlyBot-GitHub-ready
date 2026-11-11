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

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<ChannelEntity> Channels => Set<ChannelEntity>();
    public DbSet<KeywordRuleEntity> Rules => Set<KeywordRuleEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChannelEntity>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(25).IsRequired();
            e.HasMany(x => x.Rules).WithOne().HasForeignKey(r => r.ChannelId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<KeywordRuleEntity>(e => e.Property(x => x.Keyword).HasMaxLength(100).IsRequired());
    }
}
