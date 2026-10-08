using System;
using System.Linq;
using Goblin.Application.Runtime;
using Goblin.Core.Work;
using Goblin.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;
using AttemptRow = Goblin.Persistence.Entities.ExecutionAttempt;

namespace Goblin.Application.Tests;

public sealed class ExecutionRecoveryQueryTests
{
    [Theory]
    [InlineData(AttemptStatus.Queued, false, false, false, true)]
    [InlineData(AttemptStatus.Starting, false, false, false, true)]
    [InlineData(AttemptStatus.Running, false, false, false, true)]
    [InlineData(AttemptStatus.CancellationRequested, false, false, false, true)]
    [InlineData(AttemptStatus.Uncertain, false, false, false, true)]
    [InlineData(AttemptStatus.Waiting, false, false, false, false)]
    [InlineData(AttemptStatus.Succeeded, false, false, false, false)]
    [InlineData(AttemptStatus.Failed, false, false, false, false)]
    [InlineData(AttemptStatus.Cancelled, false, false, false, false)]
    [InlineData(AttemptStatus.Succeeded, true, false, false, true)]
    [InlineData(AttemptStatus.Failed, true, false, false, true)]
    [InlineData(AttemptStatus.Cancelled, true, false, false, true)]
    [InlineData(AttemptStatus.Waiting, true, false, false, true)]
    [InlineData(AttemptStatus.Succeeded, true, true, false, false)]
    [InlineData(AttemptStatus.Failed, true, true, false, false)]
    [InlineData(AttemptStatus.Cancelled, true, true, false, false)]
    [InlineData(AttemptStatus.Waiting, true, true, false, false)]
    [InlineData(AttemptStatus.Waiting, false, false, true, true)]
    [InlineData(AttemptStatus.Waiting, true, true, true, true)]
    [InlineData(AttemptStatus.Queued, true, true, true, true)]
    [InlineData(AttemptStatus.Running, true, true, false, true)]
    public void RecoveryDispatchesOnlyQueuedAttemptsAndObservesUnresolvedOwnership(
        AttemptStatus status, bool cleanupPending, bool cleanupFailed, bool retained, bool expected)
    {
        var row = new AttemptRow
        {
            Id = 7,
            WorkId = 3,
            TurnNumber = 4,
            Status = status.ToString(),
            CleanupPending = cleanupPending,
            CleanupFailed = cleanupFailed,
            WorkspaceRetained = retained
        };

        ExecutionRecoveryRequest[] requests = ExecutionRecoveryQuery.Select(new[] { row }.AsQueryable()).ToArray();

        if (!expected)
        {
            Assert.Empty(requests);
            return;
        }
        ExecutionRecoveryRequest request = Assert.Single(requests);
        Assert.Equal(3, request.WorkId);
        Assert.Equal(7, request.AttemptId);
        Assert.Equal(4, request.TurnNumber);
        Assert.Equal(status == AttemptStatus.Queued ? ExecutionRecoveryAction.Dispatch : ExecutionRecoveryAction.Reconcile,
            request.Action);
    }

    [Fact]
    public void RecoveryPreservesQueueOrderAcrossWorkItemsAndTurns()
    {
        DateTime now = new(2026, 10, 8, 8, 0, 0, DateTimeKind.Utc);
        var rows = new[]
        {
            new AttemptRow { Id = 9, WorkId = 3, TurnNumber = 2, Status = "Queued", QueuedAt = now.AddMinutes(2) },
            new AttemptRow { Id = 8, WorkId = 2, TurnNumber = 1, Status = "Running", QueuedAt = now },
            new AttemptRow { Id = 7, WorkId = 1, TurnNumber = 3, Status = "Failed", QueuedAt = now.AddMinutes(1) }
        };

        Assert.Equal(new[]
        {
            (2L, 8L, 1, ExecutionRecoveryAction.Reconcile),
            (3L, 9L, 2, ExecutionRecoveryAction.Dispatch)
        }, ExecutionRecoveryQuery.Select(rows.AsQueryable()).AsEnumerable()
            .Select(x => (x.WorkId, x.AttemptId, x.TurnNumber, x.Action)).ToArray());
    }

    [Fact]
    public void RecoveryQueryTranslatesSelectionOrderingAndRoutingToPostgres()
    {
        using var db = new GoblinDbContext(new DbContextOptionsBuilder<GoblinDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused").Options);

        string sql = ExecutionRecoveryQuery.Select(db.ExecutionAttempts.AsNoTracking()).ToQueryString();

        Assert.Contains("WHERE", sql);
        Assert.Contains("cleanup_pending", sql);
        Assert.Contains("NOT", sql);
        Assert.Contains("cleanup_failed", sql);
        Assert.Contains("workspace_retained", sql);
        Assert.Contains("ORDER BY", sql);
        Assert.Contains("queued_at", sql);
        Assert.Contains("CASE", sql);
        Assert.Contains("work_id", sql);
        Assert.Contains("turn_number", sql);
        Assert.DoesNotContain("agent_id", sql);
        Assert.DoesNotContain("connection_id", sql);
        Assert.DoesNotContain("owner_id", sql);
        Assert.DoesNotContain("environment_reference", sql);
    }
}
