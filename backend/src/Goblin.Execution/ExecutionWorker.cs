using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using Goblin.Execution;
using Goblin.Integrations.Codex;

namespace Goblin.Execution;

public static class ExecutionWorker
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 1) return 2;
        string directory = Path.GetFullPath(args[0]);
        WorkerInput? input = await ExecutionFiles.ReadAsync<WorkerInput>(Path.Combine(directory, "input.json"));
        if (input is null) return 2;
        string runtimeHome = Path.Combine(directory, "home");
        string workspace = Path.Combine(directory, "text");
        Directory.CreateDirectory(runtimeHome);
        Directory.CreateDirectory(workspace);
        using (FileStream gate = await LocalTextHost.GateAsync(directory, CancellationToken.None))
        {
            if (File.Exists(Path.Combine(directory, "stopped"))) return 0;
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            await ExecutionFiles.WriteAsync(Path.Combine(directory, "process.json"),
                new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks, Environment.MachineName));
        }
        ExecutionObservation outcome;
        ExecutionSession? session = null;
        await using (var codex = new CodexClient(new()
        {
            Home = runtimeHome, CodexHome = input.CodexHome, Workspace = workspace, Command = input.CodexCommand
        }))
        {
            try
            {
                outcome = await new CodexWorkRunner(codex).RunAsync(input.Work, false,
                    value => { session = value.Session ?? session; return ExecutionFiles.WriteAsync(Path.Combine(directory, "progress.json"), value); }, CancellationToken.None);
            }
            catch (IntegrationFailure failure)
            {
                outcome = new(ObservationKind.Failed, session, Failure: failure.Code.Contains("timeout", StringComparison.Ordinal)
                    ? FailureKind.TimedOut : FailureKind.ExecutionFailed);
            }
            catch (TimeoutException) { outcome = new(ObservationKind.Failed, session, Failure: FailureKind.TimedOut); }
            catch { outcome = new(ObservationKind.Failed, session, Failure: FailureKind.HostUnavailable); }
        }
        // Only publish a stopped outcome after the runtime and its children have retired.
        await ExecutionFiles.WriteAsync(Path.Combine(directory, "result.json"), outcome);
        return 0;
    }
}
