using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace TwitchHourlyBot;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ChannelConfig> _channels = new();
    private readonly ObservableCollection<string> _activityLog = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _authTimer = new() { Interval = TimeSpan.FromMinutes(60) };
    private readonly SemaphoreSlim _authGate = new(1, 1);
    private TwitchAuthService? _authService;
    private DateTimeOffset _lastAuthValidationUtc = DateTimeOffset.MinValue;
    private TwitchClient? _twitch;
    private TwitchToken? _token;
    private bool _running;
    private int _intervalHours = 1;
    private DateTimeOffset? _nextActivityAt;
    private readonly EventEngine _eventEngine = new();
    private KeywordTimeoutService? _keywordTimeoutService;
    private EventEngineSettings _eventEngineSettings = new();

    public MainWindow()
    {
        InitializeComponent();
        ChannelList.ItemsSource = _channels;
        ActivityLogList.ItemsSource = _activityLog;
        KeywordChannelList.ItemsSource = _channels;
        LoadSettings();
        _timer.Tick += async (_, _) => await TickAsync();
        _authTimer.Tick += async (_, _) => await ValidateSessionAsync();
        Loaded += async (_, _) => await MainWindow_LoadedAsync();
        RefreshChannelCount();
    }

    private void LoadSettings()
    {
        var p = SettingsStore.Load();
        ClientIdBox.Text = p.ClientId;
        ClientSecretBox.Password = string.IsNullOrWhiteSpace(p.ClientSecret)
            ? SecureStorage.LoadSecret("client-secret.dat")
            : p.ClientSecret;
        BotUsernameBox.Text = p.BotUsername;
        BotUserIdBox.Text = p.BotUserId;
        IntervalBox.Text = p.IntervalHours.ToString();
        MessageTemplateBox.Text = p.MessageTemplate;
        _eventEngineSettings = p.EventEngine ?? new EventEngineSettings();
        ActivityEnabledBox.IsChecked = _eventEngineSettings.Enabled;
        if (_eventEngineSettings.Messages.Count == 0)
            _eventEngineSettings.Messages = EventMessageDefaults.Create();

        _channels.Clear();
        foreach (var channel in p.Channels)
        {
            channel.Channel = channel.Channel.Trim().TrimStart('@');
            if (!string.IsNullOrWhiteSpace(channel.Channel))
            {
                EnsureChannelEventSettings(channel);
                EnsureKeywordSettings(channel);
                _channels.Add(channel);
            }
        }

        // Backward compatibility with the previous single-channel version.
        if (_channels.Count == 0 && !string.IsNullOrWhiteSpace(p.Channel))
        {
            var legacyChannel = new ChannelConfig { Channel = p.Channel.Trim().TrimStart('@'), IsEnabled = true };
            EnsureChannelEventSettings(legacyChannel);
            EnsureKeywordSettings(legacyChannel);
            _channels.Add(legacyChannel);
        }

        RefreshChannelCount();
        KeywordChannelList.SelectedItem = _channels.FirstOrDefault();
        RefreshKeywordEditor();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        StatusText.Text = "บันทึกการตั้งค่าแล้ว";
    }

    private void SaveSettings()
    {
        _intervalHours = int.TryParse(IntervalBox.Text, out var n) && n > 0 ? n : 1;
        IntervalBox.Text = _intervalHours.ToString();
        _eventEngineSettings.Enabled = ActivityEnabledBox.IsChecked == true;
        _eventEngineSettings.IntervalMinutes = Math.Clamp(_eventEngineSettings.IntervalMinutes, 5, 1440);

        var clientSecret = ClientSecretBox.Password.Trim();
        if (!string.IsNullOrWhiteSpace(clientSecret))
            SecureStorage.SaveSecret("client-secret.dat", clientSecret);

        SettingsStore.Save(new AppSettings
        {
            ClientId = ClientIdBox.Text.Trim(),
            ClientSecret = "",
            BotUsername = BotUsernameBox.Text.Trim(),
            BotUserId = BotUserIdBox.Text.Trim(),
            IntervalHours = _intervalHours,
            MessageTemplate = string.IsNullOrWhiteSpace(MessageTemplateBox.Text)
                ? "⏰ {channel} Live มาแล้ว {hours} ชั่วโมง! 🎉"
                : MessageTemplateBox.Text,
            ActivityEnabled = ActivityEnabledBox.IsChecked == true,
            EventEngine = _eventEngineSettings,
            Channels = _channels.Select(c => new ChannelConfig
            {
                Channel = c.Channel,
                IsEnabled = c.IsEnabled,
                EventSettings = c.EventSettings,
                KeywordProtectionEnabled = c.KeywordProtectionEnabled,
                KeywordRules = new ObservableCollection<KeywordRule>(c.KeywordRules.Select(r => new KeywordRule
                {
                    Keyword = r.Keyword,
                    Enabled = r.Enabled,
                    TimeoutSeconds = r.TimeoutSeconds
                }))
            }).ToList()
        });
    }

    // Authentication is intentionally handled by LoginWindow in Phase 1.
    // This window only consumes an already authenticated session.
    private async Task MainWindow_LoadedAsync()
    {
        try
        {
            var clientId = ClientIdBox.Text.Trim();
            var clientSecret = ClientSecretBox.Password.Trim();
            var saved = string.IsNullOrWhiteSpace(clientId) ? null : AuthSessionStore.Load(clientId);

            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret) || saved is null)
            {
                OpenLoginWindow();
                return;
            }

            SetBusy(true, "กำลังตรวจสอบ Twitch session...");
            _authService = new TwitchAuthService(clientId, clientSecret);
            _token = saved;
            if (!_token.HasScope("moderator:manage:banned_users"))
            {
                AuthSessionStore.Clear();
                StatusText.Text = "ต้อง Login Twitch ใหม่เพื่ออนุญาต Keyword Timeout";
                OpenLoginWindow();
                return;
            }

            _twitch = new TwitchClient(clientId, saved.AccessToken, EnsureAuthenticationAsync);

            if (!await EnsureAuthenticationAsync(forceValidation: true))
            {
                OpenLoginWindow();
                return;
            }

            // The dashboard no longer exposes Bot Username / Bot User ID fields.
            // Always hydrate them from the authenticated Twitch identity after validation.
            BotUsernameBox.Text = _token!.Login;
            BotUserIdBox.Text = _token.UserId;
            AccountNameText.Text = "@" + _token.Login;
            SaveSettings();

            SetConnectedUi();
            StatusText.Text = $"พร้อมใช้งาน • {_token.Login}";
            LogActivity($"✓ Twitch session restored: {_token.Login}");
        }
        catch (Exception ex)
        {
            LogActivity("⚠ Session error: " + ex.Message);
            OpenLoginWindow();
        }
        finally
        {
            SetBusy(false, StatusText.Text);
            _authTimer.Start();
        }
    }

    private async Task ValidateSessionAsync()
    {
        if (_token is null || _authService is null) return;
        var ok = await EnsureAuthenticationAsync(forceValidation: true);
        if (!ok)
        {
            ConnectionText.Text = "Session expired";
            SessionText.Text = "Twitch session ใช้งานไม่ได้แล้ว • กำลังกลับหน้า Login";
            StatusText.Text = "Twitch authorization หมดอายุหรือถูกยกเลิก";
            await Task.Delay(250);
            OpenLoginWindow();
        }
        else
        {
            ConnectionText.Text = $"Connected: {_token.Login}";
            SessionText.Text = $"Session checked • {_lastAuthValidationUtc.ToLocalTime():HH:mm:ss}";
        }
    }

    private Task<bool> EnsureAuthenticationAsync(CancellationToken cancellationToken) =>
        EnsureAuthenticationAsync(forceValidation: true, cancellationToken);

    private async Task<bool> EnsureAuthenticationAsync(bool forceValidation, CancellationToken cancellationToken = default)
    {
        if (_token is null || _authService is null) return false;

        await _authGate.WaitAsync(cancellationToken);
        try
        {
            if (!forceValidation && DateTimeOffset.UtcNow - _lastAuthValidationUtc < TimeSpan.FromMinutes(50))
                return true;

            if (await _authService.ValidateAsync(_token, cancellationToken))
            {
                _lastAuthValidationUtc = DateTimeOffset.UtcNow;
                _twitch?.SetAccessToken(_token.AccessToken);
                AuthSessionStore.Save(_token, ClientIdBox.Text.Trim());
                return true;
            }

            StatusText.Text = "🔄 Twitch token ใช้ไม่ได้ กำลัง refresh session...";
            var refreshed = await _authService.RefreshAsync(_token, cancellationToken);
            if (refreshed is null)
            {
                ClearAuthenticatedState();
                return false;
            }

            ApplyAuthenticatedToken(refreshed);
            BotUsernameBox.Text = refreshed.Login;
            BotUserIdBox.Text = refreshed.UserId;
            AccountNameText.Text = "@" + refreshed.Login;
            AuthSessionStore.Save(refreshed, ClientIdBox.Text.Trim());
            _lastAuthValidationUtc = DateTimeOffset.UtcNow;
            StatusText.Text = "✓ Twitch session refreshed แล้ว";
            LogActivity("↻ Twitch token refreshed");
            return true;
        }
        finally
        {
            _authGate.Release();
        }
    }

    private void ApplyAuthenticatedToken(TwitchToken token)
    {
        _token = token;
        _authService ??= new TwitchAuthService(ClientIdBox.Text.Trim(), ClientSecretBox.Password.Trim());
        _twitch ??= new TwitchClient(ClientIdBox.Text.Trim(), token.AccessToken, EnsureAuthenticationAsync);
        _twitch.SetAccessToken(token.AccessToken);
        BotUsernameBox.Text = token.Login;
        BotUserIdBox.Text = token.UserId;
        AccountNameText.Text = "@" + token.Login;
        _lastAuthValidationUtc = token.LastValidatedUtc == default ? DateTimeOffset.UtcNow : token.LastValidatedUtc;
    }

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        await DisconnectTwitchAsync();
    }

    private async Task DisconnectTwitchAsync()
    {
        try
        {
            SetBusy(true, "กำลัง Disconnect Twitch...");
            if (_token is not null && _authService is not null)
            {
                try { await _authService.RevokeAsync(_token.AccessToken); } catch { }
            }
        }
        finally
        {
            await StopKeywordTimeoutAsync();
            AuthSessionStore.Clear();
            ClearAuthenticatedState();
            _authTimer.Stop();
            LogActivity("× Twitch disconnected");
            OpenLoginWindow();
        }
    }

    private void ClearAuthenticatedState()
    {
        _running = false;
        _timer.Stop();
        _token = null;
        _twitch = null;
        _authService = null;
        _lastAuthValidationUtc = DateTimeOffset.MinValue;
        BotUsernameBox.Clear();
        BotUserIdBox.Clear();
        ConnectionText.Text = "Not connected";
        SessionText.Text = "ต้อง Login Twitch ก่อนเริ่ม Bot";
        DisconnectButton.Visibility = Visibility.Collapsed;
        LoginButton.Visibility = Visibility.Collapsed;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = false;
    }

    private void SetConnectedUi()
    {
        ConnectionText.Text = $"Connected: {_token?.Login}";
        SessionText.Text = "✓ Session พร้อมใช้งาน • ระบบจะตรวจสอบและ refresh ให้อัตโนมัติ";
        LoginButton.Visibility = Visibility.Collapsed;
        DisconnectButton.Visibility = Visibility.Visible;
        ClientIdBox.IsEnabled = false;
        ClientSecretBox.IsEnabled = false;
    }

    private void SetBusy(bool busy, string status)
    {
        DisconnectButton.IsEnabled = !busy && _token is not null;
        StartButton.IsEnabled = !busy && _token is not null && !_running;
        StatusText.Text = status;
    }

    private void OpenLoginWindow()
    {
        if (Application.Current.Windows.OfType<LoginWindow>().Any()) return;
        var login = new LoginWindow();
        Application.Current.MainWindow = login;
        login.Show();
        Close();
    }

    private void AddChannelButton_Click(object sender, RoutedEventArgs e)
    {
        AddChannel(NewChannelBox.Text);
    }

    private void AddChannel(string? value)
    {
        var channel = value?.Trim().TrimStart('@') ?? "";
        if (string.IsNullOrWhiteSpace(channel)) return;

        if (_channels.Any(x => x.Channel.Equals(channel, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText.Text = $"ช่อง {channel} มีอยู่แล้ว";
            NewChannelBox.Clear();
            return;
        }

        var newConfig = new ChannelConfig { Channel = channel, IsEnabled = true };
        EnsureChannelEventSettings(newConfig);
        EnsureKeywordSettings(newConfig);
        _channels.Add(newConfig);
        NewChannelBox.Clear();
        SaveSettings();
        RefreshChannelCount();
        StatusText.Text = $"เพิ่มช่อง {channel} แล้ว";
    }

    private void RemoveChannelButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ChannelConfig channel) return;

        var answer = System.Windows.MessageBox.Show(
            $"ต้องการลบ {channel.Channel} ออกจากรายการหรือไม่?\n\nการลบจะไม่ส่งข้อความไปช่องนี้อีก",
            "Remove channel",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

        _channels.Remove(channel);
        SaveSettings();
        RefreshChannelCount();
        StatusText.Text = $"ลบช่อง {channel.Channel} แล้ว";
    }

    private void EventSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ChannelConfig channel) return;
        EnsureChannelEventSettings(channel);
        var dialog = new EventSettingsWindow(channel, _eventEngineSettings)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            ActivityEnabledBox.IsChecked = _eventEngineSettings.Enabled;
            SaveSettings();
            StatusText.Text = $"บันทึก Event Engine ของ {channel.Channel} แล้ว";
            ChannelList.Items.Refresh();
        }
    }

    private void ChannelEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.DataContext is not ChannelConfig channel) return;
        SaveSettings();
        StatusText.Text = channel.IsEnabled
            ? $"เปิด Bot สำหรับ {channel.Channel}"
            : $"ปิด Bot สำหรับ {channel.Channel}";
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveSettings();

            if (!await EnsureAuthenticationAsync(forceValidation: false))
            {
                System.Windows.MessageBox.Show("Twitch session ใช้งานไม่ได้ กรุณา Login ใหม่", "Twitch Hourly Bot");
                return;
            }

            if (!_channels.Any(c => c.IsEnabled))
            {
                System.Windows.MessageBox.Show("เปิด Bot อย่างน้อย 1 ช่องก่อน", "Twitch Hourly Bot");
                return;
            }

            _running = true;
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            StatusText.Text = "Bot เริ่มทำงานแล้ว กำลังตรวจสอบช่อง...";
            LogActivity("▶ Bot started");
            InitializeActivityTimer();
            await TickAsync();
            await StartKeywordTimeoutAsync();
            _timer.Start();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Start error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _running = false;
        _timer.Stop();
        await StopKeywordTimeoutAsync();
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        ActivityNextText.Text = "Next activity: -";
        StatusText.Text = "Bot หยุดทำงานแล้ว";
        LogActivity("■ Bot stopped");
    }

    private async Task StartKeywordTimeoutAsync()
    {
        if (_twitch is null || _token is null || string.IsNullOrWhiteSpace(_token.UserId)) return;

        var channels = new List<KeywordTimeoutChannel>();
        foreach (var config in _channels.Where(c => c.IsEnabled && c.KeywordProtectionEnabled))
        {
            EnsureKeywordSettings(config);
            try
            {
                var user = await _twitch.GetUserAsync(config.Channel);
                if (user is not null)
                {
                    var rules = config.KeywordRules.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Keyword)).ToList();
                    if (rules.Count > 0)
                        channels.Add(new KeywordTimeoutChannel(config.Channel, user.Id, rules));
                }
            }
            catch (Exception ex)
            {
                LogActivity($"⚠ Keyword Timeout: อ่านช่อง {config.Channel} ไม่สำเร็จ: {ex.Message}");
            }
        }

        if (channels.Count == 0) return;

        await StopKeywordTimeoutAsync();
        _keywordTimeoutService = new KeywordTimeoutService(
            ClientIdBox.Text.Trim(),
            _twitch,
            _token.UserId,
            () => _token?.AccessToken ?? "",
            channels,
            LogActivity);
        await _keywordTimeoutService.StartAsync();
        LogActivity($"🛡 Keyword Timeout enabled: {channels.Sum(c => c.Rules.Count)} keyword rules across {channels.Count} channel(s)");
    }

    private async Task StopKeywordTimeoutAsync()
    {
        if (_keywordTimeoutService is null) return;
        var service = _keywordTimeoutService;
        _keywordTimeoutService = null;
        await service.DisposeAsync();
    }

    private void KeywordChannelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshKeywordEditor();
    }

    private void RefreshKeywordEditor()
    {
        if (KeywordChannelList is null || KeywordRuleList is null) return;
        if (KeywordChannelList.SelectedItem is not ChannelConfig channel)
        {
            KeywordChannelTitle.Text = "เลือก Channel";
            KeywordProtectionEnabledBox.IsChecked = false;
            KeywordRuleList.ItemsSource = null;
            KeywordEmptyText.Visibility = Visibility.Visible;
            return;
        }

        EnsureKeywordSettings(channel);
        KeywordChannelTitle.Text = "#" + channel.Channel;
        KeywordProtectionEnabledBox.IsChecked = channel.KeywordProtectionEnabled;
        KeywordRuleList.ItemsSource = channel.KeywordRules;
        KeywordEmptyText.Visibility = channel.KeywordRules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        KeywordChannelList.Items.Refresh();
    }

    private void KeywordProtectionEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (KeywordChannelList?.SelectedItem is not ChannelConfig channel) return;
        if (sender is CheckBox checkBox && checkBox.IsChecked.HasValue)
            channel.KeywordProtectionEnabled = checkBox.IsChecked.Value;
        SaveSettings();
        StatusText.Text = channel.KeywordProtectionEnabled
            ? $"เปิด Keyword Protection สำหรับ {channel.Channel}"
            : $"ปิด Keyword Protection สำหรับ {channel.Channel}";
        KeywordChannelList.Items.Refresh();
        if (_running)
            _ = RestartKeywordTimeoutAsync();
    }

    private void AddKeywordButton_Click(object sender, RoutedEventArgs e)
    {
        if (KeywordChannelList.SelectedItem is not ChannelConfig channel)
        {
            StatusText.Text = "เลือก Channel ก่อนเพิ่ม Keyword";
            return;
        }

        var keyword = NewKeywordBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(keyword)) return;
        if (channel.KeywordRules.Any(r => r.Keyword.Equals(keyword, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText.Text = $"คำว่า {keyword} มีอยู่แล้วใน {channel.Channel}";
            return;
        }

        var timeout = 60;
        if (NewKeywordTimeoutBox.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out var selected))
            timeout = selected;

        channel.KeywordRules.Add(new KeywordRule { Keyword = keyword, Enabled = true, TimeoutSeconds = KeywordMatcher.ClampTimeout(timeout) });
        channel.KeywordProtectionEnabled = true;
        NewKeywordBox.Clear();
        SaveSettings();
        RefreshKeywordEditor();
        StatusText.Text = $"เพิ่มคำว่า {keyword} ใน {channel.Channel} แล้ว";
        if (_running) _ = RestartKeywordTimeoutAsync();
    }

    private void RemoveKeywordButton_Click(object sender, RoutedEventArgs e)
    {
        if (KeywordChannelList.SelectedItem is not ChannelConfig channel) return;
        if ((sender as Button)?.Tag is not KeywordRule rule) return;

        channel.KeywordRules.Remove(rule);
        SaveSettings();
        RefreshKeywordEditor();
        StatusText.Text = $"ลบคำว่า {rule.Keyword} จาก {channel.Channel} แล้ว";
        if (_running) _ = RestartKeywordTimeoutAsync();
    }

    private void KeywordRuleEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (KeywordChannelList.SelectedItem is not ChannelConfig channel) return;
        SaveSettings();
        RefreshKeywordEditor();
        if (_running) _ = RestartKeywordTimeoutAsync();
    }

    private void KeywordTimeout_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source is not ComboBox) return;
        if (KeywordChannelList.SelectedItem is not ChannelConfig channel) return;
        SaveSettings();
        if (_running) _ = RestartKeywordTimeoutAsync();
    }

    private async Task RestartKeywordTimeoutAsync()
    {
        try
        {
            await StartKeywordTimeoutAsync();
        }
        catch (Exception ex)
        {
            LogActivity("⚠ Keyword Timeout restart failed: " + ex.Message);
        }
    }

    private static void EnsureKeywordSettings(ChannelConfig channel)
    {
        // No default keyword: rules are only the ones the streamer adds.
        channel.KeywordRules ??= new ObservableCollection<KeywordRule>();
    }

    private async Task TickAsync()
    {
        if (!_running || _twitch is null || _token is null) return;

        try
        {
            var enabledChannels = _channels.Where(c => c.IsEnabled).ToList();
            foreach (var config in enabledChannels)
                await CheckChannelAsync(config);

            // Disabled channels are intentionally not polled.
            if (ActivityEnabledBox.IsChecked == true)
                await HandleRandomActivityAsync(enabledChannels);

            ChannelList.Items.Refresh();
            RefreshChannelCount();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error: " + ex.Message;
            LogActivity("⚠ " + ex.Message);
        }
    }

    private async Task CheckChannelAsync(ChannelConfig config)
    {
        var channel = config.Channel;
        try
        {
            var user = await _twitch!.GetUserAsync(channel);
            if (user is null)
            {
                config.IsLive = false;
                config.Status = "⚠ NOT FOUND";
                config.LiveTime = "-";
                config.NextMessage = "-";
                return;
            }

            var stream = await _twitch.GetStreamAsync(user.Id);
            if (stream is null)
            {
                config.IsLive = false;
                config.Status = "🔴 OFFLINE";
                config.LiveTime = "-";
                config.NextMessage = "รอ Live";
                return;
            }

            var elapsed = DateTimeOffset.UtcNow - stream.StartedAt;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

            config.IsLive = true;
            config.Status = "🟢 LIVE";
            config.LiveTime = Format(elapsed);
            config.LastChecked = DateTime.Now.ToString("HH:mm:ss");

            var completedHours = (int)Math.Floor(elapsed.TotalHours);
            var state = StateStore.Get(channel);

            if (!string.Equals(state.StreamId, stream.Id, StringComparison.Ordinal))
            {
                StateStore.StartStream(channel, stream.Id, completedHours, _intervalHours);
                state = StateStore.Get(channel);
            }

            var announced = state.LastAnnouncedHour;
            if (completedHours >= _intervalHours && completedHours >= announced + _intervalHours)
            {
                var hoursToAnnounce = (completedHours / _intervalHours) * _intervalHours;
                var message = MessageTemplateBox.Text
                    .Replace("{channel}", channel)
                    .Replace("{hours}", hoursToAnnounce.ToString())
                    .Replace("{minutes}", ((int)elapsed.TotalMinutes).ToString())
                    .Replace("{live_time}", Format(elapsed));

                // Use the authenticated Twitch account ID as the chat sender ID.
                // Do not depend on a hidden UI field; it is populated from OAuth validation.
                var senderId = _token?.UserId?.Trim();
                if (!string.IsNullOrWhiteSpace(senderId))
                {
                    await _twitch.SendChatMessageAsync(senderId, user.Id, message);
                    StateStore.SetLastAnnouncedHour(channel, hoursToAnnounce);
                    config.LastMessage = $"{DateTime.Now:HH:mm:ss}  {message}";
                    LogActivity($"💬 {channel}: automated message sent");
                }
                else
                {
                    config.LastMessage = "ไม่พบ Twitch User ID ของบัญชีที่ Login";
                    LogActivity($"⚠ {channel}: ไม่พบ Twitch User ID ของบัญชีที่ Login");
                }
            }

            var currentAnnounced = StateStore.GetLastAnnouncedHour(channel);
            var next = currentAnnounced + _intervalHours;
            var until = TimeSpan.FromHours(next) - elapsed;
            config.NextMessage = until > TimeSpan.Zero ? Format(until) : "Now";
            StatusText.Text = $"Checked {channel} • Live {Format(elapsed)}";
        }
        catch (Exception ex)
        {
            config.IsLive = false;
            config.Status = "⚠ ERROR";
            config.NextMessage = ex.Message.Length > 45 ? ex.Message[..45] + "…" : ex.Message;
        }
    }

    private void InitializeActivityTimer()
    {
        if (!_eventEngineSettings.Enabled)
        {
            _nextActivityAt = null;
            ActivityNextText.Text = "Next event: ปิดอยู่";
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(5, _eventEngineSettings.IntervalMinutes));
        _nextActivityAt ??= DateTimeOffset.Now.Add(interval);
        UpdateActivityCountdown();
    }

    private async Task HandleRandomActivityAsync(IReadOnlyList<ChannelConfig> enabledChannels)
    {
        if (!_eventEngineSettings.Enabled || ActivityEnabledBox.IsChecked != true || enabledChannels.Count == 0)
        {
            ActivityNextText.Text = enabledChannels.Count == 0
                ? "Next event: ไม่มีช่องที่เปิดอยู่"
                : "Next event: ปิดอยู่";
            return;
        }

        if (_nextActivityAt is null)
            InitializeActivityTimer();

        UpdateActivityCountdown();
        if (_nextActivityAt is null || DateTimeOffset.Now < _nextActivityAt.Value) return;

        var now = DateTimeOffset.UtcNow;
        var sent = 0;
        foreach (var config in enabledChannels)
        {
            try
            {
                var user = await _twitch!.GetUserAsync(config.Channel);
                var stream = user is null ? null : await _twitch.GetStreamAsync(user.Id);
                if (user is null || stream is null) continue;

                var selected = _eventEngine.PickEvent(config, _eventEngineSettings, now);
                if (selected is null) continue;
                var senderId = _token?.UserId?.Trim();
                if (string.IsNullOrWhiteSpace(senderId))
                {
                    LogActivity($"⚠ {config.Channel}: ไม่พบ Twitch User ID ของบัญชีที่ Login");
                    continue;
                }

                await _twitch.SendChatMessageAsync(senderId, user.Id, selected.Text);
                CooldownStore.MarkSent(config.Channel, selected.Category, selected.Id, now);
                config.LastActivity = $"{DateTime.Now:HH:mm:ss}  {selected.Text}";
                LogActivity($"🧘 {config.Channel}: event sent ({selected.Category})");
                sent++;
            }
            catch
            {
                // One channel/event should not stop the other channels.
            }
        }

        _nextActivityAt = DateTimeOffset.Now.AddMinutes(Math.Max(5, _eventEngineSettings.IntervalMinutes));
        UpdateActivityCountdown();
        StatusText.Text = sent > 0
            ? $"Event Engine ส่งแล้ว {sent} ช่อง"
            : "ถึงรอบ Event Engine แต่ยังไม่มี Event ที่ผ่าน cooldown";
    }

    private void UpdateActivityCountdown()
    {
        if (ActivityEnabledBox.IsChecked != true)
        {
            ActivityNextText.Text = "Next event: ปิดอยู่";
            return;
        }

        if (_nextActivityAt is null)
        {
            ActivityNextText.Text = "Next event: รอ Start Bot";
            return;
        }

        var remaining = _nextActivityAt.Value - DateTimeOffset.Now;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        ActivityNextText.Text = $"Next event: {remaining.Hours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";
    }

    private static string Format(TimeSpan t) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

    private static void EnsureChannelEventSettings(ChannelConfig channel)
    {
        channel.EventSettings ??= ChannelEventSettings.CreateDefault();
        foreach (var category in EventCategories.All)
            _ = channel.EventSettings.GetCategory(category);
    }

    private void LogActivity(string message)
    {
        _activityLog.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        while (_activityLog.Count > 40)
            _activityLog.RemoveAt(_activityLog.Count - 1);
    }

    private void RefreshChannelCount()
    {
        var enabled = _channels.Count(c => c.IsEnabled);
        ChannelCountText.Text = $"{_channels.Count} channels • {enabled} active";
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _authTimer.Stop();
        try { _keywordTimeoutService?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        _keywordTimeoutService = null;
        SaveSettings();
        base.OnClosed(e);
    }
}

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

