using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Goblin.Contracts;
using Goblin.Core.Work;
using Row = Goblin.Persistence.Entities.WorkItem;

namespace Goblin.Application.Work;

// Maps the Work columns and lifecycle state together. Callers own tracking,
// attempt records, saving, and the transaction that commits related changes.
internal static class WorkStatePersistence
{
    private static readonly JsonSerializerOptions StateJson = new(ContractJson.Options)
    {
        TypeInfoResolver = ContractJson.Options.TypeInfoResolver!.WithAddedModifier(ExcludeColumnsFromState)
    };

    internal static Row Create(WorkItem work, DateTimeOffset now)
    {
        var row = new Row { Id = work.Id, CreatedAt = now.UtcDateTime };
        Update(row, work, now);
        return row;
    }

    internal static void Update(Row row, WorkItem work, DateTimeOffset now)
    {
        row.Title = work.Title ?? throw new InvalidOperationException("Persisted Work requires a title.");
        row.Description = work.Description;
        SaveSnapshot(row, work);
        row.Status = work.Status.ToString();
        row.AgentId = work.AgentId;
        row.Version++;
        row.UpdatedAt = now.UtcDateTime;
    }

    internal static void SaveSnapshot(Row row, WorkItem work) => row.State = JsonSerializer.Serialize(work.Snapshot(), StateJson);

    internal static WorkItem Restore(Row row) => Restore(row.State, row.Description, row.Title);

    internal static WorkSnapshot Snapshot(Row row) => Restore(row).Snapshot();

    internal static WorkSnapshot Snapshot(string state, string description, string title) => Restore(state, description, title).Snapshot();

    private static WorkItem Restore(string state, string description, string title) =>
        WorkItem.Restore(JsonSerializer.Deserialize<WorkSnapshot>(state, StateJson)!, description, title);

    private static void ExcludeColumnsFromState(JsonTypeInfo type)
    {
        if (type.Type != typeof(WorkSnapshot)) return;
        type.Properties.Remove(type.Properties.Single(property => property.Name == "title"));
        type.Properties.Remove(type.Properties.Single(property => property.Name == "description"));
    }
}
