using System.Text;

namespace Goblin.Mcp.Prototype;

// Trusted local operator files only, not a remote upload endpoint or OAuth client.
internal static class OfflineOAuthMetadataCommand
{
    internal const string Failure = "Offline metadata review rejected. Use --review-oauth-metadata <expected-https-issuer> <local-json-file> with the documented narrow policy.";
    internal const string Accepted = "Offline metadata policy: matched (advisory only).";
    internal const string Unverified = "Not verified: discovery trust, client registration, callbacks, OAuth enforcement, token claims, platform eligibility or Work access.";

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken token)
    {
        try
        {
            if (args.Length != 3 || args[0] != "--review-oauth-metadata") throw new InvalidOperationException();
            var policy = new CustomerOAuthDiscoveryPreflight(args[1]);
            token.ThrowIfCancellationRequested();
            var file = new FileInfo(args[2]);
            if (!file.Exists || (file.Attributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0 ||
                file.Length is <= 0 or > CustomerOAuthDiscoveryPreflight.MaximumMetadataBytes)
                throw new InvalidOperationException();

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] bytes = new byte[CustomerOAuthDiscoveryPreflight.MaximumMetadataBytes + 1];
            int length = 0;
            int read;
            while ((read = await stream.ReadAsync(bytes.AsMemory(length), deadline.Token)) > 0)
            {
                length += read;
                if (length > CustomerOAuthDiscoveryPreflight.MaximumMetadataBytes) throw new InvalidOperationException();
            }
            OAuthDiscoverySummary summary = policy.Review(new UTF8Encoding(false, true).GetString(bytes, 0, length));
            deadline.Token.ThrowIfCancellationRequested();
            await output.WriteLineAsync(Accepted);
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
}
