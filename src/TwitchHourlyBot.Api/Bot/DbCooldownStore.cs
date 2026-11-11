using Microsoft.EntityFrameworkCore;

namespace TwitchHourlyBot.Api.Bot;

/// <summary>Event-engine cooldowns persisted in the database, so they survive restarts.</summary>
public sealed class DbCooldownStore : ICooldownStore
{
    private readonly IServiceScopeFactory _scopes;

    public DbCooldownStore(IServiceScopeFactory scopes) => _scopes = scopes;

    private static string Key(string channel) => channel.ToLowerInvariant();

    public bool IsReady(string channel, string category, int cooldownMinutes, DateTimeOffset now)
    {
        var last = GetLastSent(channel, category);
        return last == DateTimeOffset.MinValue || now - last >= TimeSpan.FromMinutes(Math.Max(0, cooldownMinutes));
    }

    public DateTimeOffset GetLastSent(string channel, string category)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var key = Key(channel);
        // SQLite cannot order/compare DateTimeOffset in SQL, so read the single row and compare in memory.
        var row = db.Cooldowns.AsNoTracking().FirstOrDefault(c => c.Channel == key && c.Category == category);
        return row?.LastSentUtc ?? DateTimeOffset.MinValue;
    }

    public string GetLastMessageId(string channel, string category)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var key = Key(channel);
        return db.Cooldowns.AsNoTracking().FirstOrDefault(c => c.Channel == key && c.Category == category)?.LastMessageId ?? "";
    }

    public void MarkSent(string channel, string category, string messageId, DateTimeOffset now)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var key = Key(channel);
        var row = db.Cooldowns.FirstOrDefault(c => c.Channel == key && c.Category == category);
        if (row is null)
            db.Cooldowns.Add(new CooldownEntity { Channel = key, Category = category, LastSentUtc = now, LastMessageId = messageId });
        else
        {
            row.LastSentUtc = now;
            row.LastMessageId = messageId;
        }
        db.SaveChanges();
    }
}
