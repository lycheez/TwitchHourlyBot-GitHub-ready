using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TwitchHourlyBot;

public sealed class TwitchAuthService
{
    public const string RedirectUri = "http://localhost:37821/callback/";
    private const string ValidateUrl = "https://id.twitch.tv/oauth2/validate";
    private const string TokenUrl = "https://id.twitch.tv/oauth2/token";
    private const string RevokeUrl = "https://id.twitch.tv/oauth2/revoke";
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly HttpClient _http;

    public TwitchAuthService(string clientId, string clientSecret)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
        _http = new HttpClient();
    }

    public async Task<TwitchToken> LoginAsync(CancellationToken cancellationToken = default)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add(RedirectUri);
        listener.Start();

        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var scopes = Uri.EscapeDataString("user:read:chat user:write:chat moderator:manage:banned_users");
        var url =
            $"https://id.twitch.tv/oauth2/authorize?client_id={Uri.EscapeDataString(_clientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&response_type=code&scope={scopes}" +
            $"&state={Uri.EscapeDataString(state)}";

        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });

        using var registration = cancellationToken.Register(() =>
        {
            try { listener.Stop(); } catch { }
        });

        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync();
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        var code = context.Request.QueryString["code"];
        var error = context.Request.QueryString["error"];
        var returnedState = context.Request.QueryString["state"];

        var html = !string.IsNullOrWhiteSpace(error)
            ? "<html><body><h2>Twitch login was cancelled or failed. You can close this tab.</h2></body></html>"
            : "<html><body><h2>Twitch login complete. You can close this tab.</h2></body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
        context.Response.Close();

        if (!string.IsNullOrWhiteSpace(error))
            throw new InvalidOperationException("Twitch OAuth error: " + error);
        if (!string.Equals(state, returnedState, StringComparison.Ordinal))
            throw new InvalidOperationException("OAuth state validation failed. Please try Login again.");
        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("No OAuth authorization code received.");

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["client_secret"] = _clientSecret,
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = RedirectUri
        });

        using var res = await _http.PostAsync(TokenUrl, content, cancellationToken);
        var body = await res.Content.ReadAsStringAsync(cancellationToken);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Twitch token API: {(int)res.StatusCode} {body}");

        var token = DeserializeToken(body);
        await ValidateAsync(token, cancellationToken);
        return token;
    }

    public async Task<bool> ValidateAsync(TwitchToken token, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ValidateUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", token.AccessToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) return false;

        var identity = JsonSerializer.Deserialize<TwitchValidateResponse>(body, JsonOptions);
        if (identity is null) return false;
        if (!string.IsNullOrWhiteSpace(identity.ClientId) && !string.Equals(identity.ClientId, _clientId, StringComparison.Ordinal))
            return false;

        token.Login = identity.Login ?? "";
        token.UserId = identity.UserId ?? "";
        token.ExpiresIn = identity.ExpiresIn;
        token.LastValidatedUtc = DateTimeOffset.UtcNow;
        return true;
    }

    public async Task<TwitchToken?> RefreshAsync(TwitchToken token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token.RefreshToken)) return null;

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["client_secret"] = _clientSecret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = token.RefreshToken
        });

        using var response = await _http.PostAsync(TokenUrl, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) return null;

        var refreshed = DeserializeToken(body);
        if (refreshed.Scope.Length == 0)
            refreshed.Scope = token.Scope;
        // Twitch may rotate the refresh token. Keep the old one only if no new one is returned.
        if (string.IsNullOrWhiteSpace(refreshed.RefreshToken))
            refreshed.RefreshToken = token.RefreshToken;
        refreshed.Login = token.Login;
        refreshed.UserId = token.UserId;
        refreshed.LastValidatedUtc = DateTimeOffset.UtcNow;

        if (!await ValidateAsync(refreshed, cancellationToken)) return null;
        return refreshed;
    }

    public async Task RevokeAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return;
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["token"] = accessToken
        });
        using var response = await _http.PostAsync(RevokeUrl, content, cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.BadRequest)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Twitch revoke API: {(int)response.StatusCode} {body}");
        }
    }

    private static TwitchToken DeserializeToken(string body) =>
        JsonSerializer.Deserialize<TwitchToken>(body, JsonOptions)
        ?? throw new InvalidOperationException("Invalid Twitch token response.");

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}

public sealed class TwitchToken
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = "";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("scope")]
    public string[] Scope { get; set; } = [];

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = "";

    public bool HasScope(string requiredScope) => Scope.Any(x => string.Equals(x, requiredScope, StringComparison.OrdinalIgnoreCase));

    [JsonIgnore]
    public string Login { get; set; } = "";

    [JsonIgnore]
    public string UserId { get; set; } = "";

    [JsonIgnore]
    public DateTimeOffset LastValidatedUtc { get; set; }
}

public sealed class TwitchValidateResponse
{
    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = "";

    [JsonPropertyName("login")]
    public string Login { get; set; } = "";

    [JsonPropertyName("user_id")]
    public string UserId { get; set; } = "";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }
}

public sealed class SavedAuthSession
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string Login { get; set; } = "";
    public string UserId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string[] Scope { get; set; } = [];
}

public static class AuthSessionStore
{
    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TwitchHourlyBot");
    private static string FilePath => Path.Combine(Folder, "auth-session.dat");

    public static void Save(TwitchToken token, string clientId)
    {
        SecureStorage.SaveProtectedJson(FilePath, new SavedAuthSession
        {
            AccessToken = token.AccessToken,
            RefreshToken = token.RefreshToken,
            Login = token.Login,
            UserId = token.UserId,
            ClientId = clientId,
            Scope = token.Scope ?? []
        });
    }

    public static TwitchToken? Load(string clientId)
    {
        var saved = SecureStorage.LoadProtectedJson<SavedAuthSession>(FilePath);
        if (saved is null || !string.Equals(saved.ClientId, clientId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(saved.AccessToken) || string.IsNullOrWhiteSpace(saved.RefreshToken))
            return null;

        return new TwitchToken
        {
            AccessToken = saved.AccessToken,
            RefreshToken = saved.RefreshToken,
            Login = saved.Login,
            UserId = saved.UserId,
            Scope = saved.Scope ?? []
        };
    }

    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }
}
