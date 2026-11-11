using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace TwitchHourlyBot.Api.Bot;

public sealed record AuthStatus(bool Connected, string? Login, string? UserId, bool ScopesOk);

/// <summary>
/// Holds the Twitch login for the whole server: redirect-based OAuth, token validation/refresh,
/// encrypted persistence, and a shared <see cref="TwitchClient"/>.
/// </summary>
public sealed class TwitchSession
{
    public static readonly string[] RequiredScopes = TwitchAuthService.Scopes.Split(' ');

    private readonly IServiceScopeFactory _scopes;
    private readonly IHttpClientFactory _httpFactory;
    private readonly SecretProtector _protector;
    private readonly ActivityLog _log;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (string RedirectUri, DateTimeOffset Created)> _pendingLogins = new();
    private TwitchToken? _token;
    private TwitchClient? _client;
    private bool _loaded;

    public TwitchSession(IServiceScopeFactory scopes, IHttpClientFactory httpFactory, SecretProtector protector, ActivityLog log, TimeProvider time)
    {
        _scopes = scopes;
        _httpFactory = httpFactory;
        _protector = protector;
        _log = log;
        _time = time;
    }

    public TwitchToken? Token => _token;
    public TwitchClient? Client => _client;
    public string ClientId { get; private set; } = "";

    public AuthStatus Status => _token is null
        ? new AuthStatus(false, null, null, false)
        : new AuthStatus(true, _token.Login, _token.UserId, RequiredScopes.All(_token.HasScope));

