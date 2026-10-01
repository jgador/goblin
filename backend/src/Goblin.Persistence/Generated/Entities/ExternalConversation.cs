using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Goblin.Persistence.Entities;

[Table("external_conversations")]
[Index("InstallationId", "WorkspaceId", "ChannelId", "ThreadId", Name = "external_conversations_installation_id_workspace_id_channel_key", IsUnique = true)]
public partial class ExternalConversation
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("installation_id")]
    public string InstallationId { get; set; } = null!;

    [Column("workspace_id")]
    public string WorkspaceId { get; set; } = null!;

    [Column("channel_id")]
    public string ChannelId { get; set; } = null!;

    [Column("thread_id")]
    public string ThreadId { get; set; } = null!;

    [Column("conversation_id")]
    public long ConversationId { get; set; }

    [ForeignKey("ConversationId")]
    [InverseProperty("ExternalConversations")]
    public virtual Conversation Conversation { get; set; } = null!;
}
