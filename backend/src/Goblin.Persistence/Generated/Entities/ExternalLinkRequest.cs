using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Goblin.Persistence.Entities;

[Table("external_link_requests")]
public partial class ExternalLinkRequest
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("installation_id")]
    public string InstallationId { get; set; } = null!;

    [Column("workspace_id")]
    public string WorkspaceId { get; set; } = null!;

    [Column("session_id")]
    public string SessionId { get; set; } = null!;

    [Column("code_hash")]
    public string CodeHash { get; set; } = null!;

    [Column("user_id")]
    public string? UserId { get; set; }

    [Column("expires_at")]
    public DateTime ExpiresAt { get; set; }

    [Column("consumed")]
    public bool Consumed { get; set; }
}
