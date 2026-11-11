using System;

namespace TwitchHourlyBot;

/// <summary>Pure scheduling logic for the "live for N hours" announcement, shared by the desktop and web hosts.</summary>
public static class AnnouncementPlanner
{
    public sealed record Decision(int CompletedHours, int AnnouncedBefore, bool ShouldSend, int HoursToAnnounce, TimeSpan UntilNext);

    /// <param name="elapsed">Time since the stream started.</param>
    /// <param name="lastAnnouncedHour">Last hour mark already announced for this stream (ignored when <paramref name="newStream"/>).</param>
    /// <param name="newStream">True when this stream has not been seen before: earlier hour marks are treated as already announced.</param>
    public static Decision Plan(TimeSpan elapsed, int lastAnnouncedHour, bool newStream, int intervalHours)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        var interval = Math.Max(1, intervalHours);
        var completed = (int)Math.Floor(elapsed.TotalHours);
        var announced = newStream ? (completed / interval) * interval : lastAnnouncedHour;

        var shouldSend = completed >= interval && completed >= announced + interval;
        var hoursToAnnounce = (completed / interval) * interval;
        var announcedAfter = shouldSend ? hoursToAnnounce : announced;
        var until = TimeSpan.FromHours(announcedAfter + interval) - elapsed;
        return new Decision(completed, announced, shouldSend, hoursToAnnounce, until);
    }

    public static string Render(string template, string channel, int hours, TimeSpan elapsed) =>
        template
            .Replace("{channel}", channel)
            .Replace("{hours}", hours.ToString())
            .Replace("{minutes}", ((int)elapsed.TotalMinutes).ToString())
            .Replace("{live_time}", FormatDuration(elapsed));

    public static string FormatDuration(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
}
