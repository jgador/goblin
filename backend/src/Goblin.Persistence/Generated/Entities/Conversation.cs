using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Goblin.Persistence.Entities;

[Table("conversations")]
public partial class Conversation
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("title")]
    public string Title { get; set; } = null!;

    [Column("work_id")]
    public long? WorkId { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [InverseProperty("Conversation")]
    public virtual ICollection<ConversationMessage> ConversationMessages { get; set; } = [];

    [ForeignKey("WorkId")]
    [InverseProperty("Conversations")]
    public virtual WorkItem? Work { get; set; }
}
