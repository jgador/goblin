using System;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Core.Work;

namespace Goblin.Contracts.Runtime;

public sealed record InspectionAllocation(long Id, long WorkId, long AttemptId, string SourceVolume);
public interface IInspectionHost
{
    Task StartAsync(InspectionAllocation session, CancellationToken token);
    Task<InspectionObservation> ObserveAsync(InspectionAllocation session, CancellationToken token);
    Task StopAsync(InspectionAllocation session, CancellationToken token);
}
