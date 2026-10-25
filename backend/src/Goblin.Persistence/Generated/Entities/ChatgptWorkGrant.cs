using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Goblin.Persistence.Entities;

[Table("chatgpt_work_grant")]
public partial class ChatgptWorkGrant
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("issuer")]
    [StringLength(2048)]
    public string Issuer { get; set; } = null!;

    [Column("subject")]
    [StringLength(256)]
    public string Subject { get; set; } = null!;

    [Column("resource")]
    [StringLength(2048)]
    public string Resource { get; set; } = null!;

    [Column("work_id")]
    public long WorkId { get; set; }

    [Column("result_attempt_id")]
    public long? ResultAttemptId { get; set; }

    [Column("approved_at")]
    public DateTime ApprovedAt { get; set; }

    [Column("expires_at")]
    public DateTime ExpiresAt { get; set; }

    [Column("revoked_at")]
    public DateTime? RevokedAt { get; set; }

    [InverseProperty("Grant")]
    public virtual ICollection<ChatgptGrantEvent> ChatgptGrantEvents { get; set; } = [];

    [ForeignKey("WorkId")]
    [InverseProperty("ChatgptWorkGrants")]
    public virtual WorkItem Work { get; set; } = null!;
}
