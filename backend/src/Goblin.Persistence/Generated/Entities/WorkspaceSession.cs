using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Goblin.Persistence.Entities;

[Table("workspace_sessions")]
public partial class WorkspaceSession
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("work_id")]
    public long WorkId { get; set; }

    [Column("attempt_id")]
    public long AttemptId { get; set; }

    [Column("source_volume")]
    public string SourceVolume { get; set; } = null!;

    [Column("state")]
    public string State { get; set; } = null!;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }

    [ForeignKey("AttemptId")]
    [InverseProperty("WorkspaceSessions")]
    public virtual ExecutionAttempt Attempt { get; set; } = null!;

    [ForeignKey("WorkId")]
    [InverseProperty("WorkspaceSession")]
    public virtual WorkItem Work { get; set; } = null!;
}