public sealed class AppSettings
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string BotUsername { get; set; } = "";
    public string BotUserId { get; set; } = "";
    public string Channel { get; set; } = ""; // legacy compatibility
    public int IntervalHours { get; set; } = 1;
    public string MessageTemplate { get; set; } = "⏰ {channel} Live มาแล้ว {hours} ชั่วโมง! 🎉";
    public bool ActivityEnabled { get; set; } = true; // legacy compatibility
    public EventEngineSettings EventEngine { get; set; } = new();
    public List<ChannelConfig> Channels { get; set; } = new();
}

public static class SettingsStore
{
    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TwitchHourlyBot");
    private static string FilePath => Path.Combine(Folder, "settings.json");
    private static string LegacyFilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var path = File.Exists(FilePath) ? FilePath : LegacyFilePath;
            if (!File.Exists(path)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AppSettings();
        }
        catch { return new AppSettings(); }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void SaveCredentials(string clientId, string clientSecret)
    {
        var settings = Load();
        settings.ClientId = clientId.Trim();
        settings.ClientSecret = "";
        SecureStorage.SaveSecret("client-secret.dat", clientSecret.Trim());
        Save(settings);
    }
}

public sealed class StreamState
{
    public string StreamId { get; set; } = "";
    public int LastAnnouncedHour { get; set; }
}

