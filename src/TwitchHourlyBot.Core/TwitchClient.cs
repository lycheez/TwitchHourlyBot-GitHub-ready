using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TwitchHourlyBot;

public sealed class TwitchClient
{
    private readonly string _clientId;
    private readonly Func<CancellationToken, Task<bool>>? _recoverAuthentication;
    private readonly HttpClient _http;
    private string _token;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public TwitchClient(string clientId, string token, Func<CancellationToken, Task<bool>>? recoverAuthentication = null, HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        _clientId = clientId;
        _token = token;
        _recoverAuthentication = recoverAuthentication;
    }

    public void SetAccessToken(string token) => _token = token;

    private HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("Client-ID", _clientId);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return req;
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> requestFactory, CancellationToken cancellationToken = default)
    {
        using var firstRequest = requestFactory();
        var response = await _http.SendAsync(firstRequest, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized || _recoverAuthentication is null)
            return response;

        response.Dispose();
        var recovered = await _recoverAuthentication(cancellationToken);
        if (!recovered) throw new UnauthorizedAccessException("Twitch authorization expired. Please reconnect Twitch.");

        using var retryRequest = requestFactory();
        return await _http.SendAsync(retryRequest, cancellationToken);
    }

    public async Task<TwitchUser?> GetUserAsync(string login, CancellationToken cancellationToken = default)
    {
        using var res = await SendAsync(() => CreateRequest(HttpMethod.Get,
            $"https://api.twitch.tv/helix/users?login={Uri.EscapeDataString(login)}"), cancellationToken);
        var body = await res.Content.ReadAsStringAsync(cancellationToken);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Twitch users API: {(int)res.StatusCode} {body}");
        var data = JsonSerializer.Deserialize<TwitchResponse<TwitchUser>>(body, JsonOptions);
        return data?.Data?.FirstOrDefault();
    }

    public async Task<TwitchStream?> GetStreamAsync(string userId, CancellationToken cancellationToken = default)
    {
        using var res = await SendAsync(() => CreateRequest(HttpMethod.Get,
            $"https://api.twitch.tv/helix/streams?user_id={Uri.EscapeDataString(userId)}"), cancellationToken);
        var body = await res.Content.ReadAsStringAsync(cancellationToken);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Twitch streams API: {(int)res.StatusCode} {body}");
        var data = JsonSerializer.Deserialize<TwitchResponse<TwitchStream>>(body, JsonOptions);
        return data?.Data?.FirstOrDefault();
    }

    public async Task TimeoutUserAsync(string broadcasterId, string moderatorId, string userId, int durationSeconds, string reason, CancellationToken cancellationToken = default)
    {
        if (durationSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(durationSeconds));

        var payload = JsonSerializer.Serialize(new
        {
            broadcaster_id = broadcasterId,
            moderator_id = moderatorId,
            data = new
            {
                user_id = userId,
                duration = durationSeconds,
                reason
            }
        });

        using var res = await SendAsync(() =>
        {
            var req = CreateRequest(HttpMethod.Post, "https://api.twitch.tv/helix/moderation/bans");
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            return req;
        }, cancellationToken);

        var body = await res.Content.ReadAsStringAsync(cancellationToken);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Twitch moderation API: {(int)res.StatusCode} {body}");
    }

    public async Task SendChatMessageAsync(string senderId, string broadcasterId, string message, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            broadcaster_id = broadcasterId,
            sender_id = senderId,
            message
        });

        using var res = await SendAsync(() =>
        {
            var req = CreateRequest(HttpMethod.Post, "https://api.twitch.tv/helix/chat/messages");
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            return req;
        }, cancellationToken);

        var body = await res.Content.ReadAsStringAsync(cancellationToken);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Twitch chat API: {(int)res.StatusCode} {body}");
    }
}

public sealed class TwitchResponse<T>
{
    public List<T>? Data { get; set; }
}

public sealed class TwitchUser
{
    public string Id { get; set; } = "";
    public string Login { get; set; } = "";
    public string Display_Name { get; set; } = "";
}

public sealed class TwitchStream
{
    public string Id { get; set; } = "";
    public string User_Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Title { get; set; } = "";
    public string Game_Name { get; set; } = "";
    public DateTimeOffset Started_At { get; set; }
    public DateTimeOffset StartedAt => Started_At;
}
