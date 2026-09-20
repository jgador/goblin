using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Goblin.Persistence.Entities;

[Table("conversation_messages")]
[Index("ConversationId", "CreatedAt", "Id", Name = "conversation_history")]
public partial class ConversationMessage
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("conversation_id")]
    public long ConversationId { get; set; }

    [Column("body")]
    public string Body { get; set; } = null!;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [ForeignKey("ConversationId")]
    [InverseProperty("ConversationMessages")]
    public virtual Conversation Conversation { get; set; } = null!;
}
