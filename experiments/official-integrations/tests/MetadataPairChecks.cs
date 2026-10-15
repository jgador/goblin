using Goblin.Mcp.Prototype;

internal static class MetadataPairChecks
{
    internal static async Task<int> RunAsync()
    {
        string directory = Path.GetFullPath(Path.Combine(".artifacts", "official-integrations", "metadata-pair", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        int checks = 0;
        try
        {
            string resourcePath = Path.Combine(directory, "resource.json"), discoveryPath = Path.Combine(directory, "discovery.json");
            string resource = ProtectedResourceMetadataChecks.Sample(), discovery = OAuthDiscoveryChecks.Sample();
            string[] command = ["--review-metadata-pair", ProtectedResourceMetadataChecks.Resource, ProtectedResourceMetadataChecks.Issuer, resourcePath, discoveryPath];
            async Task Check(string name, string[] args, bool accepted, bool advertised = false, CancellationToken token = default)
            {
                using var output = new StringWriter();
                using var error = new StringWriter();
                int exit = await OfflineOAuthMetadataCommand.RunAsync(args, output, error, token);
                Require(exit == (accepted ? 0 : 1) && output.ToString() == (accepted ? Expected(advertised) : "") &&
                    error.ToString() == (accepted ? "" : OfflineOAuthMetadataCommand.Failure + Environment.NewLine));
                checks++;
                Console.WriteLine("PASS " + name);
            }
            async Task Files(string first, string second)
            {
                await File.WriteAllTextAsync(resourcePath, first);
                await File.WriteAllTextAsync(discoveryPath, second);
            }

            await Files(resource, discovery);
            await Check("metadata pair accepts both matching files with fixed advisory only", command, true);
            await Check("metadata pair rejects missing discovery path argument", command[..4], false);
            await Check("metadata pair rejects extra argument", [.. command, "PRIVATE"], false);
            await Check("metadata pair rejects reversed file arguments", [.. command[..3], discoveryPath, resourcePath], false);
            await Check("metadata pair rejects resource-only file reused as discovery", [.. command[..4], resourcePath], false);
            await Check("metadata pair hides missing second path without partial success", [.. command[..4], discoveryPath + "PRIVATE"], false);
            await Check("metadata pair rejects unsafe trusted issuer before reading", [command[0], command[1], "https://user:PRIVATE@identity.customer.example", resourcePath, discoveryPath], false);

            foreach (string second in new[] { "{ PRIVATE-DISCOVERY", "null", OAuthDiscoveryChecks.Sample(issuer: ProtectedResourceMetadataChecks.Issuer + "/"),
                discovery.Insert(1, "\"issuer\":\"PRIVATE\","), OAuthDiscoveryChecks.Sample(challenge: ["plain"]),
                OAuthDiscoveryChecks.Sample(keys: "https://PRIVATE.example/keys"), discovery.PadRight(16385) })
            {
                await Files(resource, second);
                await Check("good resource plus rejected discovery emits no partial success", command, false);
            }
            foreach (string first in new[] { "{ PRIVATE-RESOURCE", ProtectedResourceMetadataChecks.Sample(resource: ProtectedResourceMetadataChecks.Resource + "/"),
                ProtectedResourceMetadataChecks.Sample(servers: ["https://PRIVATE.example/"]), resource.Insert(1, "\"resource\":\"PRIVATE\",!") })
            {
                await Files(first, discovery);
                await Check("bad resource plus good discovery emits no success", command, false);
            }
            await Files(resource, discovery);
            await File.WriteAllBytesAsync(discoveryPath, [0xff]);
            await Check("pair second file reuses strict UTF-8 decoder", command, false);
            if (!OperatingSystem.IsWindows())
            {
                string link = Path.Combine(directory, "discovery-link.json");
                File.CreateSymbolicLink(link, discoveryPath);
                await Check("pair second file refuses a leaf symlink", [.. command[..4], link], false);
            }
            await Files(resource.PadRight(16384), discovery.PadRight(16384));
            await Check("pair allows each exact byte bound without combining authority", command, true);
            await Files(resource, discovery.Insert(1, "\"authorization_response_iss_parameter_supported\":true,"));
            await Check("pair issuer advertisement remains unverified after both reviews", command, true, true);
            await Files(resource, "{ PRIVATE-LATER-FAILURE");
            await Check("pair rejection cannot reuse a previous successful summary", command, false);
            await Files(resource, discovery);
            await Check("fresh valid pair does not retain prior true advertisement or failure", command, true);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Check("cancelled pair emits no optimistic success", command, false, token: cancelled.Token);

            using (var output = new StringWriter())
            using (var error = new StringWriter())
            {
                int reads = 0;
                CancellationToken firstToken = default;
                int exit = await OfflineOAuthMetadataCommand.RunWithReaderAsync(command, output, error, default, (path, token) =>
                {
                    Require(output.ToString() == "" && token.CanBeCanceled && !token.IsCancellationRequested);
                    reads++;
                    if (reads == 1) { Require(path == resourcePath); firstToken = token; return Task.FromResult(resource); }
                    Require(reads == 2 && path == discoveryPath && token == firstToken);
                    return Task.FromResult(discovery);
                });
                Require(exit == 0 && reads == 2 && output.ToString() == Expected(false) && error.ToString() == "");
                checks++;
                Console.WriteLine("PASS pair reads in role order with one shared deadline token and no early output");
            }
            foreach (int cancelAfter in new[] { 1, 2 })
            using (var stop = new CancellationTokenSource())
            using (var output = new StringWriter())
            using (var error = new StringWriter())
            {
                int reads = 0;
                int exit = await OfflineOAuthMetadataCommand.RunWithReaderAsync(command, output, error, stop.Token, (_, _) =>
                {
                    reads++;
                    if (reads == cancelAfter) stop.Cancel();
                    return Task.FromResult(reads == 1 ? resource : discovery);
                });
                Require(exit == 1 && reads == cancelAfter && output.ToString() == "" && error.ToString() == OfflineOAuthMetadataCommand.Failure + Environment.NewLine);
                checks++;
                Console.WriteLine(cancelAfter == 1
                    ? "PASS cancellation after first review prevents second read and any success"
                    : "PASS cancellation after second review still prevents optimistic success");
            }
            using (var output = new StringWriter())
            using (var error = new StringWriter())
            {
                int reads = 0;
                int exit = await OfflineOAuthMetadataCommand.RunWithReaderAsync(command, output, error, default, (_, _) =>
                {
                    reads++;
                    return Task.FromResult("{ PRIVATE-FIRST-FAILURE");
                });
                Require(exit == 1 && reads == 1 && output.ToString() == "" && error.ToString() == OfflineOAuthMetadataCommand.Failure + Environment.NewLine);
                checks++;
                Console.WriteLine("PASS failed first policy prevents unnecessary discovery read");
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine($"PASS {checks} metadata-pair checks; local synthetic files and friend-only reader observations.");
        return checks;
    }

    internal static string Expected(bool advertised) => OfflineOAuthMetadataCommand.PairAccepted + Environment.NewLine +
        "Authorization-response issuer identification advertised: " + (advertised ? "true" : "false") + " (unverified)." + Environment.NewLine +
        OfflineOAuthMetadataCommand.Unverified + Environment.NewLine;

    private static void Require(bool value)
    {
        if (!value) throw new Exception("Metadata pair contract assertion failed.");
    }
}
