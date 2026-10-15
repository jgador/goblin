using System.Text;

namespace Goblin.Mcp.Prototype;

// Trusted local operator files only, not a remote upload endpoint or OAuth client.
internal static class OfflineOAuthMetadataCommand
{
    internal const string Failure = "Offline metadata review rejected. Use a documented metadata command with explicitly pinned settings and a bounded local JSON file.";
    internal const string Accepted = "Offline metadata policy: matched (advisory only).";
    internal const string ResourceAccepted = "Offline protected-resource policy: matched (advisory only).";
    internal const string PairAccepted = "Offline metadata pair: matched (advisory only).";
    internal const string Unverified = "Not verified: discovery trust, client registration, callbacks, OAuth enforcement, token claims, platform eligibility or Work access.";

    internal static Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken token) =>
        RunWithReaderAsync(args, output, error, token, ReadFileAsync);

    // Friend tests can observe read order/cancellation; the entry point always
    // supplies the bounded local-file reader, never a network loader.
    internal static async Task<int> RunWithReaderAsync(string[] args, TextWriter output, TextWriter error, CancellationToken token,
        Func<string, CancellationToken, Task<string>> read)
    {
        try
        {
            bool discovery = args.Length == 3 && args[0] == "--review-oauth-metadata";
            bool resource = args.Length == 4 && args[0] == "--review-resource-metadata";
            bool pair = args.Length == 5 && args[0] == "--review-metadata-pair";
            if (!discovery && !resource && !pair) throw new InvalidOperationException();
            CustomerOAuthDiscoveryPreflight? policy = discovery ? new(args[1]) : pair ? new(args[2]) : null;
            CustomerProtectedResourcePreflight? resourcePolicy = resource || pair ? new(args[1], args[2]) : null;
            token.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            OAuthDiscoverySummary? summary = null;
            if (resourcePolicy is not null)
                resourcePolicy.Review(await read(args[3], deadline.Token));
            deadline.Token.ThrowIfCancellationRequested();
            if (policy is not null)
                summary = policy.Review(await read(args[^1], deadline.Token));
            deadline.Token.ThrowIfCancellationRequested();
            // In pair mode nothing is emitted until both independent reviews pass.
            await output.WriteLineAsync(pair ? PairAccepted : resource ? ResourceAccepted : Accepted);
            if (summary is not null)
                await output.WriteLineAsync(summary.AdvertisesAuthorizationResponseIssuerIdentification
                    ? "Authorization-response issuer identification advertised: true (unverified)."
                    : "Authorization-response issuer identification advertised: false (unverified).");
            await output.WriteLineAsync(Unverified);
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or NotSupportedException or OperationCanceledException or System.Security.SecurityException)
        {
            await error.WriteLineAsync(Failure);
            return 1;
        }
    }

    private static async Task<string> ReadFileAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var file = new FileInfo(path);
        if (!file.Exists || (file.Attributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0 ||
            file.Length is <= 0 or > CustomerOAuthDiscoveryPreflight.MaximumMetadataBytes)
            throw new InvalidOperationException();
        await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] bytes = new byte[CustomerOAuthDiscoveryPreflight.MaximumMetadataBytes + 1];
        int length = 0;
        int read;
        while ((read = await stream.ReadAsync(bytes.AsMemory(length), token)) > 0)
        {
            length += read;
            if (length > CustomerOAuthDiscoveryPreflight.MaximumMetadataBytes) throw new InvalidOperationException();
        }
        token.ThrowIfCancellationRequested();
        return new UTF8Encoding(false, true).GetString(bytes, 0, length);
    }
}
