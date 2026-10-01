using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Conversations;
using Goblin.Integrations.Slack;
using Xunit;

namespace Goblin.Application.Tests;

public sealed class SlackTests
{
    private static readonly ExternalInstallation Installation = new("installation", "T123", "A123", "U123");

    [Fact]
    public void EventsRequireTheVerifiedWorkspaceAppAndHumanActor()
    {
        var payload = new
        {
            type = "events_api",
            payload = new
            {
                api_app_id = "A123",
                team_id = "T123",
                event_id = "Ev123",
                @event = new { type = "app_mention", user = "U456", channel = "C123", ts = "12345.000001", text = "<@U123> Explain this" }
            }
        };
        JsonElement envelope = JsonSerializer.SerializeToElement(payload);
        ExternalMessage accepted = Assert.IsType<ExternalMessage>(SlackEvents.Parse(envelope, Installation));
        Assert.Equal("Explain this", accepted.Text);
        Assert.Equal(accepted.MessageId, accepted.ThreadId);
        Assert.False(accepted.Direct);
        Assert.Null(SlackEvents.Parse(envelope, Installation with { AppId = "A999" }));
        Assert.Null(SlackEvents.Parse(envelope, Installation with { WorkspaceId = "T999" }));
        Assert.Null(SlackEvents.Parse(envelope, Installation with { BotUserId = "U456" }));
    }

    [Theory]
    [InlineData("channel", "", "U456", null)]
    [InlineData("im", "message_changed", "U456", null)]
    [InlineData("im", "", "U123", null)]
    [InlineData("im", "", "U456", "B123")]
    public void BroadMessagesEditsAndBotMessagesAreIgnored(string channelType, string subtype, string user, string? bot)
    {
        var message = new System.Collections.Generic.Dictionary<string, object?>
        {
            ["type"] = "message",
            ["channel_type"] = channelType,
            ["user"] = user,
            ["channel"] = "D123",
            ["text"] = "hello",
            ["ts"] = "12345.000001"
        };
        if (subtype.Length > 0) message["subtype"] = subtype;
        if (bot is not null) message["bot_id"] = bot;
        JsonElement envelope = JsonSerializer.SerializeToElement(new { type = "events_api", payload = new { api_app_id = "A123", team_id = "T123", event_id = "Ev123", @event = message } });
        Assert.Null(SlackEvents.Parse(envelope, Installation));
    }

    [Fact]
    public async Task CredentialsAreEncryptedDurableAndAuthenticated()
    {
        string directory = TemporaryDirectory();
        try
        {
            var store = new SlackCredentialStore(directory);
            var credentials = new SlackCredentials("installation", "xapp-neutral-test-value", "xoxb-neutral-test-value", "Test workspace", "T123", "A123", "U123");
            await store.SaveAsync(credentials, CancellationToken.None);
            byte[] encrypted = await File.ReadAllBytesAsync(Path.Combine(directory, "credentials"));
            Assert.DoesNotContain("neutral-test-value", System.Text.Encoding.UTF8.GetString(encrypted));
            Assert.Equal(credentials, new SlackCredentialStore(directory).Read());
            Assert.DoesNotContain(credentials.AppToken, credentials.ToString());
            encrypted[^1] ^= 1;
            await File.WriteAllBytesAsync(Path.Combine(directory, "credentials"), encrypted);
            Assert.Throws<SlackFailure>(() => store.Read());
            store.Clear(); Assert.Null(store.Read());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task RestartKeepsTheCreatedAppReferenceButRemovesTemporaryAuthorization()
    {
        string directory = TemporaryDirectory();
        try
        {
            string project = Path.Combine(directory, "setup", "active", "project", ".slack");
            Directory.CreateDirectory(project);
            await File.WriteAllTextAsync(Path.Combine(project, "apps.json"), "{\"apps\":{\"T123\":{\"app_id\":\"A123\"}}}");
            using var api = new SlackApi();
            using var connection = new SlackConnection(api, new(Path.Combine(directory, "credentials")), new NoConversations(), "http://localhost:8787");
            await using var setup = new SlackSetup(Path.Combine(directory, "setup"), api, connection);
            Assert.Equal("A123", setup.View("session").AppId);
            Assert.Equal(SlackSetupStatus.NeedsAttention, setup.View("session").Status);
            Assert.False(Directory.Exists(Path.Combine(directory, "setup", "active")));
            Assert.Throws<SlackFailure>(() => setup.Start("session"));
            await using var restarted = new SlackSetup(Path.Combine(directory, "setup"), api, connection);
            Assert.Equal("A123", restarted.View("session").AppId);
            Assert.Equal(SlackSetupStatus.NeedsAttention, restarted.View("session").Status);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ManifestOnlyReceivesDirectMessagesAndMentions()
    {
        using JsonDocument document = JsonDocument.Parse(Assets.Manifest);
        JsonElement manifest = document.RootElement;
        Assert.True(manifest.GetProperty("settings").GetProperty("socket_mode_enabled").GetBoolean());
        string?[] events = [.. manifest.GetProperty("settings").GetProperty("event_subscriptions").GetProperty("bot_events").EnumerateArray().Select(x => x.GetString())];
        Assert.Equal(new[] { "app_mention", "message.im" }, events);
        Assert.DoesNotContain("request_url", Assets.Manifest);
    }

    private static string TemporaryDirectory()
    {
        string? repo = Environment.CurrentDirectory;
        while (repo is not null && !File.Exists(Path.Combine(repo, "Goblin.slnx")) && !Directory.Exists(Path.Combine(repo, ".git"))) repo = Path.GetDirectoryName(repo);
        if (repo is null) throw new InvalidOperationException("Repository directory not found");
        if (Path.GetFileName(repo) == "backend") repo = Path.GetDirectoryName(repo)!;
        string directory = Path.Combine(repo, ".artifacts", "slack-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); return directory;
    }

    private sealed class NoConversations : IExternalConversations
    {
        public Task AcceptAsync(ExternalMessage message, CancellationToken token) => throw new InvalidOperationException();
        public Task<ExternalReply?> ProcessNextAsync(ExternalInstallation installation, CancellationToken token) => throw new InvalidOperationException();
    }
}
