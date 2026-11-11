namespace TwitchHourlyBot.Api.Bot;

public interface IKeywordMonitor : IAsyncDisposable
{
    Task StartAsync();
}

public interface IKeywordMonitorFactory
{
    IKeywordMonitor Create(string clientId, TwitchClient twitch, string botUserId, Func<string> getAccessToken,
        IReadOnlyList<KeywordTimeoutChannel> channels, Action<string> log);
}

/// <summary>Production factory: the same EventSub-based service the desktop app uses.</summary>
public sealed class EventSubKeywordMonitorFactory : IKeywordMonitorFactory
{
    private sealed class Monitor : IKeywordMonitor
    {
        private readonly KeywordTimeoutService _service;
        public Monitor(KeywordTimeoutService service) => _service = service;
        public Task StartAsync() => _service.StartAsync();
        public ValueTask DisposeAsync() => _service.DisposeAsync();
    }

    public IKeywordMonitor Create(string clientId, TwitchClient twitch, string botUserId, Func<string> getAccessToken,
        IReadOnlyList<KeywordTimeoutChannel> channels, Action<string> log) =>
        new Monitor(new KeywordTimeoutService(clientId, twitch, botUserId, getAccessToken, channels, log));
}
