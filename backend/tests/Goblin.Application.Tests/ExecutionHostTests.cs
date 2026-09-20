using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using Goblin.Execution;
using Xunit;

namespace Goblin.Application.Tests;

public sealed class ExecutionHostTests
{
    [Fact]
    public async Task CancellationBeforeDeliveryFencesARealTextWorkerStart()
    {
        string directory = Path.Combine(Path.GetTempPath(), "goblin-host-" + Guid.NewGuid().ToString("N"));
        try
        {
            var host = new LocalTextHost(new(directory, directory, "codex", "must-not-launch", "must-not-exist"));
            WorkSnapshot work = Claimed();
            Assert.Equal(ObservationKind.Stopped, (await host.ObserveAsync(work, true, default)).Kind);
            await host.StartAsync(work, default);
            Assert.Equal(ObservationKind.Stopped, (await host.ObserveAsync(work, false, default)).Kind);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task DuplicateTextDeliveryStartsOneProcessAndCancellationStopsIt()
    {
        if (OperatingSystem.IsWindows()) return;
        string directory = Path.Combine(Path.GetTempPath(), "goblin-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string script = Path.Combine(directory, "worker.sh");
        await File.WriteAllTextAsync(script, "echo started >> starts\nexec sleep 30\n");
        var host = new LocalTextHost(new(directory, directory, "codex", script, "/bin/sh"));
        WorkSnapshot work = Claimed();
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => host.StartAsync(work, default)));
            string attemptDirectory = Path.Combine(directory, work.Attempts[^1].Id.ToString("N"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!File.Exists(Path.Combine(attemptDirectory, "starts"))) await Task.Delay(20, timeout.Token);
            Assert.Single(await File.ReadAllLinesAsync(Path.Combine(attemptDirectory, "starts")));
            Assert.Equal(ObservationKind.Pending, (await host.ObserveAsync(work, false, default)).Kind);
            Assert.Equal(ObservationKind.Stopped, (await host.ObserveAsync(work, true, default)).Kind);
            await host.StartAsync(work, default);
            Assert.Equal(ObservationKind.Stopped, (await host.ObserveAsync(work, false, default)).Kind);
        }
        finally
        {
            await host.ObserveAsync(work, true, default);
            Directory.Delete(directory, true);
        }
    }

    private static WorkSnapshot Claimed()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var work = new WorkItem(Guid.NewGuid(), "Answer a question", now);
        work.Assign(Guid.NewGuid(), now);
        work.QueueExecution(Guid.NewGuid(), new("codex", Guid.NewGuid()), now);
        work.TryClaimExecution(work.CurrentAttempt!.Id, Guid.NewGuid(), "text-test", now);
        return work.Snapshot();
    }
}
