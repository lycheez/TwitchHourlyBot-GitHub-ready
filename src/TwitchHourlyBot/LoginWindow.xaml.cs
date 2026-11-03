using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace TwitchHourlyBot;

public partial class LoginWindow : Window
{
    private bool _busy;

    public LoginWindow()
    {
        InitializeComponent();
        LoadCredentials();
        Loaded += async (_, _) => await TryRestoreSessionAsync();
    }

    private void LoadCredentials()
    {
        var settings = SettingsStore.Load();
        ClientIdBox.Text = settings.ClientId;
        ClientSecretBox.Password = string.IsNullOrWhiteSpace(settings.ClientSecret)
            ? SecureStorage.LoadSecret("client-secret.dat")
            : settings.ClientSecret;
    }

    private async Task TryRestoreSessionAsync()
    {
        if (_busy) return;

        var clientId = ClientIdBox.Text.Trim();
        var clientSecret = ClientSecretBox.Password.Trim();
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            return;

        var saved = AuthSessionStore.Load(clientId);
        if (saved is null) return;

        try
        {
            SetBusy(true, "กำลังตรวจสอบ Twitch session...");
            var auth = new TwitchAuthService(clientId, clientSecret);
            if (!await auth.ValidateAsync(saved))
            {
                SetBusy(true, "Session หมดอายุ กำลัง refresh...");
                var refreshed = await auth.RefreshAsync(saved);
                if (refreshed is null)
                {
                    AuthSessionStore.Clear();
                    SetBusy(false, "Session ใช้งานไม่ได้ • กรุณา Login ใหม่");
                    return;
                }
                saved = refreshed;
            }

            AuthSessionStore.Save(saved, clientId);
            SettingsStore.SaveCredentials(clientId, clientSecret);
            OpenDashboard();
        }
        catch (Exception ex)
        {
            SetBusy(false, "Auto-login ไม่สำเร็จ • สามารถ Login ใหม่ได้");
            StatusText.Text = ex.Message;
        }
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        try
        {
            var clientId = ClientIdBox.Text.Trim();
            var clientSecret = ClientSecretBox.Password.Trim();
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
                throw new InvalidOperationException("กรุณากรอก Client ID และ Client Secret");

            SetBusy(true, "กำลังเปิดหน้า Twitch authorization...");
            var auth = new TwitchAuthService(clientId, clientSecret);
            var token = await auth.LoginAsync();

            SettingsStore.SaveCredentials(clientId, clientSecret);
            AuthSessionStore.Save(token, clientId);
            OpenDashboard();
        }
        catch (OperationCanceledException)
        {
            SetBusy(false, "ยกเลิกการ Login");
        }
        catch (Exception ex)
        {
            SetBusy(false, "Login ไม่สำเร็จ");
            MessageBox.Show(ex.Message, "Twitch Login", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SetBusy(bool busy, string message)
    {
        _busy = busy;
        LoginButton.IsEnabled = !busy;
        ClientIdBox.IsEnabled = !busy;
        ClientSecretBox.IsEnabled = !busy;
        StatusText.Text = message;
    }

    private void OpenDashboard()
    {
        var dashboard = new MainWindow();
        Application.Current.MainWindow = dashboard;
        dashboard.Show();
        Close();
    }
}
