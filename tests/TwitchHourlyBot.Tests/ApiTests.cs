using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TwitchHourlyBot.Api;
using Xunit;

namespace TwitchHourlyBot.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public ApiFactory() => _connection.Open();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            var descriptor = services.Single(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            services.Remove(descriptor);
            services.AddDbContext<AppDbContext>(o => o.UseSqlite(_connection));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }
}

public class ApiTests : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client;

    public ApiTests(ApiFactory factory) => _client = factory.CreateClient();

    private static string UniqueName() => "ch_" + Guid.NewGuid().ToString("N").Substring(0, 12);

    private async Task<ChannelDto> CreateChannel(string? name = null)
    {
        var response = await _client.PostAsJsonAsync("/api/channels", new CreateChannelRequest(name ?? UniqueName()));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ChannelDto>())!;
    }

    private async Task<RuleDto> CreateRule(int channelId, string keyword, int? seconds = null, bool? enabled = null)
    {
        var response = await _client.PostAsJsonAsync($"/api/channels/{channelId}/rules", new CreateRuleRequest(keyword, seconds, enabled));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RuleDto>())!;
    }

    private async Task<CheckMessageResult> Check(int channelId, string message)
    {
        var response = await _client.PostAsJsonAsync($"/api/channels/{channelId}/check", new CheckMessageRequest(message));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CheckMessageResult>())!;
    }

    [Fact]
    public async Task Health_returns_ok()
    {
        var response = await _client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Channel_name_is_normalized()
    {
        var name = UniqueName().ToUpperInvariant();
        var channel = await CreateChannel("@" + name);
        Assert.Equal(name.ToLowerInvariant(), channel.Name);
        Assert.True(channel.IsEnabled);
        Assert.Equal(0, channel.RuleCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad name")]
    [InlineData("this_channel_name_is_way_too_long")]
    public async Task Invalid_channel_name_is_rejected(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/channels", new CreateChannelRequest(name));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Duplicate_channel_is_conflict()
    {
        var channel = await CreateChannel();
        var response = await _client.PostAsJsonAsync("/api/channels", new CreateChannelRequest(channel.Name.ToUpperInvariant()));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Created_channel_appears_in_list()
    {
        var channel = await CreateChannel();
        var list = await _client.GetFromJsonAsync<List<ChannelDto>>("/api/channels");
        Assert.Contains(list!, c => c.Id == channel.Id);
    }

    [Fact]
    public async Task Update_channel_changes_flags()
    {
        var channel = await CreateChannel();
        var response = await _client.PutAsJsonAsync($"/api/channels/{channel.Id}", new UpdateChannelRequest(false, false));
        var updated = (await response.Content.ReadFromJsonAsync<ChannelDto>())!;
        Assert.False(updated.IsEnabled);
        Assert.False(updated.KeywordProtectionEnabled);
    }

    [Fact]
    public async Task Unknown_channel_returns_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync("/api/channels/999999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/channels/999999/rules")).StatusCode);
    }

    [Fact]
    public async Task New_channel_has_no_default_keyword()
    {
        var channel = await CreateChannel();
        var rules = await _client.GetFromJsonAsync<List<RuleDto>>($"/api/channels/{channel.Id}/rules");
        Assert.Empty(rules!);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(60, 60)]
    [InlineData(100000, 600)]
    public async Task Rule_timeout_is_clamped(int requested, int expected)
    {
        var channel = await CreateChannel();
        var rule = await CreateRule(channel.Id, "word", requested);
        Assert.Equal(expected, rule.TimeoutSeconds);
    }

    [Fact]
    public async Task Rule_defaults_to_enabled_with_60_seconds()
    {
        var channel = await CreateChannel();
        var rule = await CreateRule(channel.Id, "word");
        Assert.True(rule.Enabled);
        Assert.Equal(60, rule.TimeoutSeconds);
    }

    [Fact]
    public async Task Blank_rule_keyword_is_rejected()
    {
        var channel = await CreateChannel();
        var response = await _client.PostAsJsonAsync($"/api/channels/{channel.Id}/rules", new CreateRuleRequest("  ", 60, true));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Duplicate_rule_ignoring_case_is_conflict()
    {
        var channel = await CreateChannel();
        await CreateRule(channel.Id, "Spoiler");
        var response = await _client.PostAsJsonAsync($"/api/channels/{channel.Id}/rules", new CreateRuleRequest("SPOILER", 60, true));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Same_keyword_is_allowed_on_different_channels()
    {
        var a = await CreateChannel();
        var b = await CreateChannel();
        await CreateRule(a.Id, "shared");
        await CreateRule(b.Id, "shared");
    }

    [Fact]
    public async Task Update_and_delete_rule()
    {
        var channel = await CreateChannel();
        var rule = await CreateRule(channel.Id, "word", 60);

        var put = await _client.PutAsJsonAsync($"/api/channels/{channel.Id}/rules/{rule.Id}", new UpdateRuleRequest(false, 120));
        var updated = (await put.Content.ReadFromJsonAsync<RuleDto>())!;
        Assert.False(updated.Enabled);
        Assert.Equal(120, updated.TimeoutSeconds);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/channels/{channel.Id}/rules/{rule.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/channels/{channel.Id}/rules/{rule.Id}")).StatusCode);
    }

    [Fact]
    public async Task Rule_cannot_be_changed_through_another_channel()
    {
        var a = await CreateChannel();
        var b = await CreateChannel();
        var rule = await CreateRule(a.Id, "word");
        var response = await _client.PutAsJsonAsync($"/api/channels/{b.Id}/rules/{rule.Id}", new UpdateRuleRequest(false, 30));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Deleting_channel_removes_it_from_list()
    {
        var channel = await CreateChannel();
        await CreateRule(channel.Id, "word");
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/channels/{channel.Id}")).StatusCode);
        var list = await _client.GetFromJsonAsync<List<ChannelDto>>("/api/channels");
        Assert.DoesNotContain(list!, c => c.Id == channel.Id);
    }

    [Fact]
    public async Task Check_returns_matching_rule_ignoring_case()
    {
        var channel = await CreateChannel();
        await CreateRule(channel.Id, "Spoiler", 120);
        var result = await Check(channel.Id, "no SPOILER here");
        Assert.True(result.Matched);
        Assert.Equal("Spoiler", result.Keyword);
        Assert.Equal(120, result.TimeoutSeconds);
    }

    [Fact]
    public async Task Check_ignores_disabled_rules()
    {
        var channel = await CreateChannel();
        await CreateRule(channel.Id, "word", 60, enabled: false);
        Assert.False((await Check(channel.Id, "a word")).Matched);
    }

    [Fact]
    public async Task Check_does_not_match_when_protection_is_off()
    {
        var channel = await CreateChannel();
        await CreateRule(channel.Id, "word");
        await _client.PutAsJsonAsync($"/api/channels/{channel.Id}", new UpdateChannelRequest(true, false));
        Assert.False((await Check(channel.Id, "a word")).Matched);
    }

    [Fact]
    public async Task Check_with_no_rules_matches_nothing()
    {
        var channel = await CreateChannel();
        Assert.False((await Check(channel.Id, "anything")).Matched);
    }
}
