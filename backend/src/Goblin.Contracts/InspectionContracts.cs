using System;
using System.Threading;
using System.Threading.Tasks;

namespace Goblin.Contracts.Runtime;

public sealed record InspectionAllocation(long Id, long WorkId, long AttemptId, long? CheckpointId, string SourceVolume);
public interface IInspectionHost
{
    Task StartAsync(InspectionAllocation session, string capability, CancellationToken token);
    Task<string> ObserveAsync(InspectionAllocation session, CancellationToken token);
    Task StopAsync(InspectionAllocation session, CancellationToken token);
}
