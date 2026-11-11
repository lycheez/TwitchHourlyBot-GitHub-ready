using System;
using System.Collections.Generic;
using System.Linq;

namespace TwitchHourlyBot;

/// <summary>Pure keyword-rule helpers, kept separate from the EventSub plumbing so they can be unit tested.</summary>
public static class KeywordMatcher
{
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 600;

    public static int ClampTimeout(int seconds) => Math.Clamp(seconds, MinTimeoutSeconds, MaxTimeoutSeconds);

    /// <summary>Returns the first enabled rule whose (trimmed) keyword appears in the message, ignoring case.</summary>
    public static KeywordRule? FindMatch(string? message, IEnumerable<KeywordRule> rules)
    {
        if (string.IsNullOrEmpty(message)) return null;
        return rules
            .Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Keyword))
            .FirstOrDefault(r => message.IndexOf(r.Keyword.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
    }
}
