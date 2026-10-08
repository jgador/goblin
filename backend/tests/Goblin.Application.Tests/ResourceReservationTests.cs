using System;
using System.Linq;
using Goblin.Application.Runtime;
using Goblin.Application.Workspaces;
using Goblin.Core.Work;
using Goblin.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;
using AttemptRow = Goblin.Persistence.Entities.ExecutionAttempt;
using SessionRow = Goblin.Persistence.Entities.WorkspaceSession;

namespace Goblin.Application.Tests;

public sealed class ResourceReservationTests
{
    [Theory]
    [InlineData(AttemptStatus.Starting, false, false, true)]
    [InlineData(AttemptStatus.Running, false, false, true)]
    [InlineData(AttemptStatus.CancellationRequested, false, false, true)]
    [InlineData(AttemptStatus.Uncertain, false, false, true)]
    [InlineData(AttemptStatus.Waiting, false, true, true)]
    [InlineData(AttemptStatus.Queued, false, true, true)]
    [InlineData(AttemptStatus.Succeeded, true, false, true)]
    [InlineData(AttemptStatus.Failed, true, false, true)]
    [InlineData(AttemptStatus.Cancelled, true, false, true)]
    [InlineData(AttemptStatus.Queued, false, false, false)]
    [InlineData(AttemptStatus.Waiting, false, false, false)]
    [InlineData(AttemptStatus.Succeeded, false, false, false)]
    [InlineData(AttemptStatus.Failed, false, false, false)]
    [InlineData(AttemptStatus.Cancelled, false, false, false)]
    public void ReservationsRetainOwnershipUntilExecutionAndCleanupReleaseIt(
        AttemptStatus status, bool cleanup, bool retained, bool expected)
    {
        var row = new AttemptRow
        {
            Status = status.ToString(), CleanupPending = cleanup,
            CleanupFailed = cleanup, WorkspaceRetained = retained
        };
        Assert.Equal(expected, ResourceReservations.Attempts(new[] { row }.AsQueryable()).Any());
    }

    [Fact]
    public void InspectionReservationsAgreeWithLifecycleCapacityRules()
    {
        foreach (InspectionState state in Enum.GetValues<InspectionState>())
        {
            var row = new SessionRow { State = state.ToString() };
            Assert.Equal(WorkspaceSessionRules.HoldsCapacity(state),
                ResourceReservations.Inspections(new[] { row }.AsQueryable()).Any());
        }
    }

    [Fact]
    public void InspectionQueriesAgreeWithEachLifecycleMeaning()
    {
        foreach (InspectionState state in Enum.GetValues<InspectionState>())
        {
            var row = new SessionRow { State = state.ToString() };
            IQueryable<SessionRow> sessions = new[] { row }.AsQueryable();
            Assert.Equal(WorkspaceSessionRules.IsActive(state),
                WorkspaceSessionQueries.Active(sessions).Any());
            Assert.Equal(WorkspaceSessionRules.RequiresObservation(state),
                WorkspaceSessionQueries.RequiringObservation(sessions).Any());
        }
    }

    [Fact]
    public void ConnectionReservationsIncludeTextAttemptsButSandboxCountsExcludeThem()
    {
        var rows = new[]
        {
            new AttemptRow { Id = 1, ConnectionId = 7, Status = "Running" },
            new AttemptRow { Id = 2, ConnectionId = 8, GithubConnectionId = 9, Status = "Waiting", WorkspaceRetained = true },
            new AttemptRow { Id = 3, ConnectionId = 8, GithubConnectionId = 9, Status = "Queued" },
            new AttemptRow { Id = 4, ConnectionId = 8, GithubConnectionId = 9, Status = "Failed", CleanupPending = true }
        }.AsQueryable();
        var reservations = ResourceReservations.Attempts(rows);
        Assert.Equal(new long[] { 1 }, reservations.Where(x => x.ConnectionId == 7).Select(x => x.Id));
        Assert.Equal(new long[] { 2, 4 }, reservations.Where(x => x.GithubConnectionId != null).Select(x => x.Id));
        Assert.Equal(new long[] { 4 }, reservations.Where(x => x.Id != 2 && x.GithubConnectionId != null).Select(x => x.Id));
    }

    [Fact]
    public void ReservationQueriesComposeAndTranslateToPostgresWithoutClientEvaluation()
    {
        using var db = new GoblinDbContext(new DbContextOptionsBuilder<GoblinDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused").Options);
        string attempts = ResourceReservations.Attempts(db.ExecutionAttempts)
            .Where(x => x.Id != 42 && x.GithubConnectionId != null && x.ConnectionId == 7).ToQueryString();
        Assert.Contains("workspace_retained", attempts);
        Assert.Contains("cleanup_pending", attempts);
        Assert.Contains("IS NOT NULL", attempts);
        Assert.Contains("42", attempts);
        string inspections = ResourceReservations.Inspections(db.WorkspaceSessions).ToQueryString();
        Assert.Contains("NeedsAttention", inspections);
        Assert.DoesNotContain("Queued", inspections);
        string active = WorkspaceSessionQueries.Active(db.WorkspaceSessions)
            .Where(x => x.WorkId == 42).ToQueryString();
        Assert.Contains("NeedsAttention", active);
        Assert.Contains("Queued", active);
        Assert.Contains("42", active);
        string observable = WorkspaceSessionQueries.RequiringObservation(db.WorkspaceSessions).ToQueryString();
        Assert.Contains("Queued", observable);
        Assert.DoesNotContain("NeedsAttention", observable);
    }
}
