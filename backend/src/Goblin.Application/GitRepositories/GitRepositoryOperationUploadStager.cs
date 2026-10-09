using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Work;
using Goblin.Contracts;
using Goblin.Core.Work;

namespace Goblin.Application.GitRepositories;

internal sealed class GitRepositoryOperationUpload : IDisposable
{
    internal required long Id { get; init; }
    internal required long AttemptId { get; init; }
    internal required GitRepositoryOperationKind Kind { get; init; }
    internal required string Fingerprint { get; init; }
    internal required string UploadPath { get; init; }
    internal required string BundlePath { get; init; }

    public void Dispose() => File.Delete(UploadPath);
}

// Owns the bounded stream-to-file boundary. The staged upload owns its
// temporary file until persistence atomically moves it into the durable bundle.
internal sealed class GitRepositoryOperationUploadStager
{
    private const long DefaultMaximumLength = 128 * 1024 * 1024;
    private readonly string _directory;
    private readonly long _maximumLength;

    internal GitRepositoryOperationUploadStager(string directory, long maximumLength = DefaultMaximumLength)
    {
        _directory = directory;
        _maximumLength = maximumLength;
    }

    internal string DirectoryFor(long attemptId) =>
        Path.Combine(_directory, attemptId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    internal string BundleFor(long attemptId, long id) =>
        Path.Combine(DirectoryFor(attemptId), id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".bundle");

    internal async Task<GitRepositoryOperationUpload> StageAsync(long attemptId, long id,
        GitRepositoryOperationKind kind, Stream input, CancellationToken token)
    {
        string directory = DirectoryFor(attemptId);
        string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".upload");
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            byte[] buffer = new byte[65536];
            long length = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, token)) != 0)
            {
                length += count;
                if (length > _maximumLength) throw new ApplicationFailure("repository_operation_unavailable");
                hash.AppendData(buffer, 0, count);
                await file.WriteAsync(buffer.AsMemory(0, count), token);
            }
            return new()
            {
                Id = id,
                AttemptId = attemptId,
                Kind = kind,
                Fingerprint = kind.WireValue() + ":" + Convert.ToHexString(hash.GetHashAndReset()),
                UploadPath = temporary,
                BundlePath = BundleFor(attemptId, id)
            };
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }
}
