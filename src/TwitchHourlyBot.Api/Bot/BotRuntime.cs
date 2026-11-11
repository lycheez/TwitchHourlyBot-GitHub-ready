using Microsoft.EntityFrameworkCore;

namespace TwitchHourlyBot.Api.Bot;

public sealed class ChannelStatus
{
    public string Channel { get; init; } = "";
    public string Status { get; set; } = "READY";
    public bool IsLive { get; set; }
    public string LiveTime { get; set; } = "-";
    public string NextMessage { get; set; } = "-";
    public string LastMessage { get; set; } = "";
    public string LastActivity { get; set; } = "";
    public string LastChecked { get; set; } = "";
}

public sealed record BotStatus(bool Running, bool Connected, string? Login, DateTimeOffset? NextEventAt, int KeywordRuleCount, IReadOnlyList<ChannelStatus> Channels);

/// <summary>
/// The server-side equivalent of the desktop app's Start/Stop bot: hourly "live for N hours" messages,
/// the wellness event engine, and keyword timeouts. <see cref="TickAsync"/> is called every 30 seconds by <see cref="BotWorker"/>.
/// </summary>
public sealed class BotRuntime
{
    private readonly IServiceScopeFactory _scopes;
    private readonly TwitchSession _session;
    private readonly ActivityLog _log;
    private readonly TimeProvider _time;
    private readonly IKeywordMonitorFactory _keywordFactory;
    private readonly DbCooldownStore _cooldowns;
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private readonly Dictionary<string, ChannelStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random _random = new();
    private IKeywordMonitor? _keywordMonitor;
    private int _keywordRuleCount;
    private DateTimeOffset? _nextActivityAt;

    public BotRuntime(IServiceScopeFactory scopes, TwitchSession session, ActivityLog log, TimeProvider time,
        IKeywordMonitorFactory keywordFactory, DbCooldownStore cooldowns)
    {
        _scopes = scopes;
        _session = session;
        _log = log;
        _time = time;
        _keywordFactory = keywordFactory;
        _cooldowns = cooldowns;
    }

