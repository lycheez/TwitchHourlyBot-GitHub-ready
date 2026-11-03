using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TwitchHourlyBot;

public sealed class KeywordTimeoutService : IAsyncDisposable
{
    public const string DefaultKeyword = "เนเน่";
    public const int DefaultTimeoutSeconds = 60;
    private const string EventSubUrl = "https://api.twitch.tv/helix/eventsub/subscriptions";
    private const string WebSocketUrl = "wss://eventsub.wss.twitch.tv/ws";

    private readonly string _clientId;
    private readonly TwitchClient _twitch;
    private readonly string _botUserId;
    private readonly Func<string> _getAccessToken;
    private readonly IReadOnlyList<KeywordTimeoutChannel> _channels;
    private readonly Action<string> _log;
    private readonly HttpClient _http = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _recentTimeouts = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private ClientWebSocket? _socket;
    private Task? _runTask;

    public KeywordTimeoutService(
        string clientId,
        TwitchClient twitch,
        string botUserId,
        Func<string> getAccessToken,
        IReadOnlyList<KeywordTimeoutChannel> channels,
        Action<string> log)
    {
        _clientId = clientId;
        _twitch = twitch;
        _botUserId = botUserId;
        _getAccessToken = getAccessToken;
        _channels = channels;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_runTask is not null) return;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _runTask = RunAsync(_cts.Token);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync()
    {
        Task? runTask;
        await _lifecycleGate.WaitAsync();
        try
        {
            _cts?.Cancel();
            try { _socket?.Abort(); } catch { }
            try { _socket?.Dispose(); } catch { }
            _socket = null;
            runTask = _runTask;
            _runTask = null;
            _cts?.Dispose();
            _cts = null;
        }
        finally
        {
            _lifecycleGate.Release();
        }

        if (runTask is not null)
        {
            try { await runTask; } catch (OperationCanceledException) { }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_channels.Count == 0)
        {
            _log("⚠ Keyword Timeout: ไม่มีช่องที่เปิดอยู่");
            return;
        }

        var retryDelay = TimeSpan.FromSeconds(3);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(cancellationToken);
                retryDelay = TimeSpan.FromSeconds(3);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log($"⚠ Keyword Timeout connection error: {ex.Message}");
                try { await Task.Delay(retryDelay, cancellationToken); } catch (OperationCanceledException) { break; }
                retryDelay = TimeSpan.FromSeconds(Math.Min(30, retryDelay.TotalSeconds * 2));
            }
        }
    }

    private async Task RunSessionAsync(CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.SetRequestHeader("Client-ID", _clientId);
        var accessToken = _getAccessToken();
        if (string.IsNullOrWhiteSpace(accessToken)) throw new InvalidOperationException("Twitch access token is unavailable.");
        socket.Options.SetRequestHeader("Authorization", "Bearer " + accessToken);
        _socket = socket;

        await socket.ConnectAsync(new Uri(WebSocketUrl), cancellationToken);
        _log("✓ Keyword Timeout chat monitor connected");

        var buffer = new byte[64 * 1024];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var json = await ReceiveTextAsync(socket, buffer, cancellationToken);
            if (json is null) break;
            await HandleEventSubMessageAsync(socket, json, cancellationToken);
        }

        _socket = null;
    }

    private async Task HandleEventSubMessageAsync(ClientWebSocket socket, string json, CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("metadata", out var metadata)) return;
        var messageType = metadata.TryGetProperty("message_type", out var mt) ? mt.GetString() : null;

        switch (messageType)
        {
            case "session_welcome":
                var sessionId = root.GetProperty("payload").GetProperty("session").GetProperty("id").GetString();
                if (string.IsNullOrWhiteSpace(sessionId)) throw new InvalidOperationException("Twitch EventSub did not return a session ID.");
                await SubscribeChannelsAsync(sessionId!, cancellationToken);
                break;

            case "session_keepalive":
                break;

            case "session_reconnect":
                // The current session will be closed by Twitch. Reconnecting to the normal
                // endpoint creates a fresh session and the subscriptions are registered again.
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "reconnect", cancellationToken); } catch { }
                break;

            case "notification":
                await HandleNotificationAsync(root, cancellationToken);
                break;
        }
    }

    private async Task SubscribeChannelsAsync(string sessionId, CancellationToken cancellationToken)
    {
        foreach (var channel in _channels)
        {
            var payload = JsonSerializer.Serialize(new
            {
                type = "channel.chat.message",
                version = "1",
                condition = new
                {
                    broadcaster_user_id = channel.BroadcasterId,
                    user_id = _botUserId
                },
                transport = new
                {
                    method = "websocket",
                    session_id = sessionId
                }
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, EventSubUrl);
            request.Headers.Add("Client-ID", _clientId);
            var accessToken = _getAccessToken();
            if (string.IsNullOrWhiteSpace(accessToken)) throw new InvalidOperationException("Twitch access token is unavailable.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"EventSub subscribe {channel.Channel}: {(int)response.StatusCode} {body}");

            _log($"✓ Keyword Timeout watching #{channel.Channel}");
        }
    }

    private async Task HandleNotificationAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var payload = root.GetProperty("payload");
        var subscription = payload.GetProperty("subscription");
        if (!string.Equals(subscription.GetProperty("type").GetString(), "channel.chat.message", StringComparison.Ordinal)) return;

        var ev = payload.GetProperty("event");
        var message = ev.TryGetProperty("message", out var messageElement)
            ? messageElement.GetProperty("text").GetString() ?? ""
            : "";
        var userId = ev.GetProperty("chatter_user_id").GetString() ?? "";
        var login = ev.TryGetProperty("chatter_user_login", out var loginElement) ? loginElement.GetString() ?? userId : userId;
        var broadcasterId = ev.GetProperty("broadcaster_user_id").GetString() ?? "";
        var channel = _channels.FirstOrDefault(x => string.Equals(x.BroadcasterId, broadcasterId, StringComparison.OrdinalIgnoreCase));
        if (channel is null || string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(broadcasterId)) return;
        if (string.Equals(userId, _botUserId, StringComparison.OrdinalIgnoreCase)) return;
        if (HasProtectedBadge(ev)) return;

        var matchedRule = channel.Rules
            .Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Keyword))
            .FirstOrDefault(r => message.IndexOf(r.Keyword.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
        if (matchedRule is null) return;

        var timeoutSeconds = Math.Clamp(matchedRule.TimeoutSeconds, 1, 600);
        var cooldownKey = broadcasterId + ":" + userId + ":" + matchedRule.Keyword;
        lock (_recentTimeouts)
        {
            if (_recentTimeouts.TryGetValue(cooldownKey, out var last) && DateTimeOffset.UtcNow - last < TimeSpan.FromSeconds(timeoutSeconds))
                return;
            _recentTimeouts[cooldownKey] = DateTimeOffset.UtcNow;
            if (_recentTimeouts.Count > 500)
            {
                foreach (var old in _recentTimeouts.Where(x => DateTimeOffset.UtcNow - x.Value > TimeSpan.FromMinutes(10)).Select(x => x.Key).ToList())
                    _recentTimeouts.Remove(old);
            }
        }

        try
        {
            await _twitch.TimeoutUserAsync(broadcasterId, _botUserId, userId, timeoutSeconds, $"Keyword: {matchedRule.Keyword}", cancellationToken);
            _log($"⏱ Keyword Timeout: @{login} in #{channel.Channel} • \"{matchedRule.Keyword}\" • {timeoutSeconds}s");
        }
        catch (Exception ex)
        {
            _log($"⚠ Keyword Timeout failed for @{login} in #{channel.Channel}: {ex.Message}");
        }
    }

    private static bool HasProtectedBadge(JsonElement ev)
    {
        if (!ev.TryGetProperty("badges", out var badges) || badges.ValueKind != JsonValueKind.Array) return false;
        foreach (var badge in badges.EnumerateArray())
        {
            var setId = badge.TryGetProperty("set_id", out var set) ? set.GetString() : null;
            if (string.Equals(setId, "broadcaster", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(setId, "moderator", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static async Task<string?> ReceiveTextAsync(ClientWebSocket socket, byte[] buffer, CancellationToken cancellationToken)
    {
        using var ms = new System.IO.MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) continue;
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _http.Dispose();
        _lifecycleGate.Dispose();
    }
}

public sealed class KeywordTimeoutChannel
{
    public string Channel { get; }
    public string BroadcasterId { get; }
    public IReadOnlyList<KeywordRule> Rules { get; }

    public KeywordTimeoutChannel(string channel, string broadcasterId, IReadOnlyList<KeywordRule> rules)
    {
        Channel = channel;
        BroadcasterId = broadcasterId;
        Rules = rules;
    }
}