public static class StateStore
{
    private static readonly object Sync = new();
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TwitchHourlyBot");
    private static readonly string FilePath = Path.Combine(Folder, "state.json");
    private static readonly string LegacyFilePath = Path.Combine(AppContext.BaseDirectory, "state.json");
    private static Dictionary<string, StreamState> Data = Load();

    public static StreamState Get(string channel)
    {
        lock (Sync)
        {
            var key = channel.ToLowerInvariant();
            if (!Data.TryGetValue(key, out var state))
            {
                state = new StreamState();
                Data[key] = state;
            }

            return new StreamState { StreamId = state.StreamId, LastAnnouncedHour = state.LastAnnouncedHour };
        }
    }

    public static void StartStream(string channel, string streamId, int completedHours, int intervalHours = 1)
    {
        lock (Sync)
        {
            var interval = Math.Max(1, intervalHours);
            var lastCompletedInterval = (completedHours / interval) * interval;
            Data[channel.ToLowerInvariant()] = new StreamState
            {
                StreamId = streamId,
                LastAnnouncedHour = lastCompletedInterval
            };
            Save();
        }
    }

    public static int GetLastAnnouncedHour(string channel) => Get(channel).LastAnnouncedHour;

    public static void SetLastAnnouncedHour(string channel, int hour)
    {
        lock (Sync)
        {
            var key = channel.ToLowerInvariant();
            if (!Data.TryGetValue(key, out var state))
            {
                state = new StreamState();
                Data[key] = state;
            }
            state.LastAnnouncedHour = hour;
            Save();
        }
    }

    private static void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Dictionary<string, StreamState> Load()
    {
        try
        {
            var path = File.Exists(FilePath) ? FilePath : LegacyFilePath;
            if (!File.Exists(path)) return new();
            return JsonSerializer.Deserialize<Dictionary<string, StreamState>>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch { return new(); }
    }
}
