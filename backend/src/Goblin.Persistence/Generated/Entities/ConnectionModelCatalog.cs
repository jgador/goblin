using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Goblin.Persistence.Entities;

[Table("connection_model_catalogs")]
[Index("ConnectionId", Name = "connection_model_catalogs_connection_id_key", IsUnique = true)]
public partial class ConnectionModelCatalog
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("connection_id")]
    public long ConnectionId { get; set; }

    [Column("auth_generation")]
    public long AuthGeneration { get; set; }

    [Column("executable_stamp")]
    public string ExecutableStamp { get; set; } = null!;

    [Column("catalog", TypeName = "jsonb")]
    public string? Catalog { get; set; }

    [Column("fetched_at")]
    public DateTime? FetchedAt { get; set; }

    [Column("retry_after")]
    public DateTime? RetryAfter { get; set; }

    [Column("refresh_failed")]
    public bool RefreshFailed { get; set; }

    [ForeignKey("ConnectionId")]
    [InverseProperty("ConnectionModelCatalog")]
    public virtual Connection Connection { get; set; } = null!;
}
