using System;

namespace Goblin.Core.Work;

public static class WorkspaceSessionRules
{
    public static bool HoldsCapacity(string state) => state is "Starting" or "Available" or "Stopping" or "NeedsAttention";
    public static string Observe(string current, string observed) => current switch
    {
        "Starting" when observed == "Running" => "Available",
        "Starting" or "Available" when observed is "Failed" or "Missing" => "NeedsAttention",
        "Stopping" when observed == "Missing" => "Stopped",
        _ => current
    };
    public static void RequireOpenable(AttemptStatus status)
    {
        if (status is AttemptStatus.Starting or AttemptStatus.Running or AttemptStatus.Uncertain or AttemptStatus.CancellationRequested)
            throw new WorkRuleException(WorkRule.InvalidTransition);
    }
}
