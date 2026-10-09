using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Goblin.Integrations.Slack;

// Owns the supply-chain checks for the pinned Slack CLI package. Setup
// orchestration receives only the single verified executable payload.
internal static class SlackCliPackage
{
    private const int MaximumExecutableSize = 150 * 1024 * 1024;

    internal static async Task<byte[]> ExtractExecutableAsync(byte[] archive, string expectedSha256,
        CancellationToken token)
    {
        if (!Convert.ToHexString(SHA256.HashData(archive)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new SlackFailure("The Slack setup helper failed checksum verification.");

        using var gzip = new GZipStream(new MemoryStream(archive), CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        byte[]? executable = null;
        while (await reader.GetNextEntryAsync(cancellationToken: token) is { } entry)
        {
            if (Path.GetFileName(entry.Name) != "slack" ||
                entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) ||
                entry.DataStream is null) continue;
            if (executable is not null || entry.Length > MaximumExecutableSize) throw new SlackFailure();
            using var memory = new MemoryStream();
            await entry.DataStream.CopyToAsync(memory, token);
            executable = memory.ToArray();
        }
        return executable ?? throw new SlackFailure();
    }
}
