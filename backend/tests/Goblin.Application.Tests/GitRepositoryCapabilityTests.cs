using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Goblin.Application.GitRepositories;
using Xunit;

namespace Goblin.Application.Tests;

public sealed class GitRepositoryCapabilityTests
{
    [Fact]
    public void CapabilityIsPersistentAndBoundToWorkAttemptAndGeneration()
    {
        string directory = Path.Combine(Path.GetTempPath(), "goblin-capability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string key = Path.Combine(directory, "capability-key");
        try
        {
            var first = new GitRepositoryCapability(key);
            string capability = first.Issue(11, 22, "generation");

            Assert.Equal(64, capability.Length);
            Assert.Equal(Convert.ToHexString(HMACSHA256.HashData(File.ReadAllBytes(key),
                Encoding.UTF8.GetBytes("11/22/generation"))), capability);
            Assert.True(first.IsValid(capability, 11, 22, "generation"));
            Assert.False(first.IsValid(capability, 12, 22, "generation"));
            Assert.False(first.IsValid(capability, 11, 23, "generation"));
            Assert.False(first.IsValid(capability, 11, 22, "changed"));
            Assert.False(first.IsValid("not-hex", 11, 22, "generation"));
            Assert.False(first.IsValid("00", 11, 22, "generation"));

            var reopened = new GitRepositoryCapability(key);
            Assert.Equal(capability, reopened.Issue(11, 22, "generation"));
            Assert.Equal(32, new FileInfo(key).Length);
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(key));
        }
        finally { Directory.Delete(directory, true); }
    }
}
