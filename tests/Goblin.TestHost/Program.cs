using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Goblin.Protocol;
using Goblin.Web;
using Goblin.Web.Codex;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

// Test-only executable; never included in the production publish/image.
FixtureOptions config = JsonSerializer.Deserialize<FixtureOptions>(args[0], new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
await using WebApplication app = await GoblinApplication.CreateAsync(new()
{
    DataDirectory = config.DataDir,
    PasswordHashFile = config.PasswordHashFile,
    PublicOrigin = config.PublicOrigin,
    AllowInsecureHttp = config.AllowInsecureHttp,
    ListenUrl = config.ListenUrl,
    AssetDirectory = Path.Combine(config.Root, "dist/public"),
    RecoverRuntime = false,
    PromptTimeout = TimeSpan.FromMilliseconds(config.PromptTimeoutMs),
    ConfigureCodex = options => options with
    {
        Command = config.RealCodex ? config.Command ?? options.Command : config.Node,
        Arguments = config.RealCodex ? [] : [Path.Combine(config.Root, "test/fixtures/fake-codex.mjs"), config.Scenario],
        RequestTimeout = TimeSpan.FromMilliseconds(config.TimeoutMs),
        Environment = new Dictionary<string, string?>
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
            ["OPENAI_API_KEY"] = "must-not-inherit",
            ["CODEX_API_KEY"] = "must-not-inherit",
            ["STACKIFY_AZURE_OPENAI_API_KEY"] = "must-not-inherit",
            ["CODEX_HOME"] = "/must-not-use",
            ["GOBLIN_SECRET"] = "must-not-inherit"
        }
    },
    VerifyApiKey = async key =>
    {
        if (config.Verification == "invalid" || key.Contains("invalid"))
            throw new PublicError("invalid_api_key", "OpenAI rejected this API key. Check the key and try again.");
        await File.WriteAllTextAsync(Path.Combine(config.DataDir, "verified-test-key"), key);
        return config.Verification;
    }
});
if (config.RealCodex)
{
    CodexClient codex = app.Services.GetRequiredService<CodexClient>();
    Authentication auth = app.Services.GetRequiredService<Authentication>();
    AuthenticationState before = await auth.StatusAsync();
    if (!before.RuntimeReady) throw new Exception("Runtime is not ready.");
    if (config.Scenario == "storage")
    {
        if (before.Account is not null) throw new Exception("Expected isolated empty storage.");
        await codex.RequestAsync<LoginAccountParams, LoginAccountResponse>("account/login/start",
            new ApiKeyLoginAccountParams { ApiKey = "sk-goblin-storage-check-this-is-not-a-real-api-key" });
        if ((await auth.StatusAsync()).Account is not ApiKeyAccountView) throw new Exception("Key was not saved.");
        if (!OperatingSystem.IsWindows() && File.GetUnixFileMode(Path.Combine(config.DataDir, "codex/auth.json")) != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
            throw new Exception("Credential file permissions are incorrect.");
    }
    else
    {
        if (before.Account is not ApiKeyAccountView) throw new Exception("Saved credentials did not survive restart.");
        if ((await auth.LogoutAsync()).Account is not null) throw new Exception("Logout did not clear account.");
    }
    await codex.DisposeAsync();
    Console.WriteLine("Codex storage check passed.");
    return;
}
await app.StartAsync();
Console.WriteLine(JsonSerializer.Serialize(new { url = app.Urls.Single(), tokenFile = app.Services.GetRequiredService<Workspace>().TokenFile }));
// EOF or a newline from the test harness gracefully stops both server and child process.
await Console.In.ReadLineAsync();
await app.Services.GetRequiredService<CodexClient>().DisposeAsync();
await app.StopAsync();

internal sealed record FixtureOptions
{
    public required string Root { get; init; }
    public required string DataDir { get; init; }
    public string? PasswordHashFile { get; init; }
    public string PublicOrigin { get; init; } = "http://localhost:8787";
    public bool AllowInsecureHttp { get; init; }
    public string ListenUrl { get; init; } = "http://127.0.0.1:0";
    public string Node { get; init; } = "node";
    public string Scenario { get; init; } = "manual";
    public string Verification { get; init; } = "accepted";
    public int TimeoutMs { get; init; } = 2000;
    public int PromptTimeoutMs { get; init; } = 90000;
    public bool RealCodex { get; init; }
    public string? Command { get; init; }
}
