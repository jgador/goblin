using System.Text.Json;
using System.Security.Cryptography;
using Goblin.Mcp.Http.Prototype;
using Goblin.Mcp.Prototype;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

internal static class ProtectedResourceMetadataChecks
{
    internal const string Resource = "https://goblin.customer.example/mcp";
    internal const string Issuer = "https://identity.customer.example/realms/goblin";
    private const string Failure = "Protected-resource metadata does not satisfy the configured preflight policy.";

    internal static async Task<int> RunAsync()
    {
        var policy = new CustomerProtectedResourcePreflight(Resource, Issuer);
        int checks = 0;
        void Accept(string name, string metadata)
        {
            policy.Review(metadata);
            checks++;
            Console.WriteLine("PASS " + name);
        }
        void Deny(string name, Action review)
        {
            try { review(); }
            catch (InvalidOperationException error)
            {
                Require(error.Message == Failure && error.InnerException is null);
                checks++;
                Console.WriteLine("PASS " + name);
                return;
            }
            throw new Exception("Protected-resource denial expected.");
        }

        string valid = Sample();
        Accept("protected-resource exact configured profile passes without network", valid);
        Accept("protected-resource exact byte limit passes", valid.PadRight(CustomerOAuthDiscoveryPreflight.MaximumMetadataBytes));
        Accept("ignored optional metadata is never used for trust or fetched", valid.Insert(1, "\"resource_documentation\":\"https://PRIVATE.example/secret\","));
        foreach (string resource in new[] { Resource + "/", Resource.Replace("customer", "other", StringComparison.Ordinal),
            Resource.Replace("https:", "http:", StringComparison.Ordinal), Resource.Replace("goblin", "GOBLIN", StringComparison.Ordinal),
            "https://goblin.customer.example:443/mcp", "https://goblin.customer.example/a/../mcp" })
            Deny("protected-resource identifier is exact without normalization", () => policy.Review(Sample(resource: resource)));
        foreach (string[] servers in new string[][] { [], [Issuer + "/"], ["https://PRIVATE.example/"], [Issuer, "https://PRIVATE.example/"], [Issuer, Issuer], [null!] })
            Deny("protected-resource requires exactly the one pinned issuer", () => policy.Review(Sample(servers: servers)));
        foreach (string[] scopes in new string[][] { [], [ConnectorReadBoundary.ReadScope + ".extra"], ["openid"],
            [ConnectorReadBoundary.ReadScope, "openid"], [ConnectorReadBoundary.ReadScope, ConnectorReadBoundary.ReadScope], [null!] })
            Deny("protected-resource requires the exact sole Work scope", () => policy.Review(Sample(scopes: scopes)));
        foreach (string field in new[] { "resource", "authorization_servers", "scopes_supported" })
        {
            Deny("protected-resource duplicate required field is sanitized", () => policy.Review(valid.Insert(1, "\"" + field + "\":null,")));
            Deny("protected-resource wrong-shaped required field is sanitized", () => policy.Review("{\"" + field + "\":{},\"ignored\":\"PRIVATE\"}"));
        }
        foreach (string json in new[] { "{}", "null", "{ PRIVATE", "[]", "", new string('x', 16385),
            valid.Insert(1, "\"ignored\":" + new string('[', 9) + "0" + new string(']', 9) + ",") })
            Deny("protected-resource missing malformed oversized or deep JSON is rejected", () => policy.Review(json));
        foreach (string resource in new[] { "http://goblin.customer.example/mcp", "https://user:PRIVATE@goblin.customer.example/mcp",
            Resource + "?PRIVATE", Resource + "#PRIVATE", Resource + "\n", "https://goblin.customer.example/", "https://goblin.customer.example:443/mcp" })
            Deny("unsafe or noncanonical expected resource is rejected", () => _ = new CustomerProtectedResourcePreflight(resource, Issuer));
        foreach (string issuer in new[] { "http://identity.customer.example/", Issuer + "?PRIVATE", Issuer + "\\PRIVATE" })
            Deny("unsafe expected issuer is rejected before metadata", () => _ = new CustomerProtectedResourcePreflight(Resource, issuer));
        Accept("a rejected document cannot replace the pinned resource or issuer", valid);

        string directory = Path.GetFullPath(Path.Combine(".artifacts", "official-integrations", "resource-command", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "metadata.json");
            string[] command = ["--review-resource-metadata", Resource, Issuer, path];
            async Task Command(string name, string[] arguments, bool accepted, CancellationToken token = default)
            {
                using var output = new StringWriter();
                using var error = new StringWriter();
                int exit = await OfflineOAuthMetadataCommand.RunAsync(arguments, output, error, token);
                Require(exit == (accepted ? 0 : 1) && output.ToString() == (accepted ? Expected() : "") &&
                    error.ToString() == (accepted ? "" : OfflineOAuthMetadataCommand.Failure + Environment.NewLine));
                checks++;
                Console.WriteLine("PASS " + name);
            }
            await File.WriteAllTextAsync(path, valid);
            await Command("resource command emits fixed advisory output only", command, true);
            await Command("resource command requires both explicit pins and local path", command[..3], false);
            await Command("resource command rejects extra args", [.. command, "PRIVATE"], false);
            await Command("resource command does not trust swapped expected pins", [command[0], Issuer, Resource, path], false);
            await Command("resource command rejects missing file without path disclosure", [command[0], Resource, Issuer, path + "PRIVATE"], false);
            await File.WriteAllTextAsync(path, Sample(servers: [Issuer, "https://PRIVATE.example/"]));
            await Command("resource command denies additional authorization server without echo", command, false);
            await File.WriteAllTextAsync(path, valid.Insert(1, "\"resource\":\"PRIVATE\","));
            await Command("resource command preserves duplicate rejection", command, false);
            await File.WriteAllTextAsync(path, valid.PadRight(16385));
            await Command("resource command reuses byte bound", command, false);
            await File.WriteAllBytesAsync(path, [0xff]);
            await Command("resource command reuses strict UTF-8 decoding", command, false);
            await File.WriteAllTextAsync(path, valid);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Command("resource command cancels without success", command, false, cancelled.Token);
        }
        finally { Directory.Delete(directory, recursive: true); }
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        using (RSA key = RSA.Create(2048))
        {
            var source = new UnusedSource();
            var boundary = new ConnectorReadBoundary(Issuer, Resource, new RsaSecurityKey(key.ExportParameters(false)), source);
            WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.UseTestServer();
            PrivateMcpHttpAdapter.AddServices(builder.Services, boundary);
            await using WebApplication app = builder.Build();
            PrivateMcpHttpAdapter.Map(app, boundary);
            await app.StartAsync(deadline.Token);
            using HttpClient client = app.GetTestClient();
            client.BaseAddress = new Uri(Resource);
            string emitted = await client.GetStringAsync(PrivateMcpHttpAdapter.MetadataPath, deadline.Token);
            policy.Review(emitted);
            Require(source.Calls == 0);
            checks++;
            Console.WriteLine("PASS actual private HTTP metadata matches offline pins without Work reads");
        }
        Console.WriteLine($"PASS {checks} protected-resource metadata checks; synthetic JSON/files only.");
        return checks;
    }

    internal static string Sample(string resource = Resource, string[]? servers = null, string[]? scopes = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["resource"] = resource,
            ["authorization_servers"] = servers ?? [Issuer],
            ["scopes_supported"] = scopes ?? [ConnectorReadBoundary.ReadScope]
        });

    internal static string Expected() => OfflineOAuthMetadataCommand.ResourceAccepted + Environment.NewLine +
        OfflineOAuthMetadataCommand.Unverified + Environment.NewLine;

    private static void Require(bool value)
    {
        if (!value) throw new Exception("Protected-resource metadata contract assertion failed.");
    }

    private sealed class UnusedSource : ICustomerWorkStatusSource
    {
        internal int Calls { get; private set; }

        public Task<WorkStatusResult> ReadAuthorizedAsync(ConnectorIdentity identity, long workId, CancellationToken token)
        {
            Calls++;
            throw new InvalidOperationException("Work must not be read during metadata review.");
        }
    }
}
