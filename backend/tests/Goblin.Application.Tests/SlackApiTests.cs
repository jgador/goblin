using System;
using System.IO;
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

    [Fact]
    public async Task QuestionsPostInTheirThreadEscapeMentionsAndMarkOnlySuccessfulDelivery()
    {
        string? repo = Environment.CurrentDirectory;
        while (repo is not null && !Directory.Exists(Path.Combine(repo, ".git"))) repo = Path.GetDirectoryName(repo);
        if (repo is null) throw new InvalidOperationException("Repository directory not found");
        string directory = Path.Combine(repo, ".artifacts", "slack-questions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var conversations = new Questions();
            bool fail = true;
            string? body = null;
            using var api = new SlackApi(new Handler(async request =>
            {
                body = await request.Content!.ReadAsStringAsync();
                return Response(fail ? "{\"ok\":false}" : "{\"ok\":true}");
            }));
            var store = new SlackCredentialStore(directory);
            await store.SaveAsync(Credentials, default);
            using var connection = new SlackConnection(api, store, conversations, "http://localhost:8788");
            await connection.DeliverQuestionAsync(Credentials, default);
            Assert.Equal(0, conversations.Deliveries);
            fail = false;
            await connection.DeliverQuestionAsync(Credentials, default);
            Assert.Equal(1, conversations.Deliveries);
            string posted = System.Net.WebUtility.UrlDecode(body)!;
            Assert.Contains("channel=D123&thread_ts=123.4&", posted);
            Assert.Contains("Which &lt;@U456&gt; &amp; region?", posted);
            Assert.Contains("Reply in this thread", posted);
            Assert.Contains("http://localhost:8788/work?item=2", posted);
            await connection.DisconnectAsync(default);
            await connection.DeliverQuestionAsync(Credentials, default);
            Assert.Equal(1, conversations.Deliveries);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class Questions : IExternalConversations
    {
        public int Deliveries { get; private set; }
        public Task AcceptAsync(ExternalMessage message, CancellationToken token) => throw new InvalidOperationException();
        public Task<ExternalReply?> ProcessNextAsync(ExternalInstallation installation, CancellationToken token) => throw new InvalidOperationException();
        public Task<ExternalQuestion?> NextQuestionAsync(ExternalInstallation installation, CancellationToken token) =>
            Task.FromResult<ExternalQuestion?>(new(1, 2, 3, "D123", "123.4", "Which <@U456> & region?"));
        public Task<ExternalWorkUpdate[]> PendingUpdatesAsync(ExternalInstallation installation, CancellationToken token) => throw new InvalidOperationException();
        public Task<bool> UpdateIsCurrentAsync(ExternalInstallation installation, ExternalWorkUpdate update, CancellationToken token) => throw new InvalidOperationException();
        public Task UpdateSentAsync(ExternalInstallation installation, ExternalWorkUpdate update, CancellationToken token) => throw new InvalidOperationException();
        public Task QuestionSentAsync(ExternalInstallation installation, ExternalQuestion question, CancellationToken token)
        {
            Deliveries++;
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(ExternalWorkUpdateKind.ResultReady, "Your result is ready for review", "approve it or request changes")]
    [InlineData(ExternalWorkUpdateKind.Completed, "Work completed", "result has been approved")]
    [InlineData(ExternalWorkUpdateKind.Failed, "encountered an error", "will not retry automatically")]
    [InlineData(ExternalWorkUpdateKind.Uncertain, "needs your attention", "Open Work")]
    [InlineData(ExternalWorkUpdateKind.CleanupFailed, "cleanup error", "Open Work")]
    [InlineData(ExternalWorkUpdateKind.Cancelled, "Goblin update", "Open Work")]
    public async Task WorkUpdatesUseOriginalThreadAndOnlyMarkSuccessfulPosts(ExternalWorkUpdateKind kind, string heading, string action)
    {
        string directory = Path.Combine(RepositoryDirectory(), ".artifacts", "slack-work-notifications", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var conversations = new Updates([new(1, 2, "D123", "123.4", new(9, kind, "Result <@U456> & detail"))]);
            bool fail = true;
            string? posted = null;
            using var api = new SlackApi(new Handler(async request =>
            {
                posted = System.Net.WebUtility.UrlDecode(await request.Content!.ReadAsStringAsync());
                return Response(fail ? "{\"ok\":false}" : "{\"ok\":true}");
            }));
            var store = new SlackCredentialStore(directory);
            await store.SaveAsync(Credentials, default);
            using var connection = new SlackConnection(api, store, conversations, "http://localhost:8788");
            conversations.Current = false;
            await connection.DeliverUpdatesAsync(Credentials, default);
            Assert.Null(posted);
            conversations.Current = true;
            await connection.DeliverUpdatesAsync(Credentials, default);
            Assert.Empty(conversations.Delivered);
            fail = false;
            await connection.DeliverUpdatesAsync(Credentials, default);
            Assert.Single(conversations.Delivered);
            Assert.Contains("channel=D123&thread_ts=123.4&", posted);
            Assert.Contains("Result &lt;@U456&gt; &amp; detail", posted);
            Assert.Contains(heading, posted);
            Assert.Contains(action, posted);
            Assert.Contains("http://localhost:8788/work?item=2", posted);
            Assert.Contains("unfurl_links=false&unfurl_media=false&parse=none", posted);
            await connection.DeliverUpdatesAsync(Credentials, default);
            Assert.Single(conversations.Delivered);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task FailedChannelDoesNotBlockOtherUpdatesAndDisconnectStopsDelivery()
    {
        string directory = Path.Combine(RepositoryDirectory(), ".artifacts", "slack-work-notifications", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var conversations = new Updates([
                new(1, 2, "C123", "123.4", new(9, ExternalWorkUpdateKind.Failed, "Error")),
                new(2, 3, "D123", "456.7", new(10, ExternalWorkUpdateKind.ResultReady, new string('x', 2999) + "🌱hidden"))
            ]);
            string? posted = null;
            using var api = new SlackApi(new Handler(async request =>
            {
                posted = System.Net.WebUtility.UrlDecode(await request.Content!.ReadAsStringAsync());
                return Response(posted!.Contains("channel=C123", StringComparison.Ordinal) ? "{\"ok\":false}" : "{\"ok\":true}");
            }));
            var store = new SlackCredentialStore(directory);
            await store.SaveAsync(Credentials, default);
            using var connection = new SlackConnection(api, store, conversations, "http://localhost:8788");
            await connection.DeliverUpdatesAsync(Credentials, default);
            Assert.Equal(2, Assert.Single(conversations.Delivered));
            Assert.Contains("…", posted);
            Assert.DoesNotContain("hidden", posted);
            Assert.DoesNotContain("�", posted);
            await connection.DisconnectAsync(default);
            await connection.DeliverUpdatesAsync(Credentials, default);
            Assert.Single(conversations.Delivered);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static string RepositoryDirectory()
    {
        string? directory = Environment.CurrentDirectory;
        while (directory is not null && !Directory.Exists(Path.Combine(directory, ".git"))) directory = Path.GetDirectoryName(directory);
        return directory ?? throw new InvalidOperationException("Repository directory not found");
    }

    private sealed class Updates : IExternalConversations
    {
        private readonly ExternalWorkUpdate[] _updates;
        public Updates(ExternalWorkUpdate[] updates) => _updates = updates;
        public System.Collections.Generic.List<long> Delivered { get; } = [];
        public bool Current { get; set; } = true;
        public Task AcceptAsync(ExternalMessage message, CancellationToken token) => throw new InvalidOperationException();
        public Task<ExternalReply?> ProcessNextAsync(ExternalInstallation installation, CancellationToken token) => throw new InvalidOperationException();
        public Task<ExternalQuestion?> NextQuestionAsync(ExternalInstallation installation, CancellationToken token) => throw new InvalidOperationException();
        public Task QuestionSentAsync(ExternalInstallation installation, ExternalQuestion question, CancellationToken token) => throw new InvalidOperationException();
        public Task<ExternalWorkUpdate[]> PendingUpdatesAsync(ExternalInstallation installation, CancellationToken token) =>
            Task.FromResult(System.Array.FindAll(_updates, update => !Delivered.Contains(update.ConversationId)));
        public Task<bool> UpdateIsCurrentAsync(ExternalInstallation installation, ExternalWorkUpdate update, CancellationToken token) => Task.FromResult(Current);
        public Task UpdateSentAsync(ExternalInstallation installation, ExternalWorkUpdate update, CancellationToken token)
        {
            Delivered.Add(update.ConversationId);
            return Task.CompletedTask;
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _send;

        public Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) => _send = send;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _send(request);
    }
}
