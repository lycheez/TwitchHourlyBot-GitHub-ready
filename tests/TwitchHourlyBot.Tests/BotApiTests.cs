using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TwitchHourlyBot.Api;
using TwitchHourlyBot.Api.Bot;
using Xunit;

namespace TwitchHourlyBot.Tests;

public sealed class FakeTwitch : HttpMessageHandler
{
    public record ChatCall(string BroadcasterId, string SenderId, string Message);

    public List<ChatCall> Chats { get; } = new();
    public List<string> Requests { get; } = new();
    public Dictionary<string, (string StreamId, DateTimeOffset StartedAt)> Live { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> UnknownUsers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> InvalidTokens { get; } = new();
    public int Refreshes { get; private set; }
    public bool Revoked { get; private set; }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var path = uri.AbsolutePath;
        var query = HttpUtility.ParseQueryString(uri.Query);
        Requests.Add($"{request.Method} {uri.Host}{path}");

        var auth = request.Headers.Authorization;
        var bearer = auth?.Parameter ?? "";

        if (uri.Host == "id.twitch.tv")
        {
            if (path == "/oauth2/token")
            {
                var form = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellationToken));
                var scopes = "[\"user:read:chat\",\"user:write:chat\",\"moderator:manage:banned_users\"]";
                if (form["grant_type"] == "authorization_code")
                {
                    if (form["code"] != "good-code") return Json(HttpStatusCode.BadRequest, "{\"message\":\"bad code\"}");
                    return Json(HttpStatusCode.OK, $"{{\"access_token\":\"at1\",\"refresh_token\":\"rt1\",\"expires_in\":3600,\"scope\":{scopes},\"token_type\":\"bearer\"}}");
                }
                Refreshes++;
                return Json(HttpStatusCode.OK, $"{{\"access_token\":\"at{Refreshes + 1}\",\"refresh_token\":\"rt{Refreshes + 1}\",\"expires_in\":3600,\"scope\":{scopes},\"token_type\":\"bearer\"}}");
            }
            if (path == "/oauth2/validate")
                return InvalidTokens.Contains(bearer)
                    ? Json(HttpStatusCode.Unauthorized, "{}")
                    : Json(HttpStatusCode.OK, "{\"client_id\":\"cid\",\"login\":\"botuser\",\"user_id\":\"99\",\"expires_in\":3600}");
            if (path == "/oauth2/revoke") { Revoked = true; return Json(HttpStatusCode.OK, "{}"); }
        }

        if (uri.Host == "api.twitch.tv")
        {
            if (InvalidTokens.Contains(bearer)) return Json(HttpStatusCode.Unauthorized, "{}");

            if (path == "/helix/users")
            {
                var login = query["login"]!;
                if (UnknownUsers.Contains(login)) return Json(HttpStatusCode.OK, "{\"data\":[]}");
                return Json(HttpStatusCode.OK, $"{{\"data\":[{{\"id\":\"u_{login}\",\"login\":\"{login}\",\"display_name\":\"{login}\"}}]}}");
            }
            if (path == "/helix/streams")
            {
                var login = query["user_id"]!.Substring(2);
                if (!Live.TryGetValue(login, out var live)) return Json(HttpStatusCode.OK, "{\"data\":[]}");
                return Json(HttpStatusCode.OK,
                    $"{{\"data\":[{{\"id\":\"{live.StreamId}\",\"user_id\":\"u_{login}\",\"type\":\"live\",\"title\":\"t\",\"game_name\":\"g\",\"started_at\":\"{live.StartedAt:O}\"}}]}}");
            }
            if (path == "/helix/chat/messages")
            {
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var root = doc.RootElement;
                Chats.Add(new ChatCall(root.GetProperty("broadcaster_id").GetString()!, root.GetProperty("sender_id").GetString()!, root.GetProperty("message").GetString()!));
                return Json(HttpStatusCode.OK, "{\"data\":[{\"message_id\":\"m\",\"is_sent\":true}]}");
            }
        }

        return Json(HttpStatusCode.NotFound, "{}");
    }
}

