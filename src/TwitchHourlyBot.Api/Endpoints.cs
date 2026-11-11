using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TwitchHourlyBot.Api.Bot;

namespace TwitchHourlyBot.Api;

public static partial class Endpoints
{
    [GeneratedRegex("^[a-z0-9_]{1,25}$")]
    private static partial Regex ChannelNameRegex();

    public static string? NormalizeChannelName(string? raw)
    {
        var name = (raw ?? "").Trim().TrimStart('@').ToLowerInvariant();
        return ChannelNameRegex().IsMatch(name) ? name : null;
    }

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = new[] { message } });

    private static ChannelDto ToDto(ChannelEntity c) =>
        new(c.Id, c.Name, c.IsEnabled, c.KeywordProtectionEnabled, c.Rules.Count);

    private static RuleDto ToDto(KeywordRuleEntity r) => new(r.Id, r.Keyword, r.Enabled, r.TimeoutSeconds);

    public static void MapApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        api.MapGet("/channels", async (AppDbContext db) =>
            (await db.Channels.Include(c => c.Rules).OrderBy(c => c.Name).ToListAsync()).Select(ToDto));

        api.MapPost("/channels", async (CreateChannelRequest req, AppDbContext db) =>
        {
            var name = NormalizeChannelName(req.Name);
            if (name is null) return Invalid("name", "Channel name must be 1-25 characters: letters, digits or underscore.");
            if (await db.Channels.AnyAsync(c => c.Name == name)) return Results.Conflict(new { error = "Channel already exists." });

            var channel = new ChannelEntity { Name = name };
            db.Channels.Add(channel);
            await db.SaveChangesAsync();
            return Results.Created($"/api/channels/{channel.Id}", ToDto(channel));
        });

        api.MapPut("/channels/{id:int}", async (int id, UpdateChannelRequest req, AppDbContext db, BotRuntime bot) =>
        {
            var channel = await db.Channels.Include(c => c.Rules).FirstOrDefaultAsync(c => c.Id == id);
            if (channel is null) return Results.NotFound();
            channel.IsEnabled = req.IsEnabled;
            channel.KeywordProtectionEnabled = req.KeywordProtectionEnabled;
            await db.SaveChangesAsync();
            if (bot.Running) await bot.RestartKeywordsAsync();
            return Results.Ok(ToDto(channel));
        });

        api.MapDelete("/channels/{id:int}", async (int id, AppDbContext db, BotRuntime bot) =>
        {
            var channel = await db.Channels.FindAsync(id);
            if (channel is null) return Results.NotFound();
            db.Channels.Remove(channel);
            await db.SaveChangesAsync();
            if (bot.Running) await bot.RestartKeywordsAsync();
            return Results.NoContent();
        });

        api.MapGet("/channels/{id:int}/rules", async (int id, AppDbContext db) =>
        {
            if (!await db.Channels.AnyAsync(c => c.Id == id)) return Results.NotFound();
            var rules = await db.Rules.Where(r => r.ChannelId == id).OrderBy(r => r.Id).ToListAsync();
            return Results.Ok(rules.Select(ToDto));
        });

        api.MapPost("/channels/{id:int}/rules", async (int id, CreateRuleRequest req, AppDbContext db, BotRuntime bot) =>
        {
            if (!await db.Channels.AnyAsync(c => c.Id == id)) return Results.NotFound();
            var keyword = (req.Keyword ?? "").Trim();
            if (keyword.Length == 0 || keyword.Length > 100) return Invalid("keyword", "Keyword must be 1-100 characters.");

            var existing = await db.Rules.Where(r => r.ChannelId == id).ToListAsync();
            if (existing.Any(r => string.Equals(r.Keyword, keyword, StringComparison.OrdinalIgnoreCase)))
                return Results.Conflict(new { error = "Keyword already exists for this channel." });

            var rule = new KeywordRuleEntity
            {
                ChannelId = id,
                Keyword = keyword,
                Enabled = req.Enabled ?? true,
                TimeoutSeconds = KeywordMatcher.ClampTimeout(req.TimeoutSeconds ?? 60),
            };
            db.Rules.Add(rule);
            await db.SaveChangesAsync();
            if (bot.Running) await bot.RestartKeywordsAsync();
            return Results.Created($"/api/channels/{id}/rules/{rule.Id}", ToDto(rule));
        });

        api.MapPut("/channels/{id:int}/rules/{ruleId:int}", async (int id, int ruleId, UpdateRuleRequest req, AppDbContext db, BotRuntime bot) =>
        {
            var rule = await db.Rules.FirstOrDefaultAsync(r => r.Id == ruleId && r.ChannelId == id);
            if (rule is null) return Results.NotFound();
            rule.Enabled = req.Enabled;
            rule.TimeoutSeconds = KeywordMatcher.ClampTimeout(req.TimeoutSeconds);
            await db.SaveChangesAsync();
            if (bot.Running) await bot.RestartKeywordsAsync();
            return Results.Ok(ToDto(rule));
        });

        api.MapDelete("/channels/{id:int}/rules/{ruleId:int}", async (int id, int ruleId, AppDbContext db, BotRuntime bot) =>
        {
            var rule = await db.Rules.FirstOrDefaultAsync(r => r.Id == ruleId && r.ChannelId == id);
            if (rule is null) return Results.NotFound();
            db.Rules.Remove(rule);
            await db.SaveChangesAsync();
            if (bot.Running) await bot.RestartKeywordsAsync();
            return Results.NoContent();
        });

        // Dry run: which rule (if any) would this chat message trigger? Uses the same matcher as the desktop app.
        api.MapPost("/channels/{id:int}/check", async (int id, CheckMessageRequest req, AppDbContext db) =>
        {
            var channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == id);
            if (channel is null) return Results.NotFound();
            if (!channel.KeywordProtectionEnabled) return Results.Ok(new CheckMessageResult(false, null, null));

            var rules = await db.Rules.Where(r => r.ChannelId == id).OrderBy(r => r.Id).ToListAsync();
            var match = KeywordMatcher.FindMatch(req.Message, rules.Select(r =>
                new KeywordRule { Keyword = r.Keyword, Enabled = r.Enabled, TimeoutSeconds = r.TimeoutSeconds }));
            return Results.Ok(match is null
                ? new CheckMessageResult(false, null, null)
                : new CheckMessageResult(true, match.Keyword, KeywordMatcher.ClampTimeout(match.TimeoutSeconds)));
        });
    }
}
