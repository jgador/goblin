using System;

namespace Goblin.Core.Work;

public enum InspectionState
{
    Queued,
    Starting,
    Available,
    Stopping,
    Stopped,
    NeedsAttention
}

public enum InspectionObservation
{
    Pending,
    Running,
    Missing,
    Failed
}

public static class WorkspaceSessionRules
{
    public static bool IsActive(InspectionState state) => state != InspectionState.Stopped;

    public static bool HoldsCapacity(InspectionState state) => state is InspectionState.Starting or InspectionState.Available or InspectionState.Stopping or InspectionState.NeedsAttention;

    public static bool RequiresObservation(InspectionState state) => state is InspectionState.Queued or InspectionState.Starting or InspectionState.Available or InspectionState.Stopping;

    public static InspectionState Observe(InspectionState current, InspectionObservation observed) => current switch
    {
        InspectionState.Starting when observed == InspectionObservation.Running => InspectionState.Available,
        InspectionState.Starting or InspectionState.Available when observed is InspectionObservation.Failed or InspectionObservation.Missing => InspectionState.NeedsAttention,
        InspectionState.Stopping when observed == InspectionObservation.Missing => InspectionState.Stopped,
        _ => current
    };

    public static void RequireOpenable(AttemptStatus status)
    {
        if (status is AttemptStatus.Starting or AttemptStatus.Running or AttemptStatus.Uncertain or AttemptStatus.CancellationRequested)
            throw new WorkRuleException(WorkRule.InvalidTransition);
    }
}