public sealed class TestTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class FakeKeywordMonitorFactory : IKeywordMonitorFactory
{
    public sealed class Monitor : IKeywordMonitor
    {
        public IReadOnlyList<KeywordTimeoutChannel> Channels { get; init; } = Array.Empty<KeywordTimeoutChannel>();
        public string BotUserId { get; init; } = "";
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }
        public Task StartAsync() { Started = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    public List<Monitor> Created { get; } = new();

    public IKeywordMonitor Create(string clientId, TwitchClient twitch, string botUserId, Func<string> getAccessToken,
        IReadOnlyList<KeywordTimeoutChannel> channels, Action<string> log)
    {
        var monitor = new Monitor { Channels = channels, BotUserId = botUserId };
        Created.Add(monitor);
        return monitor;
    }
}

public sealed class BotFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    public FakeTwitch Twitch { get; } = new();
    public TestTime Time { get; } = new();
    public FakeKeywordMonitorFactory Keywords { get; } = new();

    private readonly bool _configured;

    public BotFactory(bool configured = true)
    {
        _configured = configured;
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (_configured)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Twitch:ClientId"] = "cid",
                ["Twitch:ClientSecret"] = "s3cr3t-value",
            }));
        }

        builder.ConfigureServices(services =>
        {
            services.Remove(services.Single(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>)));
            services.AddDbContext<AppDbContext>(o => o.UseSqlite(_connection));
            services.AddHttpClient("twitch").ConfigurePrimaryHttpMessageHandler(() => Twitch);
            services.Remove(services.Single(d => d.ServiceType == typeof(TimeProvider)));
            services.AddSingleton<TimeProvider>(Time);
            services.Remove(services.Single(d => d.ServiceType == typeof(IKeywordMonitorFactory)));
            services.AddSingleton<IKeywordMonitorFactory>(Keywords);
        });
    }

    public HttpClient CreateApiClient() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }
}

public class BotApiTests : IDisposable
{
    private readonly BotFactory _factory = new();
    private readonly HttpClient _client;

    public BotApiTests() => _client = _factory.CreateApiClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private BotRuntime Runtime => _factory.Services.GetRequiredService<BotRuntime>();

    private async Task Login()
    {
        var result = (await _client.GetFromJsonAsync<LoginUrlResult>("/api/auth/login-url"))!;
        var state = HttpUtility.ParseQueryString(new Uri(result.Url).Query)["state"]!;
        var response = await _client.GetAsync($"/api/auth/callback?code=good-code&state={state}");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/?login=ok", response.Headers.Location!.OriginalString);
    }

    private async Task DisableEvents() =>
        (await _client.PutAsJsonAsync("/api/settings", new UpdateSettingsRequest(null, null, false, null))).EnsureSuccessStatusCode();

    private async Task<ChannelDto> AddChannel(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/channels", new CreateChannelRequest(name));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ChannelDto>())!;
    }

    private static string Template(string channel, int hours) => $"⏰ {channel} Live มาแล้ว {hours} ชั่วโมง! 🎉";

    // ---- settings and login ----

