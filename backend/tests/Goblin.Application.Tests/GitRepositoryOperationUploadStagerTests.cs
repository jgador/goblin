using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Goblin.Application.GitRepositories;
using Goblin.Application.Work;
using Goblin.Contracts;
using Goblin.Core.Work;
using Xunit;

namespace Goblin.Application.Tests;

public sealed class GitRepositoryOperationUploadStagerTests
{
    [Fact]
    public async Task StagesBytesWithStableFingerprintAndOwnsTemporaryFile()
    {
        string root = TemporaryDirectory(out string attemptDirectory);
        byte[] content = [1, 2, 3, 4];
        try
        {
            var stager = new GitRepositoryOperationUploadStager(root);
            GitRepositoryOperationUpload upload = await stager.StageAsync(17, 23,
                GitRepositoryOperationKind.Publish, new MemoryStream(content), default);
            string temporary = upload.UploadPath;
            using (upload)
            {
                Assert.Equal(23, upload.Id);
                Assert.Equal(17, upload.AttemptId);
                Assert.Equal(GitRepositoryOperationKind.Publish, upload.Kind);
                Assert.Equal("publish:" + Convert.ToHexString(SHA256.HashData(content)), upload.Fingerprint);
                Assert.Equal(Path.Combine(attemptDirectory, "23.bundle"), upload.BundlePath);
                Assert.Equal(content, await File.ReadAllBytesAsync(upload.UploadPath));
            }
            Assert.False(File.Exists(temporary));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RejectsOversizedInputAndRemovesPartialUpload()
    {
        string root = TemporaryDirectory(out string attemptDirectory);
        try
        {
            var stager = new GitRepositoryOperationUploadStager(root, maximumLength: 3);

            ApplicationFailure failure = await Assert.ThrowsAsync<ApplicationFailure>(() => stager.StageAsync(17, 23,
                GitRepositoryOperationKind.Checkpoint, new MemoryStream([1, 2, 3, 4]), default));

            Assert.Equal("repository_operation_unavailable", failure.Code);
            Assert.Empty(Directory.GetFiles(attemptDirectory));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string TemporaryDirectory(out string attemptDirectory)
    {
        string root = Path.Combine(Path.GetTempPath(), "goblin-repository-upload-" + Guid.NewGuid().ToString("N"));
        attemptDirectory = Path.Combine(root, "17");
        Directory.CreateDirectory(attemptDirectory);
        return root;
    }
}