    public bool Running { get; private set; }

    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        if (!await _session.EnsureAuthenticatedAsync(ct)) return false;
        Running = true;
        _nextActivityAt = null;
        _log.Add("▶ Bot started");
        await RestartKeywordsAsync(ct);
        await TickAsync(ct);
        return true;
    }

    public async Task StopAsync()
    {
        Running = false;
        _nextActivityAt = null;
        await StopKeywordsAsync();
        _log.Add("⏹ Bot stopped");
    }

    public BotStatus GetStatus()
    {
        lock (_statuses)
            return new BotStatus(Running, _session.Status.Connected, _session.Status.Login, _nextActivityAt, _keywordRuleCount,
                _statuses.Values.OrderBy(s => s.Channel).ToList());
    }

    private ChannelStatus StatusFor(string channel)
    {
        lock (_statuses)
        {
            if (!_statuses.TryGetValue(channel, out var s)) _statuses[channel] = s = new ChannelStatus { Channel = channel };
            return s;
        }
    }

    // ---- keyword protection -------------------------------------------------------------------

    public async Task RestartKeywordsAsync(CancellationToken ct = default)
    {
        await StopKeywordsAsync();
        var twitch = _session.Client;
        var token = _session.Token;
        if (!Running || twitch is null || token is null || string.IsNullOrWhiteSpace(token.UserId)) return;

        List<ChannelEntity> channels;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            channels = await db.Channels.Include(c => c.Rules).AsNoTracking()
                .Where(c => c.IsEnabled && c.KeywordProtectionEnabled).ToListAsync(ct);
        }

        var monitored = new List<KeywordTimeoutChannel>();
        foreach (var channel in channels)
        {
            var rules = channel.Rules.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Keyword))
                .Select(r => new KeywordRule { Keyword = r.Keyword, Enabled = true, TimeoutSeconds = r.TimeoutSeconds }).ToList();
            if (rules.Count == 0) continue;
            try
            {
                var user = await twitch.GetUserAsync(channel.Name, ct);
                if (user is not null) monitored.Add(new KeywordTimeoutChannel(channel.Name, user.Id, rules));
            }
            catch (Exception ex)
            {
                _log.Add($"⚠ Keyword Timeout: could not read channel {channel.Name}: {ex.Message}");
            }
        }

        if (monitored.Count == 0) return;

        _keywordMonitor = _keywordFactory.Create(_session.ClientId, twitch, token.UserId, () => _session.Token?.AccessToken ?? "", monitored, _log.Add);
        await _keywordMonitor.StartAsync();
        _keywordRuleCount = monitored.Sum(c => c.Rules.Count);
        _log.Add($"🛡 Keyword Timeout enabled: {_keywordRuleCount} rule(s) across {monitored.Count} channel(s)");
    }

    private async Task StopKeywordsAsync()
    {
        var monitor = _keywordMonitor;
        _keywordMonitor = null;
        _keywordRuleCount = 0;
        if (monitor is not null) await monitor.DisposeAsync();
    }

    // ---- periodic work ------------------------------------------------------------------------

    public async Task TickAsync(CancellationToken ct = default)
    {
        if (!Running) return;
        await _tickGate.WaitAsync(ct);
        try
        {
            if (!Running) return;
            if (!await _session.EnsureAuthenticatedAsync(ct))
            {
                _log.Add("⚠ Not connected to Twitch; skipping this round.");
                return;
            }

            BotSettingsEntity settings;
            List<ChannelEntity> channels;
            using (var scope = _scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new BotSettingsEntity();
                channels = await db.Channels.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
            }

            // Channels removed from the list must not linger in the status table.
            lock (_statuses)
                foreach (var gone in _statuses.Keys.Where(k => channels.All(c => !string.Equals(c.Name, k, StringComparison.OrdinalIgnoreCase))).ToList())
                    _statuses.Remove(gone);

            var enabled = channels.Where(c => c.IsEnabled).ToList();
            foreach (var channel in channels.Where(c => !c.IsEnabled)) StatusFor(channel.Name).Status = "DISABLED";

            foreach (var channel in enabled) await CheckChannelAsync(channel, settings, ct);
            await HandleEventsAsync(enabled, settings, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Add("⚠ " + ex.Message);
        }
        finally { _tickGate.Release(); }
    }

    private async Task CheckChannelAsync(ChannelEntity channel, BotSettingsEntity settings, CancellationToken ct)
    {
        var status = StatusFor(channel.Name);
        var twitch = _session.Client!;
        try
        {
            var user = await twitch.GetUserAsync(channel.Name, ct);
            if (user is null)
            {
                status.IsLive = false; status.Status = "NOT FOUND"; status.LiveTime = "-"; status.NextMessage = "-";
                return;
            }

            var stream = await twitch.GetStreamAsync(user.Id, ct);
            if (stream is null)
            {
                status.IsLive = false; status.Status = "OFFLINE"; status.LiveTime = "-"; status.NextMessage = "waiting for live";
                return;
            }

            var now = _time.GetUtcNow();
            var elapsed = now - stream.StartedAt;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            status.IsLive = true;
            status.Status = "LIVE";
            status.LiveTime = AnnouncementPlanner.FormatDuration(elapsed);
            status.LastChecked = _time.GetLocalNow().ToString("HH:mm:ss");

            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var key = channel.Name.ToLowerInvariant();
            var state = await db.StreamStates.FirstOrDefaultAsync(s => s.Channel == key, ct);
            var newStream = state is null || !string.Equals(state.StreamId, stream.Id, StringComparison.Ordinal);

            var plan = AnnouncementPlanner.Plan(elapsed, state?.LastAnnouncedHour ?? 0, newStream, settings.IntervalHours);
            if (state is null) db.StreamStates.Add(state = new StreamStateEntity { Channel = key });
            state.StreamId = stream.Id;
            state.LastAnnouncedHour = plan.AnnouncedBefore;

            var senderId = _session.Token?.UserId?.Trim();
            var until = plan.UntilNext;
            if (plan.ShouldSend)
            {
                if (string.IsNullOrWhiteSpace(senderId))
                {
                    status.LastMessage = "Twitch user id of the logged-in account is missing";
                    _log.Add($"⚠ {channel.Name}: Twitch user id of the logged-in account is missing");
                }
                else
                {
                    var message = AnnouncementPlanner.Render(settings.MessageTemplate, channel.Name, plan.HoursToAnnounce, elapsed);
                    await twitch.SendChatMessageAsync(senderId, user.Id, message, ct);
                    state.LastAnnouncedHour = plan.HoursToAnnounce;
                    status.LastMessage = $"{_time.GetLocalNow():HH:mm:ss}  {message}";
                    _log.Add($"💬 {channel.Name}: automated message sent");
                    until = TimeSpan.FromHours(plan.HoursToAnnounce + Math.Max(1, settings.IntervalHours)) - elapsed;
                }
            }
            await db.SaveChangesAsync(ct);
            status.NextMessage = until > TimeSpan.Zero ? AnnouncementPlanner.FormatDuration(until) : "Now";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            status.IsLive = false;
            status.Status = "ERROR";
            status.NextMessage = ex.Message.Length > 45 ? ex.Message[..45] + "…" : ex.Message;
        }
    }

    private async Task HandleEventsAsync(List<ChannelEntity> enabled, BotSettingsEntity settings, CancellationToken ct)
    {
        if (!settings.ActivityEnabled || enabled.Count == 0)
        {
            _nextActivityAt = null;
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(5, settings.ActivityIntervalMinutes));
        _nextActivityAt ??= _time.GetUtcNow().Add(interval);
        if (_time.GetUtcNow() < _nextActivityAt.Value) return;

        var engineSettings = new EventEngineSettings { Enabled = true, IntervalMinutes = settings.ActivityIntervalMinutes };
        var engine = new EventEngine(_cooldowns, _random);
        var twitch = _session.Client!;
        var senderId = _session.Token?.UserId?.Trim();
        var now = _time.GetUtcNow();

        foreach (var entity in enabled)
        {
            try
            {
                var user = await twitch.GetUserAsync(entity.Name, ct);
                var stream = user is null ? null : await twitch.GetStreamAsync(user.Id, ct);
                if (user is null || stream is null) continue;

                var config = new ChannelConfig { Channel = entity.Name, IsEnabled = true };
                var selected = engine.PickEvent(config, engineSettings, now);
                if (selected is null) continue;
                if (string.IsNullOrWhiteSpace(senderId))
                {
                    _log.Add($"⚠ {entity.Name}: Twitch user id of the logged-in account is missing");
                    continue;
                }

                await twitch.SendChatMessageAsync(senderId, user.Id, selected.Text, ct);
                _cooldowns.MarkSent(entity.Name, selected.Category, selected.Id, now);
                StatusFor(entity.Name).LastActivity = $"{_time.GetLocalNow():HH:mm:ss}  {selected.Text}";
                _log.Add($"🧘 {entity.Name}: event sent ({selected.Category})");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Add($"⚠ {entity.Name}: event failed: {ex.Message}");
            }
        }

        _nextActivityAt = _time.GetUtcNow().Add(interval);
    }
}

public sealed class BotWorker : BackgroundService
{
    private readonly BotRuntime _runtime;
    public BotWorker(BotRuntime runtime) => _runtime = runtime;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await _runtime.TickAsync(stoppingToken);
        }
        catch (OperationCanceledException) { }
        await _runtime.StopAsync();
    }
}