    [Fact]
    public async Task Settings_expose_the_redirect_url_but_no_credentials()
    {
        var body = await (await _client.GetAsync("/api/settings")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("s3cr3t-value", body);
        Assert.DoesNotContain("clientSecret", body, StringComparison.OrdinalIgnoreCase);
        var settings = JsonSerializer.Deserialize<SettingsDto>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.EndsWith("/api/auth/callback", settings.RedirectUri);
    }

    [Fact]
    public async Task Status_reports_that_the_server_is_configured()
    {
        Assert.True((await _client.GetFromJsonAsync<AuthStatus>("/api/auth/status"))!.Configured);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(25, null)]
    [InlineData(null, 4)]
    public async Task Invalid_intervals_are_rejected(int? hours, int? minutes)
    {
        var response = await _client.PutAsJsonAsync("/api/settings", new UpdateSettingsRequest(hours, null, null, minutes));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_is_refused_when_the_server_has_no_twitch_application()
    {
        using var factory = new BotFactory(configured: false);
        using var client = factory.CreateApiClient();

        Assert.False((await client.GetFromJsonAsync<AuthStatus>("/api/auth/status"))!.Configured);
        var response = await client.GetAsync("/api/auth/login-url");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Twitch:ClientId", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_url_contains_client_scopes_state_and_redirect()
    {
        var url = new Uri((await _client.GetFromJsonAsync<LoginUrlResult>("/api/auth/login-url"))!.Url);
        var query = HttpUtility.ParseQueryString(url.Query);
        Assert.Equal("id.twitch.tv", url.Host);
        Assert.Equal("cid", query["client_id"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal(TwitchAuthService.Scopes, query["scope"]);
        Assert.False(string.IsNullOrEmpty(query["state"]));
        Assert.EndsWith("/api/auth/callback", query["redirect_uri"]);
    }

    [Fact]
    public async Task Callback_with_unknown_state_is_rejected()
    {
        var response = await _client.GetAsync("/api/auth/callback?code=good-code&state=forged");
        Assert.Contains("login=error", response.Headers.Location!.OriginalString);
        Assert.False((await _client.GetFromJsonAsync<AuthStatus>("/api/auth/status"))!.Connected);
    }

    [Fact]
    public async Task State_can_only_be_used_once()
    {
        var url = (await _client.GetFromJsonAsync<LoginUrlResult>("/api/auth/login-url"))!.Url;
        var state = HttpUtility.ParseQueryString(new Uri(url).Query)["state"];
        await _client.GetAsync($"/api/auth/callback?code=good-code&state={state}");
        var second = await _client.GetAsync($"/api/auth/callback?code=good-code&state={state}");
        Assert.Contains("login=error", second.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Twitch_error_on_callback_is_reported()
    {
        var url = (await _client.GetFromJsonAsync<LoginUrlResult>("/api/auth/login-url"))!.Url;
        var state = HttpUtility.ParseQueryString(new Uri(url).Query)["state"];
        var response = await _client.GetAsync($"/api/auth/callback?error=access_denied&state={state}");
        Assert.Contains("login=error", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Successful_login_reports_connected_account()
    {
        await Login();
        var status = (await _client.GetFromJsonAsync<AuthStatus>("/api/auth/status"))!;
        Assert.True(status.Connected);
        Assert.Equal("botuser", status.Login);
        Assert.Equal("99", status.UserId);
        Assert.True(status.ScopesOk);
    }

    [Fact]
    public async Task Tokens_are_stored_encrypted()
    {
        await Login();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = db.AuthSessions.Single().ProtectedJson;
        Assert.DoesNotContain("at1", stored);
        Assert.DoesNotContain("rt1", stored);
    }

    [Fact]
    public async Task Logout_revokes_and_disconnects()
    {
        await Login();
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.True(_factory.Twitch.Revoked);
        Assert.False((await _client.GetFromJsonAsync<AuthStatus>("/api/auth/status"))!.Connected);
    }

    // ---- bot ----

    [Fact]
    public async Task Bot_cannot_start_before_login()
    {
        var response = await _client.PostAsync("/api/bot/start", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False(Runtime.Running);
    }

    [Fact]
    public async Task Sends_hourly_message_once_when_the_hour_mark_passes()
    {
        await Login();
        await DisableEvents();
        await AddChannel("alpha");
        // The bot first sees the stream at 30 minutes, then it crosses the 1h mark.
        _factory.Twitch.Live["alpha"] = ("stream-1", _factory.Time.Now - TimeSpan.FromMinutes(30));
        await _client.PostAsync("/api/bot/start", null);
        Assert.Empty(_factory.Twitch.Chats);

        _factory.Time.Now += TimeSpan.FromMinutes(31);
        await Runtime.TickAsync();
        var chat = Assert.Single(_factory.Twitch.Chats);
        Assert.Equal("u_alpha", chat.BroadcasterId);
        Assert.Equal("99", chat.SenderId);
        Assert.Equal(Template("alpha", 1), chat.Message);

        await Runtime.TickAsync();
        Assert.Single(_factory.Twitch.Chats);
    }

    [Fact]
    public async Task Joining_a_stream_already_past_hour_marks_does_not_spam()
    {
        await Login();
        await AddChannel("alpha");
        _factory.Twitch.Live["alpha"] = ("stream-1", _factory.Time.Now - new TimeSpan(2, 10, 0));
        await _client.PostAsync("/api/bot/start", null);
        Assert.Empty(_factory.Twitch.Chats);

        var status = Assert.Single((await _client.GetFromJsonAsync<BotStatus>("/api/bot"))!.Channels);
        Assert.Equal("LIVE", status.Status);
        Assert.Equal("02:10:00", status.LiveTime);
        Assert.Equal("00:50:00", status.NextMessage);
    }

    [Fact]
    public async Task Uses_the_configured_message_template_and_interval()
    {
        await Login();
        await _client.PutAsJsonAsync("/api/settings", new UpdateSettingsRequest(2, "{channel} is live for {hours}h ({live_time})", false, null)); // events off
        await AddChannel("alpha");
        _factory.Twitch.Live["alpha"] = ("s", _factory.Time.Now - TimeSpan.FromMinutes(90));
        await _client.PostAsync("/api/bot/start", null);
        _factory.Time.Now += TimeSpan.FromMinutes(31);
        await Runtime.TickAsync();
        Assert.Equal("alpha is live for 2h (02:01:00)", Assert.Single(_factory.Twitch.Chats).Message);
    }

    [Fact]
    public async Task A_new_stream_resets_the_announcement_state()
    {
        await Login();
        await DisableEvents();
        await AddChannel("alpha");
        _factory.Twitch.Live["alpha"] = ("s1", _factory.Time.Now - TimeSpan.FromMinutes(59));
        await _client.PostAsync("/api/bot/start", null);
        _factory.Time.Now += TimeSpan.FromMinutes(2);
        await Runtime.TickAsync();
        Assert.Single(_factory.Twitch.Chats);

        // A new stream is first seen at 59 minutes, then crosses its own 1h mark.
        _factory.Twitch.Live["alpha"] = ("s2", _factory.Time.Now - TimeSpan.FromMinutes(59));
        await Runtime.TickAsync();
        Assert.Single(_factory.Twitch.Chats);
        _factory.Time.Now += TimeSpan.FromMinutes(2);
        await Runtime.TickAsync();
        Assert.Equal(2, _factory.Twitch.Chats.Count);
    }

    [Fact]
    public async Task Offline_and_unknown_channels_are_reported_without_messages()
    {
        await Login();
        await AddChannel("alpha");
        await AddChannel("ghost");
        _factory.Twitch.UnknownUsers.Add("ghost");
        await _client.PostAsync("/api/bot/start", null);

        var channels = (await _client.GetFromJsonAsync<BotStatus>("/api/bot"))!.Channels.ToDictionary(c => c.Channel);
        Assert.Equal("OFFLINE", channels["alpha"].Status);
        Assert.Equal("NOT FOUND", channels["ghost"].Status);
        Assert.Empty(_factory.Twitch.Chats);
    }

    [Fact]
    public async Task Disabled_channels_are_not_polled()
    {
        await Login();
        var channel = await AddChannel("alpha");
        await _client.PutAsJsonAsync($"/api/channels/{channel.Id}", new UpdateChannelRequest(false, true));
        await _client.PostAsync("/api/bot/start", null);
        Assert.DoesNotContain(_factory.Twitch.Requests, r => r.EndsWith("/helix/streams"));
        Assert.Equal("DISABLED", Assert.Single((await _client.GetFromJsonAsync<BotStatus>("/api/bot"))!.Channels).Status);
    }

    [Fact]
    public async Task Expired_token_is_refreshed_and_the_request_retried()
    {
        await Login();
        await AddChannel("alpha");
        _factory.Twitch.Live["alpha"] = ("s", _factory.Time.Now - TimeSpan.FromMinutes(59));
        _factory.Twitch.InvalidTokens.Add("at1");
        await _client.PostAsync("/api/bot/start", null);

        _factory.Time.Now += TimeSpan.FromMinutes(2);
        await Runtime.TickAsync();
        Assert.Equal(1, _factory.Twitch.Refreshes);
        Assert.Single(_factory.Twitch.Chats);
    }

    [Fact]
    public async Task Stopped_bot_does_nothing()
    {
        await Login();
        await AddChannel("alpha");
        _factory.Twitch.Live["alpha"] = ("s", _factory.Time.Now - TimeSpan.FromMinutes(59));
        await _client.PostAsync("/api/bot/start", null);
        await _client.PostAsync("/api/bot/stop", null);
        _factory.Time.Now += TimeSpan.FromMinutes(5);
        await Runtime.TickAsync();
        Assert.Empty(_factory.Twitch.Chats);
        Assert.False((await _client.GetFromJsonAsync<BotStatus>("/api/bot"))!.Running);
    }

    [Fact]
    public async Task Wellness_event_is_sent_when_the_event_interval_elapses()
    {
        await Login();
        await _client.PutAsJsonAsync("/api/settings", new UpdateSettingsRequest(null, null, true, 5));
        await AddChannel("alpha");
        _factory.Twitch.Live["alpha"] = ("s", _factory.Time.Now - new TimeSpan(0, 10, 0));
        await _client.PostAsync("/api/bot/start", null);
        Assert.Empty(_factory.Twitch.Chats);

        _factory.Time.Now += TimeSpan.FromMinutes(6);
        await Runtime.TickAsync();
        var chat = Assert.Single(_factory.Twitch.Chats);
        Assert.Contains(EventMessageDefaults.Create(), m => m.Text == chat.Message);
    }

    [Fact]
    public async Task Events_can_be_turned_off()
    {
        await Login();
        await _client.PutAsJsonAsync("/api/settings", new UpdateSettingsRequest(null, null, false, 5));
        await AddChannel("alpha");
        _factory.Twitch.Live["alpha"] = ("s", _factory.Time.Now - new TimeSpan(0, 10, 0));
        await _client.PostAsync("/api/bot/start", null);
        _factory.Time.Now += TimeSpan.FromMinutes(30);
        await Runtime.TickAsync();
        Assert.Empty(_factory.Twitch.Chats);
    }

    [Fact]
    public async Task Activity_log_records_what_happened()
    {
        await Login();
        await _client.PostAsync("/api/bot/start", null);
        var log = (await _client.GetFromJsonAsync<List<string>>("/api/log"))!;
        Assert.Contains(log, l => l.Contains("Connected to Twitch as botuser"));
        Assert.Contains(log, l => l.Contains("Bot started"));
    }

    // ---- keyword protection wiring ----

    [Fact]
    public async Task Keyword_monitor_gets_enabled_channels_with_enabled_rules_and_restarts_on_changes()
    {
        await Login();
        var alpha = await AddChannel("alpha");
        await AddChannel("beta");
        await _client.PostAsJsonAsync($"/api/channels/{alpha.Id}/rules", new CreateRuleRequest("spoiler", 120, true));
        await _client.PostAsync("/api/bot/start", null);

        var first = Assert.Single(_factory.Keywords.Created);
        Assert.True(first.Started);
        Assert.Equal("99", first.BotUserId);
        var channel = Assert.Single(first.Channels);
        Assert.Equal("alpha", channel.Channel);
        Assert.Equal("u_alpha", channel.BroadcasterId);
        Assert.Equal("spoiler", Assert.Single(channel.Rules).Keyword);

        await _client.PostAsJsonAsync($"/api/channels/{alpha.Id}/rules", new CreateRuleRequest("other", 60, true));
        Assert.Equal(2, _factory.Keywords.Created.Count);
        Assert.True(first.Disposed);
        Assert.Equal(2, _factory.Keywords.Created[1].Channels.Single().Rules.Count);

        await _client.PostAsync("/api/bot/stop", null);
        Assert.True(_factory.Keywords.Created[1].Disposed);
    }

    [Fact]
    public async Task No_keyword_monitor_when_there_are_no_rules()
    {
        await Login();
        await AddChannel("alpha");
        await _client.PostAsync("/api/bot/start", null);
        Assert.Empty(_factory.Keywords.Created);
    }

    [Fact]
    public async Task Channels_with_protection_off_are_not_monitored()
    {
        await Login();
        var alpha = await AddChannel("alpha");
        await _client.PostAsJsonAsync($"/api/channels/{alpha.Id}/rules", new CreateRuleRequest("spoiler", 120, true));
        await _client.PutAsJsonAsync($"/api/channels/{alpha.Id}", new UpdateChannelRequest(true, false));
        await _client.PostAsync("/api/bot/start", null);
        Assert.Empty(_factory.Keywords.Created);
    }
}
