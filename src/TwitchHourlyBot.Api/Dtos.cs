namespace TwitchHourlyBot.Api;

public record ChannelDto(int Id, string Name, bool IsEnabled, bool KeywordProtectionEnabled, int RuleCount);
public record CreateChannelRequest(string? Name);
public record UpdateChannelRequest(bool IsEnabled, bool KeywordProtectionEnabled);

public record RuleDto(int Id, string Keyword, bool Enabled, int TimeoutSeconds);
public record CreateRuleRequest(string? Keyword, int? TimeoutSeconds, bool? Enabled);
public record UpdateRuleRequest(bool Enabled, int TimeoutSeconds);

public record CheckMessageRequest(string? Message);
public record CheckMessageResult(bool Matched, string? Keyword, int? TimeoutSeconds);

public record SettingsDto(string ClientId, bool HasClientSecret, int IntervalHours, string MessageTemplate, bool ActivityEnabled, int ActivityIntervalMinutes, string RedirectUri);
public record UpdateSettingsRequest(string? ClientId, string? ClientSecret, int? IntervalHours, string? MessageTemplate, bool? ActivityEnabled, int? ActivityIntervalMinutes);
public record LoginUrlResult(string Url);
