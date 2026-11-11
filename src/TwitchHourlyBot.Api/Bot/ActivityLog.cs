namespace TwitchHourlyBot.Api.Bot;

public sealed class ActivityLog
{
    private const int Capacity = 200;
    private readonly object _sync = new();
    private readonly LinkedList<string> _entries = new();
    private readonly TimeProvider _time;

    public ActivityLog(TimeProvider time) => _time = time;

    public void Add(string message)
    {
        var stamp = _time.GetLocalNow().ToString("HH:mm:ss");
        lock (_sync)
        {
            _entries.AddFirst($"{stamp}  {message}");
            while (_entries.Count > Capacity) _entries.RemoveLast();
        }
    }

    /// <summary>Newest entry first.</summary>
    public IReadOnlyList<string> Snapshot()
    {
        lock (_sync) return _entries.ToList();
    }
}
