using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using Goblin.Execution;
using Goblin.Web;
using Xunit;

namespace Goblin.Application.Tests;

public sealed class ExecutionHostTests
{
    private static long _nextId = int.MaxValue;
    private static long NextId() => System.Threading.Interlocked.Increment(ref _nextId);

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
            string attemptDirectory = Path.Combine(directory, work.Attempts[^1].Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
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

    [Fact]
    public async Task RealTextWorkerIdentitySurvivesObservationAndHostRestart()
    {
        if (!OperatingSystem.IsLinux()) return;
        (TextHostOptions? options, WorkSnapshot? work, string? directory) = await CreateWorkerAsync("prompt-timeout");
        string attemptDirectory = Path.Combine(directory, work.Attempts[^1].Id.ToString());
        var host = new LocalTextHost(options);
        try
        {
            await host.StartAsync(work, default);
            await WaitForFileAsync(Path.Combine(attemptDirectory, "progress.json"));
            ProcessIdentity identity = (await ExecutionFiles.ReadAsync<ProcessIdentity>(Path.Combine(attemptDirectory, "process.json")))!;
            Assert.NotNull(identity.StartTicks);
            Assert.False(string.IsNullOrWhiteSpace(identity.BootId));
            Assert.False(string.IsNullOrWhiteSpace(identity.PidNamespace));
            using var process = Process.GetProcessById(identity.Pid);
            Assert.False(process.HasExited);
            Assert.Equal(ObservationKind.Running, (await host.ObserveAsync(work, false, default)).Kind);

            // The new host has no in-memory Process object from launch.
            var restarted = new LocalTextHost(options);
            ExecutionObservation observed = await restarted.ObserveAsync(work, false, default);
            Assert.Equal(ObservationKind.Running, observed.Kind);
            Assert.Equal("test-thread-1", observed.Session!.SessionReference);
            await restarted.StartAsync(work, default);
            Assert.Equal(identity, await ExecutionFiles.ReadAsync<ProcessIdentity>(Path.Combine(attemptDirectory, "process.json")));
            Assert.Equal(ObservationKind.Stopped, (await restarted.ObserveAsync(work, true, default)).Kind);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { await RemoveWorkerAsync(directory, attemptDirectory); }
    }

    [Fact]
    public async Task RealTextWorkerRetainsInputResponseAfterExitAndCleanup()
    {
        if (!OperatingSystem.IsLinux()) return;
        (TextHostOptions? options, WorkSnapshot? work, string? directory) = await CreateWorkerAsync("work-input");
        string attemptDirectory = Path.Combine(directory, work.Attempts[^1].Id.ToString());
        var host = new LocalTextHost(options);
        try
        {
            await host.StartAsync(work, default);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            ExecutionObservation observed;
            do
            {
                observed = await host.ObserveAsync(work, false, timeout.Token);
                Assert.Contains(observed.Kind, new[] { ObservationKind.Pending, ObservationKind.Running, ObservationKind.Paused });
                if (observed.Kind != ObservationKind.Paused) await Task.Delay(5, timeout.Token);
            } while (observed.Kind != ObservationKind.Paused);
            Assert.Equal(ObservationKind.Paused, observed.Kind);
            Assert.Equal("Which outcome matters?", observed.Text);
            Assert.Equal("test-model", observed.Session!.Model);
            await host.CleanupAsync(work, default);
            Assert.Equal(observed, await new LocalTextHost(options).ObserveAsync(work, false, default));
        }
        finally { await RemoveWorkerAsync(directory, attemptDirectory); }
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("boot")]
    [InlineData("namespace")]
    [InlineData("reused-pid")]
    [InlineData("wall-clock")]
    public async Task LinuxObservationRequiresStableProcessAndHostIdentity(string scenario)
    {
        if (!OperatingSystem.IsLinux()) return;
        string directory = Directory.CreateTempSubdirectory("goblin-identity-").FullName;
        WorkSnapshot work = Claimed();
        string attemptDirectory = Directory.CreateDirectory(Path.Combine(directory, work.Attempts[^1].Id.ToString())).FullName;
        using Process process = Process.Start(new ProcessStartInfo("/bin/sleep", "30") { UseShellExecute = false })!;
        try
        {
            ProcessIdentity identity = ProcessIdentity.Capture(process);
            identity = scenario switch
            {
                "legacy" => new(identity.Pid, identity.StartedAt, identity.Machine),
                "boot" => identity with { BootId = "another-boot" },
                "namespace" => identity with { PidNamespace = "another-namespace" },
                "reused-pid" => identity with { StartTicks = identity.StartTicks + 1 },
                _ => identity with { StartedAt = identity.StartedAt + TimeSpan.TicksPerSecond }
            };
            await ExecutionFiles.WriteAsync(Path.Combine(attemptDirectory, "process.json"), identity);
            var host = new LocalTextHost(new(directory, directory, "unused", "unused"));
            ObservationKind expected = scenario switch
            {
                "wall-clock" => ObservationKind.Pending,
                "reused-pid" => ObservationKind.Stopped,
                _ => ObservationKind.Uncertain
            };
            Assert.Equal(expected, (await host.ObserveAsync(work, false, default)).Kind);
            if (scenario != "wall-clock")
                Assert.Equal(expected, (await host.ObserveAsync(work, true, default)).Kind);
            Assert.False(process.HasExited); // Never kill a process whose identity is unproven.
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Directory.Delete(directory, true);
        }
    }

    private static async Task<(TextHostOptions Options, WorkSnapshot Work, string Directory)> CreateWorkerAsync(string scenario)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "tests/fixtures/fake-codex.mts"))) root = root.Parent;
        string directory = Directory.CreateTempSubdirectory("goblin-real-worker-").FullName;
        string runtime = Path.Combine(directory, "codex-fixture");
        string fixture = Path.Combine(root!.FullName, "tests/fixtures/fake-codex.mts");
        await File.WriteAllTextAsync(runtime, "#!/bin/sh\nexec node '" + fixture.Replace("'", "'\\''") + "' " + scenario + " \"$@\"\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(runtime, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string codexHome = Directory.CreateDirectory(Path.Combine(directory, "codex")).FullName;
        return (new(directory, codexHome, runtime, typeof(GoblinApplication).Assembly.Location), Claimed(), directory);
    }

    private static async Task WaitForFileAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!File.Exists(path)) await Task.Delay(20, timeout.Token);
    }

    private static async Task RemoveWorkerAsync(string directory, string attemptDirectory)
    {
        // Cleanup must also work when a regression misidentifies the live worker.
        ProcessIdentity? identity = await ExecutionFiles.ReadAsync<ProcessIdentity>(Path.Combine(attemptDirectory, "process.json"));
        if (identity is not null)
        {
            try
            {
                using var process = Process.GetProcessById(identity.Pid);
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            catch (ArgumentException) { }
        }
        Directory.Delete(directory, true);
    }

    private static WorkSnapshot Claimed()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var work = new WorkItem(NextId(), "Answer a question", now);
        work.Assign(NextId(), now);
        work.QueueExecution(NextId(), new("codex", NextId()), now);
        work.TryClaimExecution(work.CurrentAttempt!.Id, NextId(), "text-test", now);
        return work.Snapshot();
    }
}
