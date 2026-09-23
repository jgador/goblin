using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Goblin.Persistence.Entities;

[Table("work_items")]
[Index("UpdatedAt", "Id", Name = "work_items_updated", IsDescending = new[] { true, false })]
public partial class WorkItem
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("objective")]
    public string Objective { get; set; } = null!;

    [Column("state", TypeName = "jsonb")]
    public string? State { get; set; }

    [Column("version")]
    public long Version { get; set; }

    [Column("status")]
    public string Status { get; set; } = null!;

    [Column("agent_id")]
    public long? AgentId { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }

    [ForeignKey("AgentId")]
    [InverseProperty("WorkItems")]
    public virtual Agent? Agent { get; set; }

    [InverseProperty("Work")]
    public virtual ICollection<Conversation> Conversations { get; set; } = [];

    [InverseProperty("Work")]
    public virtual ICollection<ExecutionAttempt> ExecutionAttempts { get; set; } = [];

    [InverseProperty("Work")]
    public virtual ICollection<RepositorySetupMemory> RepositorySetupMemories { get; set; } = [];

    [InverseProperty("Work")]
    public virtual ICollection<WorkCommand> WorkCommands { get; set; } = [];

    [InverseProperty("Work")]
    public virtual ICollection<WorkspaceCheckpoint> WorkspaceCheckpoints { get; set; } = [];

    [InverseProperty("Work")]
    public virtual WorkspaceSession? WorkspaceSession { get; set; }
}
