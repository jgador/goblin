using Goblin.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Goblin.Persistence;

public partial class GoblinDbContext
{
    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        // Reverse engineering infers one-to-one from the filtered unique index.
        // Only ACTIVE attempts are unique; a connection has many historical
        // attempts. Keep this correction outside regenerated mappings.
        modelBuilder.Entity<Connection>().Ignore(x => x.ExecutionAttempt);
        modelBuilder.Entity<ExecutionAttempt>().HasOne(x => x.Connection).WithMany()
            .HasForeignKey(x => x.ConnectionId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ExecutionAttempt>().Ignore(x => x.RepositoryOperation);
        modelBuilder.Entity<RepositoryOperation>().HasOne(x => x.Attempt).WithMany()
            .HasForeignKey(x => x.AttemptId).OnDelete(DeleteBehavior.Restrict);
    }
}
