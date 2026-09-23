using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Goblin.Persistence.Entities;

[Table("repository_operations")]
public partial class RepositoryOperation
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("attempt_id")]
    public long AttemptId { get; set; }

    [Column("kind")]
    public string Kind { get; set; } = null!;

    [Column("state")]
    public string State { get; set; } = null!;

    [Column("fingerprint")]
    public string Fingerprint { get; set; } = null!;

    [Column("commit_sha")]
    public string? CommitSha { get; set; }

    [Column("result_url")]
    public string? ResultUrl { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }

    [ForeignKey("AttemptId")]
    [InverseProperty("RepositoryOperation")]
    public virtual ExecutionAttempt Attempt { get; set; } = null!;
}
