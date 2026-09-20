using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Goblin.Persistence.Entities;

[Table("work_commands")]
public partial class WorkCommand
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("work_id")]
    public long WorkId { get; set; }

    [Column("fingerprint")]
    public string Fingerprint { get; set; } = null!;

    [Column("response", TypeName = "jsonb")]
    public string Response { get; set; } = null!;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [ForeignKey("WorkId")]
    [InverseProperty("WorkCommands")]
    public virtual WorkItem Work { get; set; } = null!;
}
