using System;
using System.Text.Json;
using Goblin.Application.Work;
using Goblin.Contracts;
using Goblin.Core.Work;
using Xunit;

namespace Goblin.Application.Tests;

public sealed class WorkStatePersistenceTests
{
    [Fact]
    public void SnapshotRestorationUsesTheAggregateBoundary()
    {
        DateTimeOffset now = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
        var work = new WorkItem(11, "Validate durable Work", now);
        work.Assign(12, now);
        Persistence.Entities.WorkItem row = WorkStatePersistence.Create(work, now);

        WorkSnapshot snapshot = WorkStatePersistence.Snapshot(row);

        Assert.Equal(work.Snapshot().History, snapshot.History);
        Assert.Equal(12, snapshot.AgentId);
    }

    [Fact]
    public void SnapshotRestorationRejectsMissingAggregateState()
    {
        var row = new Persistence.Entities.WorkItem { Id = 11, Objective = "Missing state", State = null! };
        Assert.Throws<ArgumentNullException>(() => WorkStatePersistence.Snapshot(row));
    }

    [Fact]
    public void SnapshotRestorationRejectsInvalidAggregateState()
    {
        DateTimeOffset now = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
        var work = new WorkItem(11, "Invalid schema", now);
        string invalid = JsonSerializer.Serialize(work.Snapshot(), ContractJson.Options)
            .Replace("\"schemaVersion\":2", "\"schemaVersion\":1", StringComparison.Ordinal);

        Assert.Throws<WorkRuleException>(() => WorkStatePersistence.Snapshot(invalid));
    }
}
