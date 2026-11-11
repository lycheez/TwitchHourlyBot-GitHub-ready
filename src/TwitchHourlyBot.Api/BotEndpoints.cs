using Microsoft.EntityFrameworkCore;
using TwitchHourlyBot.Api.Bot;

namespace TwitchHourlyBot.Api;

public static class BotEndpoints
{
    public static string RedirectUriFor(HttpRequest request) => $"{request.Scheme}://{request.Host}/api/auth/callback";

    public static void MapBotApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        // ---- settings ----
        api.MapGet("/settings", async (AppDbContext db, HttpRequest request) =>
            Results.Ok(ToDto(await db.Settings.AsNoTracking().FirstOrDefaultAsync() ?? new BotSettingsEntity(), request)));

        api.MapPut("/settings", async (UpdateSettingsRequest req, AppDbContext db, SecretProtector protector, HttpRequest request) =>
        {
            if (req.IntervalHours is < 1 or > 24) return Results.ValidationProblem(new Dictionary<string, string[]> { ["intervalHours"] = new[] { "Interval must be 1-24 hours." } });
            if (req.ActivityIntervalMinutes is < 5 or > 720) return Results.ValidationProblem(new Dictionary<string, string[]> { ["activityIntervalMinutes"] = new[] { "Event interval must be 5-720 minutes." } });
            if (req.MessageTemplate is not null && (req.MessageTemplate.Trim().Length == 0 || req.MessageTemplate.Length > 400))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["messageTemplate"] = new[] { "Message must be 1-400 characters." } });

            var settings = await db.Settings.FirstOrDefaultAsync();
            if (settings is null) db.Settings.Add(settings = new BotSettingsEntity());
            if (req.ClientId is not null) settings.ClientId = req.ClientId.Trim();
            // An empty secret keeps the stored one, so the UI never has to know it.
            if (!string.IsNullOrWhiteSpace(req.ClientSecret)) settings.ProtectedClientSecret = protector.Protect(req.ClientSecret.Trim());
            if (req.IntervalHours is not null) settings.IntervalHours = req.IntervalHours.Value;
            if (req.MessageTemplate is not null) settings.MessageTemplate = req.MessageTemplate;
            if (req.ActivityEnabled is not null) settings.ActivityEnabled = req.ActivityEnabled.Value;
            if (req.ActivityIntervalMinutes is not null) settings.ActivityIntervalMinutes = req.ActivityIntervalMinutes.Value;
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(settings, request));
        });

        // ---- Twitch login ----
        api.MapGet("/auth/status", async (TwitchSession session) =>
        {
            await session.EnsureAuthenticatedAsync();
            return Results.Ok(session.Status);
        });

        // Returns the Twitch URL as JSON so the dashboard can navigate to it.
        api.MapGet("/auth/login-url", async (TwitchSession session, HttpRequest request) =>
        {
            try { return Results.Ok(new LoginUrlResult(await session.BeginLoginAsync(RedirectUriFor(request)))); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Twitch redirects the browser here; we finish the login and send the user back to the dashboard.
        api.MapGet("/auth/callback", async (string? code, string? error, string? state, TwitchSession session) =>
        {
            try
            {
                await session.CompleteLoginAsync(code, error, state);
                return Results.Redirect("/?login=ok");
            }
            catch (InvalidOperationException ex)
            {
                return Results.Redirect("/?login=error&message=" + Uri.EscapeDataString(ex.Message));
            }
        });

        api.MapPost("/auth/logout", async (TwitchSession session, BotRuntime bot) =>
        {
            await bot.StopAsync();
            await session.DisconnectAsync();
            return Results.NoContent();
        });

        // ---- bot control ----
        api.MapGet("/bot", (BotRuntime bot) => Results.Ok(bot.GetStatus()));

        api.MapPost("/bot/start", async (BotRuntime bot) =>
            await bot.StartAsync()
                ? Results.Ok(bot.GetStatus())
                : Results.Conflict(new { error = "Not connected to Twitch. Save your credentials and log in first." }));

        api.MapPost("/bot/stop", async (BotRuntime bot) =>
        {
            await bot.StopAsync();
            return Results.Ok(bot.GetStatus());
        });

        api.MapGet("/log", (ActivityLog log) => Results.Ok(log.Snapshot()));
    }

    private static SettingsDto ToDto(BotSettingsEntity s, HttpRequest request) =>
        new(s.ClientId, !string.IsNullOrEmpty(s.ProtectedClientSecret), s.IntervalHours, s.MessageTemplate, s.ActivityEnabled, s.ActivityIntervalMinutes, RedirectUriFor(request));
}
