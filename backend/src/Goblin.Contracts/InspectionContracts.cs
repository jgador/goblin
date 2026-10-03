using System;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Core.Work;

namespace Goblin.Contracts.Runtime;

public sealed class InspectionAllocation
{
    public InspectionAllocation(long id, long workId, long attemptId, string sourceVolume)
    {
        Id = id;
        WorkId = workId;
        AttemptId = attemptId;
        SourceVolume = sourceVolume;
    }

    public long Id { get; init; }

    public long WorkId { get; init; }

    public long AttemptId { get; init; }

    public string SourceVolume { get; init; }
}

public interface IInspectionHost
{
    Task StartAsync(InspectionAllocation session, CancellationToken token);

    Task<InspectionObservation> ObserveAsync(InspectionAllocation session, CancellationToken token);

    Task StopAsync(InspectionAllocation session, CancellationToken token);
}
