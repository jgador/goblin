using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Goblin.Persistence.Entities;

[Table("github_repositories")]
[Index("Name", Name = "github_repositories_name_key", IsUnique = true)]
public partial class GithubRepository
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("connection_id")]
    public long ConnectionId { get; set; }

    [Column("name")]
    public string Name { get; set; } = null!;

    [Column("default_branch")]
    public string DefaultBranch { get; set; } = null!;

    [Column("enabled")]
    public bool Enabled { get; set; }

    [ForeignKey("ConnectionId")]
    [InverseProperty("GithubRepositories")]
    public virtual GithubConnection Connection { get; set; } = null!;

    [InverseProperty("Repository")]
    public virtual ICollection<RepositorySetupMemory> RepositorySetupMemories { get; set; } = [];
}
