using System;
using System.Collections.Generic;
using System.Linq;
using TwitchHourlyBot;
using Xunit;

namespace TwitchHourlyBot.Tests;

public class EventEngineTests
{
    private sealed class FakeCooldownStore : ICooldownStore
    {
        private readonly Dictionary<(string, string), (DateTimeOffset At, string Id)> _data = new();

        public bool IsReady(string channel, string category, int cooldownMinutes, DateTimeOffset now)
        {
            var last = GetLastSent(channel, category);
            return last == DateTimeOffset.MinValue || now - last >= TimeSpan.FromMinutes(Math.Max(0, cooldownMinutes));
        }

        public DateTimeOffset GetLastSent(string channel, string category) =>
            _data.TryGetValue((channel, category), out var v) ? v.At : DateTimeOffset.MinValue;

        public string GetLastMessageId(string channel, string category) =>
            _data.TryGetValue((channel, category), out var v) ? v.Id : "";

        public void MarkSent(string channel, string category, string messageId, DateTimeOffset now) =>
            _data[(channel, category)] = (now, messageId);
    }

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static (EventEngine Engine, FakeCooldownStore Store) CreateEngine(int seed = 1)
    {
        var store = new FakeCooldownStore();
        return (new EventEngine(store, new Random(seed)), store);
    }

    private static ChannelConfig Channel() => new() { Channel = "streamer" };

    private static EventEngineSettings Settings(params EventMessage[] messages) =>
        new() { Messages = messages.ToList() };

    private static EventMessage Msg(string id, string category, string text = "hello") =>
        new() { Id = id, Category = category, Text = text };

    [Fact]
    public void Returns_null_when_engine_disabled()
    {
        var (engine, _) = CreateEngine();
        var settings = Settings(Msg("a", EventCategories.Hydration));
        settings.Enabled = false;
        Assert.Null(engine.PickEvent(Channel(), settings, Now));
    }

    [Fact]
    public void Returns_null_when_channel_disabled()
    {
        var (engine, _) = CreateEngine();
        var channel = Channel();
        channel.IsEnabled = false;
        Assert.Null(engine.PickEvent(channel, Settings(Msg("a", EventCategories.Hydration)), Now));
    }

    [Fact]
    public void Returns_null_when_channel_events_disabled()
    {
        var (engine, _) = CreateEngine();
        var channel = Channel();
        channel.EventSettings.Enabled = false;
        Assert.Null(engine.PickEvent(channel, Settings(Msg("a", EventCategories.Hydration)), Now));
    }

    [Fact]
    public void Skips_blank_messages()
    {
        var (engine, _) = CreateEngine();
        Assert.Null(engine.PickEvent(Channel(), Settings(Msg("a", EventCategories.Hydration, "   ")), Now));
    }

    [Fact]
    public void Skips_disabled_categories()
    {
        var (engine, _) = CreateEngine();
        var channel = Channel();
        channel.EventSettings.GetCategory(EventCategories.Hydration).Enabled = false;
        Assert.Null(engine.PickEvent(channel, Settings(Msg("a", EventCategories.Hydration)), Now));
    }

    [Fact]
    public void Respects_category_cooldown()
    {
        var (engine, store) = CreateEngine();
        var channel = Channel();
        var settings = Settings(Msg("a", EventCategories.Hydration));
        store.MarkSent("streamer", EventCategories.Hydration, "a", Now.AddMinutes(-10));

        Assert.Null(engine.PickEvent(channel, settings, Now));
        Assert.NotNull(engine.PickEvent(channel, settings, Now.AddMinutes(50)));
    }

    [Fact]
    public void Zero_cooldown_is_always_ready()
    {
        var (engine, store) = CreateEngine();
        var channel = Channel();
        channel.EventSettings.GetCategory(EventCategories.Hydration).CooldownMinutes = 0;
        store.MarkSent("streamer", EventCategories.Hydration, "a", Now);
        Assert.NotNull(engine.PickEvent(channel, Settings(Msg("a", EventCategories.Hydration)), Now));
    }

    [Fact]
    public void Does_not_repeat_last_message_when_alternative_exists()
    {
        var (engine, store) = CreateEngine();
        var channel = Channel();
        channel.EventSettings.GetCategory(EventCategories.Hydration).CooldownMinutes = 0;
        var settings = Settings(Msg("a", EventCategories.Hydration), Msg("b", EventCategories.Hydration));
        store.MarkSent("streamer", EventCategories.Hydration, "a", Now.AddMinutes(-1));

        for (var i = 0; i < 25; i++)
            Assert.Equal("b", engine.PickEvent(channel, settings, Now)!.Id);
    }

    [Fact]
    public void Repeats_the_only_message_when_no_alternative()
    {
        var (engine, store) = CreateEngine();
        var channel = Channel();
        channel.EventSettings.GetCategory(EventCategories.Hydration).CooldownMinutes = 0;
        store.MarkSent("streamer", EventCategories.Hydration, "a", Now.AddMinutes(-1));

        Assert.Equal("a", engine.PickEvent(channel, Settings(Msg("a", EventCategories.Hydration)), Now)!.Id);
    }

    [Fact]
    public void Default_messages_cover_every_category()
    {
        var defaults = EventMessageDefaults.Create();
        foreach (var category in EventCategories.All)
            Assert.Contains(defaults, m => m.Category == category && !string.IsNullOrWhiteSpace(m.Text));
    }

    [Fact]
    public void Default_channel_settings_enable_every_category()
    {
        var settings = ChannelEventSettings.CreateDefault();
        foreach (var category in EventCategories.All)
            Assert.True(settings.Categories[category].Enabled);
    }
}
