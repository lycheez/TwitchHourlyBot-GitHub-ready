namespace TwitchHourlyBot.Api.Bot;

/// <summary>
/// The Twitch application's credentials. They belong to whoever runs the server (not to the people who log in)
/// and come from configuration: environment variables Twitch__ClientId / Twitch__ClientSecret,
/// appsettings.Local.json, or user secrets. They are never shown in the dashboard.
/// </summary>
public sealed class TwitchOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public bool Configured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
