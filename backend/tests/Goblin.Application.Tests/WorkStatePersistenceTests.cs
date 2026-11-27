using System;
using System.Text.Json;
using Goblin.Application.Work;
using Goblin.Core.Work;
using Xunit;

namespace Goblin.Application.Tests;

public sealed class WorkStatePersistenceTests
{
    [Fact]
    public void SnapshotRestorationUsesTheAggregateBoundary()
    {
        DateTimeOffset now = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
        var work = new WorkItem(11, "Validate durable Work", now, "Test work");
        work.Assign(12, now);
        work.QueueExecution(13, new("codex", 1, gitRepository: new("owner/repo", "Goblin", "agent@example.com")), now);
        Assert.True(work.TryClaimExecution(13, 14, "k8s/agents/run-11/1", now));
        Persistence.Entities.WorkItem row = WorkStatePersistence.Create(work, now);

        WorkSnapshot snapshot = WorkStatePersistence.Snapshot(row);

        Assert.Equal("Test work", row.Title);
        Assert.Equal("Validate durable Work", row.Description);
        Assert.Equal(row.Title, snapshot.Title);
        Assert.Equal(row.Description, snapshot.Description);
        using JsonDocument state = JsonDocument.Parse(row.State);
        Assert.False(state.RootElement.TryGetProperty("title", out _));
        Assert.False(state.RootElement.TryGetProperty("description", out _));
        Assert.False(state.RootElement.GetProperty("workspace").TryGetProperty("baseCommitSha", out _));
        Assert.Equal(work.Snapshot().History, snapshot.History);
        Assert.Equal(work.Workspace, snapshot.Workspace);
        Assert.Equal(12, snapshot.AgentId);
    }

    [Fact]
    public void SnapshotRestorationRejectsMissingAggregateState()
    {
        var row = new Persistence.Entities.WorkItem { Id = 11, Title = "Test work", Description = "Missing state", State = null! };
        Assert.Throws<ArgumentNullException>(() => WorkStatePersistence.Snapshot(row));
    }

    [Fact]
    public void SnapshotRestorationRejectsInvalidAggregateState()
    {
        DateTimeOffset now = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
        var work = new WorkItem(11, "Invalid schema", now, "Test work");
        Persistence.Entities.WorkItem row = WorkStatePersistence.Create(work, now);
        row.State = row.State.Replace("\"schemaVersion\":2", "\"schemaVersion\":1", StringComparison.Ordinal);

        Assert.Throws<WorkRuleException>(() => WorkStatePersistence.Snapshot(row));
    }
}
