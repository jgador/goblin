using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Goblin.Persistence.Entities;

[Table("connections")]
public partial class Connection
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("runtime")]
    public string Runtime { get; set; } = null!;

    [Column("name")]
    public string Name { get; set; } = null!;

    [Column("availability")]
    public string Availability { get; set; } = null!;

    [Column("account_signature")]
    public string? AccountSignature { get; set; }

    [Column("auth_generation")]
    public long AuthGeneration { get; set; }

    [Column("changed_at")]
    public DateTime ChangedAt { get; set; }

    [InverseProperty("Connection")]
    public virtual ICollection<Agent> Agents { get; set; } = [];

    [InverseProperty("Connection")]
    public virtual ConnectionModelCatalog? ConnectionModelCatalog { get; set; }

    [InverseProperty("Connection")]
    public virtual ExecutionAttempt? ExecutionAttempt { get; set; }
}
