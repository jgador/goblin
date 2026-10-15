using System.Diagnostics;
using System.Security.Cryptography;
using Goblin.Mcp.Prototype;
using ModelContextProtocol.Client;

internal static class OfflineOAuthCommandChecks
{
    private const string Issuer = "https://identity.customer.example/realms/goblin";

    internal static async Task<int> RunAsync(string? dotnet, string? prototype)
    {
        string directory = Path.GetFullPath(Path.Combine(".artifacts", "official-integrations", "oauth-command", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        int checks = 0;
        try
        {
            string path = Path.Combine(directory, "metadata.json");
            string valid = OAuthDiscoveryChecks.Sample().Insert(1, "\"ignored\":\"PRIVATE-METADATA-MARKER\",");
            string[] command = ["--review-oauth-metadata", Issuer, path];

            async Task Check(string name, string[] arguments, bool accepted, bool advertised = false, CancellationToken token = default)
            {
                using var output = new StringWriter();
                using var error = new StringWriter();
                int exit = await OfflineOAuthMetadataCommand.RunAsync(arguments, output, error, token);
                Require(exit == (accepted ? 0 : 1));
                Require(output.ToString() == (accepted ? Expected(advertised) : ""));
                Require(error.ToString() == (accepted ? "" : OfflineOAuthMetadataCommand.Failure + Environment.NewLine));
                checks++;
                Console.WriteLine("PASS " + name);
            }

            await File.WriteAllTextAsync(path, valid);
            await Check("offline command reports only advisory policy with issuer advertisement absent", command, true);
            await File.WriteAllTextAsync(path, valid.Insert(1, "\"authorization_response_iss_parameter_supported\":true,"));
            await Check("offline command true issuer advertisement never claims live readiness", command, true, true);
            await Check("offline command rejects missing arguments", [], false);
            await Check("offline command rejects unknown mode without starting MCP", ["--unknown", Issuer, path], false);
            await Check("offline command rejects extra arguments", [.. command, "PRIVATE-EXTRA"], false);
            await Check("offline command sanitizes invalid pinned issuer", [command[0], "https://user:PRIVATE@identity.customer.example", path], false);
            await Check("offline command hides missing local path", [command[0], Issuer, path + "PRIVATE-MISSING"], false);
            await Check("offline command refuses directory input", [command[0], Issuer, directory], false);

            await File.WriteAllTextAsync(path, "{ PRIVATE-METADATA-MARKER");
            await Check("offline command hides malformed metadata", command, false);
            await File.WriteAllTextAsync(path, valid.Insert(1, "\"issuer\":\"PRIVATE-DUPLICATE\","));
            await Check("offline command preserves duplicate-property rejection", command, false);
            await File.WriteAllTextAsync(path, valid.Replace("S256", "plain", StringComparison.Ordinal));
            await Check("offline command preserves S256 policy rejection", command, false);
            await File.WriteAllBytesAsync(path, [0xff]);
            await Check("offline command rejects invalid UTF-8 without decoder details", command, false);
            await File.WriteAllTextAsync(path, valid.PadRight(CustomerOAuthDiscoveryPreflight.MaximumMetadataBytes));
            await Check("offline command accepts exact byte limit", command, true);
            await File.WriteAllTextAsync(path, valid.PadRight(CustomerOAuthDiscoveryPreflight.MaximumMetadataBytes + 1));
            await Check("offline command rejects one byte beyond limit", command, false);
            await File.WriteAllTextAsync(path, "");
            await Check("offline command rejects empty files", command, false);

            await File.WriteAllTextAsync(path, valid);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Check("offline command cancellation has fixed failure and no success", command, false, token: cancelled.Token);
            if (!OperatingSystem.IsWindows())
            {
                string link = Path.Combine(directory, "metadata-link.json");
                File.CreateSymbolicLink(link, path);
                await Check("offline command refuses leaf symlink", [command[0], Issuer, link], false);
            }
            else Console.WriteLine("SKIP leaf symlink fixture: Windows privilege not assumed.");

            if (dotnet is not null && prototype is not null)
            {
                await ProcessCheck(command, true);
                await ProcessCheck(["--unknown", Issuer, path], false);
                string resourcePath = Path.Combine(directory, "resource.json");
                string[] resourceCommand = ["--review-resource-metadata", ProtectedResourceMetadataChecks.Resource, Issuer, resourcePath];
                await File.WriteAllTextAsync(resourcePath, ProtectedResourceMetadataChecks.Sample());
                await ProcessCheck(resourceCommand, true, resource: true);
                await File.WriteAllTextAsync(resourcePath, ProtectedResourceMetadataChecks.Sample(servers: ["https://PRIVATE.example/"]));
                await ProcessCheck(resourceCommand, false, resource: true);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                string session = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                var transport = new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name = "Offline command default-mode regression",
                    Command = dotnet,
                    Arguments = [prototype],
                    InheritEnvironmentVariables = false,
                    EnvironmentVariables = new Dictionary<string, string?>
                    {
                        [PrototypeOptions.OriginVariable] = "https://customer.example/",
                        [PrototypeOptions.SessionVariable] = session,
                        [PrototypeOptions.WorkIdsVariable] = "42"
                    }
                });
                await using McpClient client = await McpClient.CreateAsync(transport, cancellationToken: deadline.Token);
                var tools = await client.ListToolsAsync(cancellationToken: deadline.Token);
                Require(tools.Count == 1 && tools[0].Name == "goblin_get_work_status");
                var denied = await client.CallToolAsync("goblin_get_work_status",
                    new Dictionary<string, object?> { ["workId"] = "43" }, cancellationToken: deadline.Token);
                Require(denied.IsError == true);
                checks++;
                Console.WriteLine("PASS no-argument SDK stdio discovery and local grant denial remain intact");
            }
            else Console.WriteLine("SKIP offline command child processes: supply dotnet and prototype DLL.");

            async Task ProcessCheck(string[] arguments, bool accepted, bool resource = false)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var start = new ProcessStartInfo(dotnet!)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false
                };
                start.Environment.Clear();
                start.ArgumentList.Add(prototype!);
                foreach (string argument in arguments) start.ArgumentList.Add(argument);
                using var process = new Process { StartInfo = start };
                Require(process.Start());
                process.StandardInput.Close();
                try
                {
                    Task<string> output = process.StandardOutput.ReadToEndAsync(deadline.Token);
                    Task<string> error = process.StandardError.ReadToEndAsync(deadline.Token);
                    await process.WaitForExitAsync(deadline.Token);
                    Require(process.ExitCode == (accepted ? 0 : 1));
                    Require(await output == (accepted ? resource ? ProtectedResourceMetadataChecks.Expected() : Expected(false) : ""));
                    Require(await error == (accepted ? "" : OfflineOAuthMetadataCommand.Failure + Environment.NewLine));
                    checks++;
                    Console.WriteLine(resource ? "PASS actual protected-resource command has fixed output and no inherited environment" : accepted
                        ? "PASS actual offline command succeeds with no inherited environment/session"
                        : "PASS actual unknown command fails before session configuration or MCP startup");
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await process.WaitForExitAsync(cleanup.Token);
                    }
                }
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine($"PASS {checks} offline command checks; synthetic local files, no live OAuth.");
        return checks;
    }

    private static string Expected(bool advertised) => OfflineOAuthMetadataCommand.Accepted + Environment.NewLine +
        "Authorization-response issuer identification advertised: " + (advertised ? "true" : "false") + " (unverified)." + Environment.NewLine +
        OfflineOAuthMetadataCommand.Unverified + Environment.NewLine;

    private static void Require(bool value)
    {
        if (!value) throw new Exception("Offline OAuth command contract assertion failed.");
    }
}
