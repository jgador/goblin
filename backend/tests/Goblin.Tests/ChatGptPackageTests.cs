using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using Goblin.Integrations.ChatGpt;
using Goblin.Web;
using Xunit;

namespace Goblin.Tests;

public sealed class ChatGptPackageTests
{
    [Fact]
    public void PackageCanBeExtractedWithCompleteMetadataOAuthAndReviewCases()
    {
        var configuration = new ChatGptConfiguration("https://goblin.example.test");
        ChatGptPackageView package = ChatGptPackage.Create(configuration, Request());
        using var bytes = new MemoryStream(Convert.FromBase64String(package.ArchiveBase64));
        using var archive = new ZipArchive(bytes);
        Assert.Equal(new[] { "assets/icon.svg", "mcp.json", "plugin.json" }, archive.Entries.Select(entry => entry.FullName).Order());
        Assert.Equal(package.PluginJson, Read(archive, "plugin.json"));
        Assert.Equal(package.McpJson, Read(archive, "mcp.json"));
        using JsonDocument plugin = JsonDocument.Parse(package.PluginJson);
        JsonElement metadata = plugin.RootElement.GetProperty("extensions").GetProperty("com.openai");
        Assert.Equal("Example Studio", metadata.GetProperty("interface").GetProperty("developerName").GetString());
        Assert.Equal("https://example.test/privacy", metadata.GetProperty("interface").GetProperty("privacyPolicyURL").GetString());
        JsonElement cases = metadata.GetProperty("review").GetProperty("test_cases");
        Assert.Equal(5, cases.GetProperty("positive").GetArrayLength());
        Assert.Equal(3, cases.GetProperty("negative").GetArrayLength());
        Assert.Contains("9007199254740993", cases.GetProperty("positive")[1].GetProperty("prompt").GetString());
        Assert.Contains("9007199254740995", cases.GetProperty("negative")[0].GetProperty("prompt").GetString());
        using JsonDocument mcp = JsonDocument.Parse(package.McpJson);
        JsonElement server = mcp.RootElement.GetProperty("mcpServers").GetProperty("goblin");
        Assert.Equal(configuration.Resource, server.GetProperty("url").GetString());
        JsonElement auth = server.GetProperty("extensions").GetProperty("com.openai").GetProperty("auth");
        Assert.Equal("oauth", auth.GetProperty("type").GetString());
        Assert.Equal("provided", auth.GetProperty("client").GetProperty("mode").GetString());
        Assert.Equal(ChatGptConfiguration.ClientId, auth.GetProperty("client").GetProperty("clientId").GetString());
        Assert.Equal("client_secret_basic", auth.GetProperty("client").GetProperty("tokenEndpointAuthMethod").GetString());
        Assert.False(auth.GetProperty("client").TryGetProperty("clientSecret", out _));
        Assert.Equal(new[] { "openid", "offline_access", "goblin.work.read" },
            auth.GetProperty("baseScopes").EnumerateArray().Select(scope => scope.GetString()));
        Assert.False(metadata.GetProperty("review").TryGetProperty("test_credentials", out _));
        XDocument logo = XDocument.Parse(Read(archive, "assets/icon.svg"));
        Assert.Equal(logo.Root!.Attribute("width")!.Value, logo.Root.Attribute("height")!.Value);
        string[] viewBox = logo.Root.Attribute("viewBox")!.Value.Split(' ');
        Assert.Equal(viewBox[2], viewBox[3]);
    }

    [Theory]
    [InlineData("http://example.test/privacy", "9007199254740993", "goblin-demo")]
    [InlineData("https://reader:sample@example.test/privacy", "9007199254740993", "goblin-demo")]
    [InlineData("https://example.test/privacy", "1e3", "goblin-demo")]
    [InlineData("https://example.test/privacy", "9007199254740994", "goblin-demo")]
    [InlineData("https://example.test/privacy", "9007199254740993", "../goblin")]
    public void InvalidListingAndDemoInputsCannotProduceAPackage(string privacy, string workId, string packageId)
    {
        var configuration = new ChatGptConfiguration("https://goblin.example.test");
        Assert.Throws<PublicError>(() => ChatGptPackage.Create(configuration, Request(privacy, workId, packageId)));
    }

    private static ChatGptPackageRequest Request(string privacy = "https://example.test/privacy", string workId = "9007199254740993", string packageId = "goblin-demo") => new()
    {
        PackageId = packageId,
        Name = "Goblin Demo",
        Publisher = "Example Studio",
        Subtitle = "Read shared Work",
        Description = "Read shared Work and approved results.",
        WebsiteUrl = "https://example.test",
        SupportUrl = "https://example.test/support",
        PrivacyUrl = privacy,
        TermsUrl = "https://example.test/terms",
        VideoUrl = "https://example.test/demo",
        StatusWorkId = workId,
        ResultWorkId = "9007199254740994",
        PrivateWorkId = "9007199254740995"
    };

    private static string Read(ZipArchive archive, string name)
    {
        using var reader = new StreamReader(archive.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }
}
