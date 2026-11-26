using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Work;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using Wolverine.Attributes;

namespace Goblin.Application.Workspaces;

public sealed record DeleteClosedWorkspace(long WorkId);

public static class DeleteClosedWorkspaceHandler
{
    [ScheduleRetry(typeof(Exception), 1, 5, 30, 120)]
    public static async Task Handle(DeleteClosedWorkspace message, WorkStore store, InspectionStore inspections,
        IExecutionHost host, CancellationToken token)
    {
        WorkSnapshot work = (await store.GetAsync(message.WorkId, token)).Work;
        if (work.Workspace is null || WorkWorkspace.RetainsVolume(work.Status) ||
            work.Attempts.LastOrDefault()?.CleanupPending == true) return;
        if (await inspections.HasActiveAsync(message.WorkId, token))
            throw new IOException("Workspace deletion is waiting for inspection to stop.");
        if (host is not IWorkspaceVolumeHost volumes)
            throw new InvalidOperationException("The current execution host cannot delete workspace volumes.");
        await volumes.DeleteClosedWorkspaceAsync(work, token);
    }
}
