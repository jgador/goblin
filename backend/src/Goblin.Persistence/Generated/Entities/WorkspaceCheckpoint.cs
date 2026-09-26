using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Goblin.Persistence.Entities;

[Table("workspace_checkpoints")]
[Index("AttemptId", "TurnNumber", Name = "workspace_checkpoints_attempt_id_turn_number_key", IsUnique = true)]
public partial class WorkspaceCheckpoint
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("work_id")]
    public long WorkId { get; set; }

    [Column("attempt_id")]
    public long AttemptId { get; set; }

    [Column("turn_number")]
    public int TurnNumber { get; set; }

    [Column("workspace_number")]
    public int WorkspaceNumber { get; set; }

    [Column("repository")]
    public string Repository { get; set; } = null!;

    [Column("branch")]
    public string Branch { get; set; } = null!;

    [Column("commit_sha")]
    public string CommitSha { get; set; } = null!;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [ForeignKey("AttemptId")]
    [InverseProperty("WorkspaceCheckpoints")]
    public virtual ExecutionAttempt Attempt { get; set; } = null!;

    [InverseProperty("Checkpoint")]
    public virtual ICollection<RepositorySetupMemory> RepositorySetupMemories { get; set; } = [];

    [ForeignKey("WorkId")]
    [InverseProperty("WorkspaceCheckpoints")]
    public virtual WorkItem Work { get; set; } = null!;
}
