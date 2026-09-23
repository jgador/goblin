using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Goblin.Persistence.Entities;

[Table("repository_setup_memories")]
[Index("AttemptId", "TurnNumber", "Topic", Name = "repository_setup_memories_attempt_id_turn_number_topic_key", IsUnique = true)]
[Index("GithubConnectionId", "AccountId", "RepositoryId", "VerifiedAt", Name = "repository_setup_recall", IsDescending = new[] { false, false, false, true })]
public partial class RepositorySetupMemory
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("repository_id")]
    public long RepositoryId { get; set; }

    [Column("github_connection_id")]
    public long GithubConnectionId { get; set; }

    [Column("account_id")]
    public string AccountId { get; set; } = null!;

    [Column("checkpoint_id")]
    public long CheckpointId { get; set; }

    [Column("work_id")]
    public long WorkId { get; set; }

    [Column("attempt_id")]
    public long AttemptId { get; set; }

    [Column("turn_number")]
    public int TurnNumber { get; set; }

    [Column("topic")]
    public string Topic { get; set; } = null!;

    [Column("environment")]
    public string Environment { get; set; } = null!;

    [Column("fingerprint")]
    public string Fingerprint { get; set; } = null!;

    [Column("observation", TypeName = "jsonb")]
    public string Observation { get; set; } = null!;

    [Column("verified_at")]
    public DateTime VerifiedAt { get; set; }

    [ForeignKey("AttemptId")]
    [InverseProperty("RepositorySetupMemories")]
    public virtual ExecutionAttempt Attempt { get; set; } = null!;

    [ForeignKey("CheckpointId")]
    [InverseProperty("RepositorySetupMemories")]
    public virtual WorkspaceCheckpoint Checkpoint { get; set; } = null!;

    [ForeignKey("GithubConnectionId")]
    [InverseProperty("RepositorySetupMemories")]
    public virtual GithubConnection GithubConnection { get; set; } = null!;

    [ForeignKey("RepositoryId")]
    [InverseProperty("RepositorySetupMemories")]
    public virtual GithubRepository Repository { get; set; } = null!;

    [ForeignKey("WorkId")]
    [InverseProperty("RepositorySetupMemories")]
    public virtual WorkItem Work { get; set; } = null!;
}
