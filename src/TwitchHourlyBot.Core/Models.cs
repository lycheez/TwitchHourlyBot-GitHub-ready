using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace TwitchHourlyBot;

public sealed class ChannelConfig : INotifyPropertyChanged
{
    private bool _isEnabled = true;
    private bool _isLive;
    private string _status = "⚪ READY";
    private string _liveTime = "-";
    private string _nextMessage = "รอ Live";
    private string _lastMessage = "ยังไม่มีข้อความ";
    private string _lastActivity = "";

    public string Channel { get; set; } = "";
    public ChannelEventSettings EventSettings { get; set; } = ChannelEventSettings.CreateDefault();
    public bool KeywordProtectionEnabled { get; set; } = true;
    public ObservableCollection<KeywordRule> KeywordRules { get; set; } = new();

    public string KeywordSummary => KeywordRules is null
        ? "0 keywords"
        : $"{KeywordRules.Count(r => r.Enabled)} active / {KeywordRules.Count} total";

    public bool IsEnabled
    {
        get => _isEnabled;
        set => Set(ref _isEnabled, value);
    }

    public bool IsLive
    {
        get => _isLive;
        set => Set(ref _isLive, value);
    }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public string LiveTime
    {
        get => _liveTime;
        set => Set(ref _liveTime, value);
    }

    public string NextMessage
    {
        get => _nextMessage;
        set => Set(ref _nextMessage, value);
    }

    public string LastMessage
    {
        get => _lastMessage;
        set => Set(ref _lastMessage, value);
    }

    public string LastActivity
    {
        get => _lastActivity;
        set => Set(ref _lastActivity, value);
    }

    public string LastChecked { get; set; } = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class KeywordRule : INotifyPropertyChanged
{
    private bool _enabled = true;
    private int _timeoutSeconds = 60;

    public string Keyword { get; set; } = "";

    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    public int TimeoutSeconds
    {
        get => _timeoutSeconds;
        set => Set(ref _timeoutSeconds, Math.Clamp(value, 1, 600));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
