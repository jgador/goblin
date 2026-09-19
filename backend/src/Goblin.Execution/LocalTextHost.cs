using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;

namespace Goblin.Execution;

public sealed record TextHostOptions(string Directory, string CodexHome, string CodexCommand,
    string WorkerAssembly, string DotnetCommand = "dotnet");
public sealed record WorkerInput(WorkSnapshot Work, string CodexHome, string CodexCommand);
public sealed record ProcessIdentity(int Pid, long StartedAt, string Machine);

// No agent sandbox is allocated for text-only work. This independent worker has
// tools disabled and receives an allowlisted environment, never DB credentials.
// Its journal survives the browser and web process. Repository work uses the
// sandbox host, never this host.
public sealed class LocalTextHost(TextHostOptions options) : IExecutionHost
{
    public RuntimeCapabilities[] Capabilities => [new("codex", true, false, true, false, false)];
    public string EnvironmentFor(Guid workId, Guid attemptId) => "text/" + attemptId.ToString("N");
    private string DirectoryFor(WorkSnapshot work) => Path.Combine(options.Directory, work.Attempts[^1].Id.ToString("N"));

    public async Task StartAsync(WorkSnapshot work, CancellationToken token)
    {
        if (work.Attempts[^1].Target.Repository is not null) throw new InvalidOperationException("Repository execution requires an agent sandbox.");
        string directory = DirectoryFor(work);
        Directory.CreateDirectory(directory);
        using FileStream gate = await GateAsync(directory, token);
        if (File.Exists(Path.Combine(directory, "reserved"))) return;
        // The persistent reservation is a tombstone as well as a start fence.
        using (File.Create(Path.Combine(directory, "reserved"))) { }
        await ExecutionFiles.WriteAsync(Path.Combine(directory, "input.json"),
            new WorkerInput(work, options.CodexHome, options.CodexCommand), token);
        var start = new ProcessStartInfo(options.DotnetCommand)
        {
            UseShellExecute = false, WorkingDirectory = directory,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment.Clear();
        start.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH");
        start.Environment["LANG"] = "C.UTF-8";
        start.ArgumentList.Add(options.WorkerAssembly);
        start.ArgumentList.Add("--execute");
        start.ArgumentList.Add(directory);
        using Process process = Process.Start(start) ?? throw new IOException("Worker did not start.");
        await ExecutionFiles.WriteAsync(Path.Combine(directory, "process.json"),
            new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks, Environment.MachineName), token);
        // The worker never emits upstream details. Drain anyway to avoid pipes
        // preventing completion; disposal of the Process handle does not kill it.
        _ = DrainAsync(process.StandardOutput);
        _ = DrainAsync(process.StandardError);
    }

    public async Task<ExecutionObservation> ObserveAsync(WorkSnapshot work, bool stop, CancellationToken token)
    {
        string directory = DirectoryFor(work);
        Directory.CreateDirectory(directory);
        using FileStream gate = await GateAsync(directory, token);
        ExecutionObservation? result = await ExecutionFiles.ReadAsync<ExecutionObservation>(Path.Combine(directory, "result.json"), token);
        if (result is not null) return result;
        ProcessIdentity? identity = await ExecutionFiles.ReadAsync<ProcessIdentity>(Path.Combine(directory, "process.json"), token);
        if (identity is null)
        {
            // Fence any delayed starter before asserting that nothing can run.
            using (File.Open(Path.Combine(directory, "reserved"), FileMode.OpenOrCreate)) { }
            using (File.Open(Path.Combine(directory, "stopped"), FileMode.OpenOrCreate)) { }
            return new(ObservationKind.Stopped);
        }
        if (identity.Machine != Environment.MachineName) return new(ObservationKind.Uncertain, Failure: FailureKind.HostUnavailable);
        Process? process = null;
        try
        {
            process = Process.GetProcessById(identity.Pid);
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != identity.StartedAt)
                return new(ObservationKind.Stopped);
            if (stop)
            {
                // Kill the tree and wait for termination; intent alone is not completion.
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(token);
                result = await ExecutionFiles.ReadAsync<ExecutionObservation>(Path.Combine(directory, "result.json"), token);
                return result ?? new(ObservationKind.Stopped);
            }
            return await ExecutionFiles.ReadAsync<ExecutionObservation>(Path.Combine(directory, "progress.json"), token)
                ?? new(ObservationKind.Pending);
        }
        catch (ArgumentException) { return new(ObservationKind.Stopped); }
        finally { process?.Dispose(); }
    }

    public Task CleanupAsync(WorkSnapshot work, CancellationToken token)
    {
        // Retain the reservation and result as recovery evidence. Input is a
        // duplicate of persisted context and is no longer needed after capture.
        File.Delete(Path.Combine(DirectoryFor(work), "input.json"));
        return Task.CompletedTask;
    }

    public static async Task<FileStream> GateAsync(string directory, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(directory, "gate"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(20, token); }
        }
    }
    private static async Task DrainAsync(StreamReader reader)
    {
        try { while (await reader.ReadLineAsync() is not null) { } } catch { }
    }
}
