using System;
using TwitchHourlyBot;
using Xunit;

namespace TwitchHourlyBot.Tests;

public class AnnouncementPlannerTests
{
    [Fact]
    public void New_stream_under_one_hour_does_not_send()
    {
        var plan = AnnouncementPlanner.Plan(TimeSpan.FromMinutes(30), 0, newStream: true, 1);
        Assert.False(plan.ShouldSend);
        Assert.Equal(TimeSpan.FromMinutes(30), plan.UntilNext);
    }

    [Fact]
    public void New_stream_already_past_hour_marks_treats_them_as_announced()
    {
        var plan = AnnouncementPlanner.Plan(new TimeSpan(2, 10, 0), 0, newStream: true, 1);
        Assert.False(plan.ShouldSend);
        Assert.Equal(2, plan.AnnouncedBefore);
        Assert.Equal(TimeSpan.FromMinutes(50), plan.UntilNext);
    }

    [Fact]
    public void Sends_when_next_hour_mark_is_reached()
    {
        var plan = AnnouncementPlanner.Plan(new TimeSpan(2, 5, 0), 1, newStream: false, 1);
        Assert.True(plan.ShouldSend);
        Assert.Equal(2, plan.HoursToAnnounce);
        Assert.Equal(TimeSpan.FromMinutes(55), plan.UntilNext);
    }

    [Fact]
    public void Does_not_send_before_the_mark()
    {
        var plan = AnnouncementPlanner.Plan(new TimeSpan(1, 59, 59), 1, newStream: false, 1);
        Assert.False(plan.ShouldSend);
    }

    [Fact]
    public void Missed_hours_are_announced_once_with_the_latest_mark()
    {
        var plan = AnnouncementPlanner.Plan(new TimeSpan(5, 0, 1), 1, newStream: false, 1);
        Assert.True(plan.ShouldSend);
        Assert.Equal(5, plan.HoursToAnnounce);
    }

    [Fact]
    public void Two_hour_interval_announces_on_even_marks()
    {
        Assert.False(AnnouncementPlanner.Plan(new TimeSpan(3, 30, 0), 0, newStream: true, 2).ShouldSend);
        var plan = AnnouncementPlanner.Plan(new TimeSpan(4, 0, 1), 2, newStream: false, 2);
        Assert.True(plan.ShouldSend);
        Assert.Equal(4, plan.HoursToAnnounce);
    }

    [Fact]
    public void Interval_below_one_is_treated_as_one()
    {
        Assert.True(AnnouncementPlanner.Plan(new TimeSpan(1, 0, 1), 0, newStream: false, 0).ShouldSend);
    }

    [Fact]
    public void Negative_elapsed_is_treated_as_zero()
    {
        var plan = AnnouncementPlanner.Plan(TimeSpan.FromMinutes(-5), 0, newStream: true, 1);
        Assert.False(plan.ShouldSend);
        Assert.Equal(0, plan.CompletedHours);
    }

    [Fact]
    public void Render_replaces_all_placeholders()
    {
        var text = AnnouncementPlanner.Render("{channel}: {hours}h / {minutes}m / {live_time}", "alpha", 2, new TimeSpan(2, 5, 9));
        Assert.Equal("alpha: 2h / 125m / 02:05:09", text);
    }

    [Fact]
    public void FormatDuration_pads_and_counts_hours_beyond_24()
    {
        Assert.Equal("30:01:02", AnnouncementPlanner.FormatDuration(new TimeSpan(30, 1, 2)));
    }
}
