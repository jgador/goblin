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
    public Guid Id { get; set; }

    [Column("objective")]
    public string Objective { get; set; } = null!;

    [Column("state", TypeName = "jsonb")]
    public string? State { get; set; }

    [Column("version")]
    public long Version { get; set; }

    [Column("status")]
    public string Status { get; set; } = null!;

    [Column("agent_id")]
    public Guid? AgentId { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }

    [ForeignKey("AgentId")]
    [InverseProperty("WorkItems")]
    public virtual Agent? Agent { get; set; }

    [InverseProperty("Work")]
    public virtual ICollection<Conversation> Conversations { get; set; } = new List<Conversation>();

    [InverseProperty("Work")]
    public virtual ICollection<ExecutionAttempt> ExecutionAttempts { get; set; } = new List<ExecutionAttempt>();

    [InverseProperty("Work")]
    public virtual ICollection<WorkCommand> WorkCommands { get; set; } = new List<WorkCommand>();
}
