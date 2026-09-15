using System;
using System.Collections.Generic;
using Goblin.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Goblin.Persistence;

public partial class GoblinDbContext : DbContext
{
    public GoblinDbContext(DbContextOptions<GoblinDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<WorkItem> WorkItems { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkItem>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("work_items_pkey");

            entity.Property(e => e.Id).ValueGeneratedNever();
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
