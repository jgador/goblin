using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Goblin.Application.GitRepositories;
using Xunit;

namespace Goblin.Application.Tests;

public sealed class GitRepositoryOperationEvidenceTests
{
    [Fact]
    public async Task DispatchFailureEvidenceIsDurableUntilExplicitlyCleared()
    {
        string directory = TemporaryDirectory();
        try
        {
            var evidence = new GitRepositoryOperationEvidence(directory);

            Assert.False(evidence.HasDispatchFailure(11));
            await evidence.RecordDispatchFailureAsync(11);
            Assert.True(evidence.HasDispatchFailure(11));
            Assert.Equal("failed", await File.ReadAllTextAsync(Path.Combine(directory, "11.failed")));

            evidence.ClearDispatchFailure(11);
            Assert.False(evidence.HasDispatchFailure(11));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ExternalLaunchEvidencePreservesRecoveryPolicy()
    {
        string directory = TemporaryDirectory();
        try
        {
            var evidence = new GitRepositoryOperationEvidence(directory);
            Assert.True(evidence.ExternalProcessStopped(22));

            if (!OperatingSystem.IsLinux()) return;
            await evidence.RecordExternalLaunchAsync(22, default);
            Assert.False(evidence.ExternalProcessStopped(22));

            string marker = Path.Combine(directory, "22.external");
            string boot = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
            double uptime = double.Parse(File.ReadAllText("/proc/uptime").Split(' ')[0], CultureInfo.InvariantCulture);

            await File.WriteAllLinesAsync(marker, [boot, (uptime - 301).ToString(CultureInfo.InvariantCulture)]);
            Assert.True(evidence.ExternalProcessStopped(22));

            await File.WriteAllLinesAsync(marker, [Guid.NewGuid().ToString(), uptime.ToString(CultureInfo.InvariantCulture)]);
            Assert.True(evidence.ExternalProcessStopped(22));

            await File.WriteAllTextAsync(marker, "malformed");
            Assert.False(evidence.ExternalProcessStopped(22));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static string TemporaryDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "goblin-repository-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
