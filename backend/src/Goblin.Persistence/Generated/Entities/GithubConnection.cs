using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Goblin.Persistence.Entities;

[Table("github_connections")]
public partial class GithubConnection
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("generation")]
    public string? Generation { get; set; }

    [Column("account_id")]
    public string? AccountId { get; set; }

    [Column("login")]
    public string? Login { get; set; }

    [Column("availability")]
    public string Availability { get; set; } = null!;

    [InverseProperty("GithubConnection")]
    public virtual ICollection<ExecutionAttempt> ExecutionAttempts { get; set; } = [];

    [InverseProperty("Connection")]
    public virtual ICollection<GithubRepository> GithubRepositories { get; set; } = [];

    [InverseProperty("GithubConnection")]
    public virtual ICollection<RepositorySetupMemory> RepositorySetupMemories { get; set; } = [];
}
