using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Goblin.Persistence.Entities;

[Table("execution_attempts")]
[Index("Status", "UpdatedAt", Name = "attempts_recovery")]
public partial class ExecutionAttempt
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("work_id")]
    public long WorkId { get; set; }

    [Column("agent_id")]
    public long AgentId { get; set; }

    [Column("connection_id")]
    public long ConnectionId { get; set; }

    [Column("runtime")]
    public string Runtime { get; set; } = null!;

    [Column("status")]
    public string Status { get; set; } = null!;

    [Column("owner_id")]
    public long? OwnerId { get; set; }

    [Column("environment_reference")]
    public string? EnvironmentReference { get; set; }

    [Column("queued_at")]
    public DateTime QueuedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }

    [Column("cleanup_pending")]
    public bool CleanupPending { get; set; }

    [Column("cleanup_failed")]
    public bool CleanupFailed { get; set; }

    [Column("github_connection_id")]
    public long? GithubConnectionId { get; set; }

    [Column("turn_number")]
    public int TurnNumber { get; set; }

    [Column("workspace_retained")]
    public bool WorkspaceRetained { get; set; }

    [ForeignKey("AgentId")]
    [InverseProperty("ExecutionAttempts")]
    public virtual Agent Agent { get; set; } = null!;

    [ForeignKey("ConnectionId")]
    [InverseProperty("ExecutionAttempt")]
    public virtual Connection Connection { get; set; } = null!;

    [ForeignKey("GithubConnectionId")]
    [InverseProperty("ExecutionAttempts")]
    public virtual GithubConnection? GithubConnection { get; set; }

    [InverseProperty("Attempt")]
    public virtual RepositoryOperation? RepositoryOperation { get; set; }

    [ForeignKey("WorkId")]
    [InverseProperty("ExecutionAttempts")]
    public virtual WorkItem Work { get; set; } = null!;

    [InverseProperty("Attempt")]
    public virtual ICollection<WorkspaceCheckpoint> WorkspaceCheckpoints { get; set; } = [];

    [InverseProperty("Attempt")]
    public virtual ICollection<WorkspaceSession> WorkspaceSessions { get; set; } = [];
}
