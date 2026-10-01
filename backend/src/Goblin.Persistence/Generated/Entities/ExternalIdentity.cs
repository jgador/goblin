using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Goblin.Persistence.Entities;

[Table("external_identities")]
[Index("InstallationId", "WorkspaceId", "UserId", Name = "external_identities_installation_id_workspace_id_user_id_key", IsUnique = true)]
public partial class ExternalIdentity
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("installation_id")]
    public string InstallationId { get; set; } = null!;

    [Column("workspace_id")]
    public string WorkspaceId { get; set; } = null!;

    [Column("user_id")]
    public string UserId { get; set; } = null!;

    [Column("local_actor")]
    public string LocalActor { get; set; } = null!;

    [Column("enabled")]
    public bool Enabled { get; set; }
}
