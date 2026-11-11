using System.Collections.Generic;
using TwitchHourlyBot;
using Xunit;

namespace TwitchHourlyBot.Tests;

public class KeywordMatcherTests
{
    private static KeywordRule Rule(string keyword, bool enabled = true, int seconds = 60) =>
        new() { Keyword = keyword, Enabled = enabled, TimeoutSeconds = seconds };

    [Fact]
    public void Matches_ignoring_case()
    {
        var rule = Rule("Spoiler");
        Assert.Same(rule, KeywordMatcher.FindMatch("no SPOILER please", new[] { rule }));
    }

    [Fact]
    public void Matches_keyword_with_surrounding_spaces_in_rule()
    {
        var rule = Rule("  abc  ");
        Assert.Same(rule, KeywordMatcher.FindMatch("xxabcxx", new[] { rule }));
    }

    [Fact]
    public void Disabled_rule_never_matches()
    {
        Assert.Null(KeywordMatcher.FindMatch("hello", new[] { Rule("hello", enabled: false) }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_keyword_never_matches(string keyword)
    {
        Assert.Null(KeywordMatcher.FindMatch("anything", new[] { Rule(keyword) }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Empty_message_matches_nothing(string? message)
    {
        Assert.Null(KeywordMatcher.FindMatch(message, new[] { Rule("a") }));
    }

    [Fact]
    public void No_rules_means_no_match()
    {
        Assert.Null(KeywordMatcher.FindMatch("hello", new List<KeywordRule>()));
    }

    [Fact]
    public void First_matching_rule_wins()
    {
        var first = Rule("foo");
        var second = Rule("foo bar");
        Assert.Same(first, KeywordMatcher.FindMatch("foo bar", new[] { first, second }));
    }

    [Fact]
    public void Matches_thai_text()
    {
        var rule = Rule("คำต้องห้าม");
        Assert.Same(rule, KeywordMatcher.FindMatch("นี่คือคำต้องห้ามนะ", new[] { rule }));
    }

    [Theory]
    [InlineData(-5, 1)]
    [InlineData(0, 1)]
    [InlineData(60, 60)]
    [InlineData(600, 600)]
    [InlineData(100000, 600)]
    public void Timeout_is_clamped_to_twitch_range(int input, int expected)
    {
        Assert.Equal(expected, KeywordMatcher.ClampTimeout(input));
    }
}
