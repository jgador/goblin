using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Goblin.Persistence.Entities;

[Table("chatgpt_grant_event")]
[Index("GrantId", "Id", Name = "chatgpt_grant_event_grant")]
public partial class ChatgptGrantEvent
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("grant_id")]
    public long GrantId { get; set; }

    [Column("action")]
    public string Action { get; set; } = null!;

    [Column("actor")]
    public string Actor { get; set; } = null!;

    [Column("occurred_at")]
    public DateTime OccurredAt { get; set; }

    [ForeignKey("GrantId")]
    [InverseProperty("ChatgptGrantEvents")]
    public virtual ChatgptWorkGrant Grant { get; set; } = null!;
}
