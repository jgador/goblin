using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Goblin.Persistence.Entities;

[Table("agents")]
public partial class Agent
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = null!;

    [Column("connection_id")]
    public Guid ConnectionId { get; set; }

    [Column("model")]
    public string? Model { get; set; }

    [ForeignKey("ConnectionId")]
    [InverseProperty("Agents")]
    public virtual Connection Connection { get; set; } = null!;

    [InverseProperty("Agent")]
    public virtual ICollection<ExecutionAttempt> ExecutionAttempts { get; set; } = new List<ExecutionAttempt>();

    [InverseProperty("Agent")]
    public virtual ICollection<WorkItem> WorkItems { get; set; } = new List<WorkItem>();
}
