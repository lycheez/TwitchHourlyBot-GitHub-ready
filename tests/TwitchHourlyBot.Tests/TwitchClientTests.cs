using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TwitchHourlyBot;
using Xunit;

namespace TwitchHourlyBot.Tests;

public class TwitchClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _statuses;
        public List<string?> BearerTokens { get; } = new();

        public StubHandler(params HttpStatusCode[] statuses) => _statuses = new Queue<HttpStatusCode>(statuses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            BearerTokens.Add(request.Headers.Authorization?.Parameter);
            var status = _statuses.Dequeue();
            var body = status == HttpStatusCode.OK ? "{\"data\":[{\"id\":\"42\",\"login\":\"streamer\"}]}" : "{}";
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task Returns_user_on_success()
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var client = new TwitchClient("cid", "tok", null, new HttpClient(handler));

        var user = await client.GetUserAsync("streamer");

        Assert.Equal("42", user!.Id);
        Assert.Equal(new[] { "tok" }, handler.BearerTokens);
    }

    [Fact]
    public async Task Retries_once_with_new_token_after_401()
    {
        var handler = new StubHandler(HttpStatusCode.Unauthorized, HttpStatusCode.OK);
        TwitchClient? client = null;
        client = new TwitchClient("cid", "old", _ =>
        {
            client!.SetAccessToken("new");
            return Task.FromResult(true);
        }, new HttpClient(handler));

        var user = await client.GetUserAsync("streamer");

        Assert.Equal("streamer", user!.Login);
        Assert.Equal(new[] { "old", "new" }, handler.BearerTokens);
    }

    [Fact]
    public async Task Throws_when_reauthentication_fails()
    {
        var handler = new StubHandler(HttpStatusCode.Unauthorized);
        var client = new TwitchClient("cid", "old", _ => Task.FromResult(false), new HttpClient(handler));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.GetUserAsync("streamer"));
        Assert.Single(handler.BearerTokens);
    }

    [Fact]
    public async Task Does_not_retry_without_recovery_callback()
    {
        var handler = new StubHandler(HttpStatusCode.Unauthorized);
        var client = new TwitchClient("cid", "old", null, new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetUserAsync("streamer"));
        Assert.Single(handler.BearerTokens);
    }
}
