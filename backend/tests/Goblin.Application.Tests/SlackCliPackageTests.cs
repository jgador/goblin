using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Goblin.Integrations.Slack;
using Xunit;

namespace Goblin.Application.Tests;

public sealed class SlackCliPackageTests
{
    [Fact]
    public async Task ExtractsOnlyTheVerifiedSlackExecutable()
    {
        byte[] expected = "verified executable"u8.ToArray();
        byte[] archive = Archive(("docs/readme", "ignored"u8.ToArray()), ("bin/slack", expected));

        byte[] actual = await SlackCliPackage.ExtractExecutableAsync(archive, Hash(archive), default);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task RejectsAnArchiveThatDoesNotMatchThePinnedDigest()
    {
        byte[] archive = Archive(("slack", "executable"u8.ToArray()));

        SlackFailure failure = await Assert.ThrowsAsync<SlackFailure>(() =>
            SlackCliPackage.ExtractExecutableAsync(archive, new string('0', 64), default));

        Assert.Equal("The Slack setup helper failed checksum verification.", failure.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsMissingOrAmbiguousExecutables(bool duplicate)
    {
        byte[] archive = duplicate
            ? Archive(("first/slack", "one"u8.ToArray()), ("second/slack", "two"u8.ToArray()))
            : Archive(("not-slack", "other"u8.ToArray()));

        await Assert.ThrowsAsync<SlackFailure>(() =>
            SlackCliPackage.ExtractExecutableAsync(archive, Hash(archive), default));
    }

    private static string Hash(byte[] archive) => Convert.ToHexString(SHA256.HashData(archive));

    private static byte[] Archive(params (string Name, byte[] Content)[] files)
    {
        using var result = new MemoryStream();
        using (var gzip = new GZipStream(result, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip))
        {
            foreach ((string name, byte[] content) in files)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(content)
                };
                writer.WriteEntry(entry);
            }
        }
        return result.ToArray();
    }
}
