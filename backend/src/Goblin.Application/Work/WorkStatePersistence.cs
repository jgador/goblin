using System;
using System.Text.Json;
using Goblin.Contracts;
using Goblin.Core.Work;
using Row = Goblin.Persistence.Entities.WorkItem;

namespace Goblin.Application.Work;

// Maps the core snapshot and its durable summary together. Callers own tracking,
// attempt records, saving, and the transaction that commits related changes.
internal static class WorkStatePersistence
{
    internal static Row Create(WorkItem work, DateTimeOffset now)
    {
        var row = new Row { Id = work.Id, Objective = work.Objective, CreatedAt = now.UtcDateTime };
        Update(row, work, now);
        return row;
    }

    internal static void Update(Row row, WorkItem work, DateTimeOffset now)
    {
        row.State = JsonSerializer.Serialize(work.Snapshot(), ContractJson.Options);
        row.Status = work.Status.ToString();
        row.AgentId = work.AgentId;
        row.Version++;
        row.UpdatedAt = now.UtcDateTime;
    }

    internal static WorkItem Restore(Row row) => Restore(row.State);

    internal static WorkSnapshot Snapshot(Row row) => Restore(row).Snapshot();

    internal static WorkSnapshot Snapshot(string state) => Restore(state).Snapshot();

    private static WorkItem Restore(string state) =>
        WorkItem.Restore(JsonSerializer.Deserialize<WorkSnapshot>(state, ContractJson.Options)!);
}