    private async Task<(string ClientId, string Secret)> LoadCredentialsAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct);
        return (settings?.ClientId ?? "", _protector.Unprotect(settings?.ProtectedClientSecret ?? ""));
    }

    private TwitchAuthService CreateAuth(string clientId, string secret) =>
        new(clientId, secret, _httpFactory.CreateClient("twitch"));

    /// <summary>Returns the Twitch URL to send the browser to. Throws if credentials are missing.</summary>
    public async Task<string> BeginLoginAsync(string redirectUri, CancellationToken ct = default)
    {
        var (clientId, secret) = await LoadCredentialsAsync(ct);
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("Save the Twitch Client ID and Client Secret first.");

        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await _gate.WaitAsync(ct);
        try
        {
            var cutoff = _time.GetUtcNow().AddMinutes(-10);
            foreach (var old in _pendingLogins.Where(p => p.Value.Created < cutoff).Select(p => p.Key).ToList())
                _pendingLogins.Remove(old);
            _pendingLogins[state] = (redirectUri, _time.GetUtcNow());
        }
        finally { _gate.Release(); }

        return CreateAuth(clientId, secret).BuildAuthorizeUrl(redirectUri, state);
    }

    public async Task CompleteLoginAsync(string? code, string? error, string? state, CancellationToken ct = default)
    {
        string redirectUri;
        await _gate.WaitAsync(ct);
        try
        {
            if (string.IsNullOrEmpty(state) || !_pendingLogins.Remove(state, out var pending))
                throw new InvalidOperationException("OAuth state validation failed. Please try Login again.");
            redirectUri = pending.RedirectUri;
        }
        finally { _gate.Release(); }

        if (!string.IsNullOrWhiteSpace(error)) throw new InvalidOperationException("Twitch OAuth error: " + error);
        if (string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("No OAuth authorization code received.");

        var (clientId, secret) = await LoadCredentialsAsync(ct);
        var token = await CreateAuth(clientId, secret).ExchangeCodeAsync(code, redirectUri, ct);
        if (!RequiredScopes.All(token.HasScope))
            throw new InvalidOperationException("Twitch did not grant all required permissions. Please log in again and accept them.");

        await _gate.WaitAsync(ct);
        try { await SetTokenAsync(clientId, token, ct); }
        finally { _gate.Release(); }
        _log.Add($"🔐 Connected to Twitch as {token.Login}");
    }

    /// <summary>Loads the saved session on first use and validates it (refreshing if needed).</summary>
    public async Task<bool> EnsureAuthenticatedAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var (clientId, secret) = await LoadCredentialsAsync(ct);
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(secret)) return false;

            if (!_loaded || _token is null)
            {
                _loaded = true;
                _token = await LoadSavedAsync(clientId, ct) ?? _token;
            }
            if (_token is null) return false;

            var auth = CreateAuth(clientId, secret);
            if (_time.GetUtcNow() - _token.LastValidatedUtc < TimeSpan.FromMinutes(50) && _client is not null) return true;

            if (await auth.ValidateAsync(_token, ct))
            {
                await SetTokenAsync(clientId, _token, ct);
                return true;
            }
            return await RefreshLockedAsync(clientId, auth, ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Used by <see cref="TwitchClient"/> after a 401: refresh the token and keep going.</summary>
    public async Task<bool> RecoverAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_token is null) return false;
            var (clientId, secret) = await LoadCredentialsAsync(ct);
            return await RefreshLockedAsync(clientId, CreateAuth(clientId, secret), ct);
        }
        finally { _gate.Release(); }
    }

    private async Task<bool> RefreshLockedAsync(string clientId, TwitchAuthService auth, CancellationToken ct)
    {
        var refreshed = await auth.RefreshAsync(_token!, ct);
        if (refreshed is null)
        {
            _log.Add("⚠ Twitch session expired. Please log in again.");
            return false;
        }
        await SetTokenAsync(clientId, refreshed, ct);
        return true;
    }

    private async Task SetTokenAsync(string clientId, TwitchToken token, CancellationToken ct)
    {
        _token = token;
        _loaded = true;
        ClientId = clientId;
        if (_client is null) _client = new TwitchClient(clientId, token.AccessToken, RecoverAsync, _httpFactory.CreateClient("twitch"));
        else _client.SetAccessToken(token.AccessToken);

        var json = JsonSerializer.Serialize(new SavedAuthSession
        {
            AccessToken = token.AccessToken,
            RefreshToken = token.RefreshToken,
            Login = token.Login,
            UserId = token.UserId,
            ClientId = clientId,
            Scope = token.Scope ?? []
        });
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.AuthSessions.FirstOrDefaultAsync(ct);
        if (row is null) db.AuthSessions.Add(new AuthSessionEntity { ProtectedJson = _protector.Protect(json) });
        else row.ProtectedJson = _protector.Protect(json);
        await db.SaveChangesAsync(ct);
    }

    private async Task<TwitchToken?> LoadSavedAsync(string clientId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.AuthSessions.AsNoTracking().FirstOrDefaultAsync(ct);
        var json = _protector.Unprotect(row?.ProtectedJson ?? "");
        if (json.Length == 0) return null;

        var saved = JsonSerializer.Deserialize<SavedAuthSession>(json);
        if (saved is null || saved.ClientId != clientId || saved.AccessToken.Length == 0 || saved.RefreshToken.Length == 0) return null;
        return new TwitchToken
        {
            AccessToken = saved.AccessToken,
            RefreshToken = saved.RefreshToken,
            Login = saved.Login,
            UserId = saved.UserId,
            Scope = saved.Scope
        };
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        TwitchToken? token;
        await _gate.WaitAsync(ct);
        try
        {
            token = _token;
            _token = null;
            _client = null;
            _loaded = true;
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AuthSessions.RemoveRange(db.AuthSessions);
            await db.SaveChangesAsync(ct);
        }
        finally { _gate.Release(); }

        if (token is not null)
        {
            var (clientId, secret) = await LoadCredentialsAsync(ct);
            try { await CreateAuth(clientId, secret).RevokeAsync(token.AccessToken, ct); }
            catch (Exception ex) { _log.Add("⚠ Twitch revoke failed: " + ex.Message); }
            _log.Add("🔌 Disconnected from Twitch");
        }
    }
}
