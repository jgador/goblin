using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Goblin.Persistence.Entities;

[Table("external_messages")]
[Index("InstallationId", "EventId", Name = "external_messages_installation_id_event_id_key", IsUnique = true)]
[Index("InstallationId", "WorkspaceId", "ChannelId", "MessageId", Name = "external_messages_installation_id_workspace_id_channel_id_m_key", IsUnique = true)]
public partial class ExternalMessage
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("installation_id")]
    public string InstallationId { get; set; } = null!;

    [Column("workspace_id")]
    public string WorkspaceId { get; set; } = null!;

    [Column("app_id")]
    public string AppId { get; set; } = null!;

    [Column("event_id")]
    public string EventId { get; set; } = null!;

    [Column("user_id")]
    public string UserId { get; set; } = null!;

    [Column("channel_id")]
    public string ChannelId { get; set; } = null!;

    [Column("thread_id")]
    public string ThreadId { get; set; } = null!;

    [Column("message_id")]
    public string MessageId { get; set; } = null!;

    [Column("body")]
    public string? Body { get; set; }

    [Column("state")]
    public string State { get; set; } = null!;

    [Column("conversation_message_id")]
    public long? ConversationMessageId { get; set; }

    [Column("work_id")]
    public long? WorkId { get; set; }

    [Column("received_at")]
    public DateTime ReceivedAt { get; set; }

    [ForeignKey("ConversationMessageId")]
    [InverseProperty("ExternalMessages")]
    public virtual ConversationMessage? ConversationMessage { get; set; }

    [ForeignKey("WorkId")]
    [InverseProperty("ExternalMessages")]
    public virtual WorkItem? Work { get; set; }
}
