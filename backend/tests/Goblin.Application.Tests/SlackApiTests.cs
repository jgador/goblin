using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Conversations;
using Goblin.Integrations.Slack;
using Xunit;

namespace Goblin.Application.Tests;

public sealed class SlackApiTests
{
    private static readonly SlackCredentials Credentials = new("installation", "xapp-test-placeholder", "xoxb-test-placeholder", "Test", "T123", "A123", "U123");
    private static readonly JsonSerializerOptions DifferentPolicy = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseUpper };

    [Fact]
    public async Task PostingKeepsTheSlackFormContractWithoutSerializingThroughADom()
    {
        string? body = null;
        using var api = new SlackApi(new Handler(async request =>
        {
            Assert.Equal("https://slack.com/api/chat.postMessage", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
            body = await request.Content.ReadAsStringAsync();
            return Response("{\"ok\":true,\"unconsumed\":{\"ts\":\"123.4\"}}");
        }));
        await api.PostAsync(Credentials, "D123", "123.4", "Hello & goodbye", default);
        Assert.Equal("channel=D123&thread_ts=123.4&text=Hello+%26+goodbye&unfurl_links=false&unfurl_media=false&parse=none", body);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"ok\":false,\"error\":\"private-upstream-error\"}")]
    [InlineData("{\"ok\":\"true\"}")]
    [InlineData("null")]
    [InlineData("{broken")]
    public async Task FailedOrMalformedResponsesStaySafe(string json)
    {
        using var api = new SlackApi(new Handler(_ => Task.FromResult(Response(json))));
        SlackFailure error = await Assert.ThrowsAsync<SlackFailure>(() => api.PostAsync(Credentials, "D123", "123.4", "hello", default));
        Assert.DoesNotContain("private-upstream-error", error.Message);
    }

    [Fact]
    public async Task ResponsesStillHaveASizeBoundAndRateLimitsStayVisible()
    {
        using var large = new SlackApi(new Handler(_ => Task.FromResult(Response("{\"ok\":true,\"text\":\"" + new string('a', 1024 * 1024) + "\"}"))));
        await Assert.ThrowsAsync<SlackFailure>(() => large.PostAsync(Credentials, "D123", "123.4", "hello", default));
        using var limited = new SlackApi(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests))));
        Assert.Contains("rate limiting", (await Assert.ThrowsAsync<SlackFailure>(() => limited.PostAsync(Credentials, "D123", "123.4", "hello", default))).Message);
    }

    [Fact]
    public async Task AuthenticationStillRequiresAllRuntimeScopes()
    {
        using var api = new SlackApi(new Handler(_ =>
        {
            HttpResponseMessage response = Response("{\"ok\":true,\"team_id\":\"T123\",\"user_id\":\"U123\",\"bot_id\":\"B123\"}");
            response.Headers.Add("x-oauth-scopes", "chat:write");
            return Task.FromResult(response);
        }));
        Assert.Contains("required Slack scopes", (await Assert.ThrowsAsync<SlackFailure>(() => api.VerifyAsync(Credentials.AppToken, Credentials.BotToken, default))).Message);
    }

    [Theory]
    [InlineData("bot_id", "null")]
    [InlineData("bot_id", "123")]
    [InlineData("subtype", "null")]
    [InlineData("subtype", "\"\"")]
    public void BotAndSubtypePresenceIncludingNullStillSuppressesMessages(string field, string value)
    {
        string json = """
            {"type":"events_api","payload":{"api_app_id":"A123","team_id":"T123","event_id":"Ev123",
             "event":{"type":"message","channel_type":"im","user":"U456","channel":"D123",
             "text":"hello","ts":"123.4",
            """ + "\"" + field + "\":" + value + "}}}";
        SlackSocketEnvelope envelope = JsonSerializer.Deserialize<SlackSocketEnvelope>(json, DifferentPolicy)!;
        Assert.Null(SlackEvents.Parse(envelope, Credentials.Installation));
    }

    [Fact]
    public void SocketModeEnvelopeKeepsWireNamesAndMissingStringsRemainEmpty()
    {
        SlackSocketEnvelope envelope = JsonSerializer.Deserialize<SlackSocketEnvelope>("""
            {"type":"events_api","envelope_id":"envelope","retry_attempt":1,
             "payload":{"api_app_id":"A123","team_id":"T123","event_id":"Ev123",
              "event":{"type":"message","channel_type":"im","user":"U456","channel":"D123",
                       "text":"hello","ts":"123.4","thread_ts":null,"unconsumed":{"x":1}}}}
            """, DifferentPolicy)!;
        ExternalMessage message = Assert.IsType<ExternalMessage>(SlackEvents.Parse(envelope, Credentials.Installation));
        Assert.True(message.Direct);
        Assert.Equal("123.4", message.ThreadId);
        Assert.Equal("envelope", envelope.EnvelopeId);
        Assert.Equal("A123", JsonSerializer.Deserialize<SlackSocketEnvelope>("{\"type\":\"hello\",\"connection_info\":{\"app_id\":\"A123\"}}", DifferentPolicy)!.ConnectionInfo!.AppId);
        Assert.Equal("", JsonSerializer.Deserialize<SlackSocketEnvelope>("{\"type\":null}")!.Type);
    }

    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _send;

        public Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) => _send = send;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _send(request);
    }
}
