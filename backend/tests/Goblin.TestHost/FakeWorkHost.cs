using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using Goblin.Execution;

// Durable browser fixture; never compiled into the application image.
internal sealed class FakeWorkHost : IExecutionHost
{
    private readonly string _root;

    public FakeWorkHost(string root) => _root = root;

    public RuntimeCapabilities[] Capabilities => [new("codex", true, false, true, false, false)];
    public string EnvironmentFor(long workId, long attemptId) => "fixture/" + attemptId;
    private string PathFor(WorkSnapshot work) => Path.Combine(_root, work.Attempts[^1].Id + ".work-result");
    public Task StartAsync(WorkSnapshot work, CancellationToken token)
    {
        bool question = work.Objective.Contains("decision", StringComparison.OrdinalIgnoreCase) && work.Decisions.Length == 0;
        return ExecutionFiles.WriteAsync(PathFor(work), new ExecutionObservation(
            work.Objective.Contains("failure", StringComparison.OrdinalIgnoreCase) ? ObservationKind.Failed : question ? ObservationKind.InputRequired : ObservationKind.Result,
            new("fixture-model", "fixture-session", "fixture-operation"), question ? "Which outcome should I prioritize?" : "Proposed result for: " + work.Objective,
            FailureKind.ExecutionFailed), token);
    }
    public async Task<ExecutionObservation> ObserveAsync(WorkSnapshot work, bool stop, CancellationToken token) =>
        await ExecutionFiles.ReadAsync<ExecutionObservation>(PathFor(work), token) ?? new(stop ? ObservationKind.Stopped : ObservationKind.Pending);
    public Task CleanupAsync(WorkSnapshot work, CancellationToken token) => Task.CompletedTask;
}
