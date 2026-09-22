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

    public virtual DbSet<Agent> Agents { get; set; }

    public virtual DbSet<Connection> Connections { get; set; }

    public virtual DbSet<Conversation> Conversations { get; set; }

    public virtual DbSet<ConversationMessage> ConversationMessages { get; set; }

    public virtual DbSet<ExecutionAttempt> ExecutionAttempts { get; set; }

    public virtual DbSet<GithubConnection> GithubConnections { get; set; }

    public virtual DbSet<GithubRepository> GithubRepositories { get; set; }

    public virtual DbSet<RepositoryOperation> RepositoryOperations { get; set; }

    public virtual DbSet<WorkCommand> WorkCommands { get; set; }

    public virtual DbSet<WorkItem> WorkItems { get; set; }

    public virtual DbSet<WorkspaceCheckpoint> WorkspaceCheckpoints { get; set; }

    public virtual DbSet<WorkspaceSession> WorkspaceSessions { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Agent>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("agents_pkey");

            entity.HasOne(d => d.Connection).WithMany(p => p.Agents)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("agents_connection_id_fkey");
        });

        modelBuilder.Entity<Connection>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("connections_pkey");

            entity.Property(e => e.Availability).HasDefaultValueSql("'Disconnected'::text");
            entity.Property(e => e.ChangedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<Conversation>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("conversations_pkey");

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Work).WithMany(p => p.Conversations).HasConstraintName("conversations_work_id_fkey");
        });

        modelBuilder.Entity<ConversationMessage>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("conversation_messages_pkey");

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Conversation).WithMany(p => p.ConversationMessages)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("conversation_messages_conversation_id_fkey");
        });

        modelBuilder.Entity<ExecutionAttempt>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("execution_attempts_pkey");

            entity.HasIndex(e => e.ConnectionId, "one_execution_per_connection")
                .IsUnique()
                .HasFilter("((status = ANY (ARRAY['Starting'::text, 'Running'::text, 'CancellationRequested'::text, 'Uncertain'::text])) OR cleanup_pending OR workspace_retained)");

            entity.Property(e => e.TurnNumber).HasDefaultValue(1);

            entity.HasOne(d => d.Agent).WithMany(p => p.ExecutionAttempts)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("execution_attempts_agent_id_fkey");

            entity.HasOne(d => d.Connection).WithOne(p => p.ExecutionAttempt)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("execution_attempts_connection_id_fkey");

            entity.HasOne(d => d.GithubConnection).WithMany(p => p.ExecutionAttempts).HasConstraintName("execution_attempts_github_connection_id_fkey");

            entity.HasOne(d => d.Work).WithMany(p => p.ExecutionAttempts)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("execution_attempts_work_id_fkey");
        });

        modelBuilder.Entity<GithubConnection>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("github_connections_pkey");

            entity.Property(e => e.Availability).HasDefaultValueSql("'Disconnected'::text");
        });

        modelBuilder.Entity<GithubRepository>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("github_repositories_pkey");

            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Enabled).HasDefaultValue(true);

            entity.HasOne(d => d.Connection).WithMany(p => p.GithubRepositories)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("github_repositories_connection_id_fkey");
        });

        modelBuilder.Entity<RepositoryOperation>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("repository_operations_pkey");

            entity.HasIndex(e => e.AttemptId, "one_repository_operation")
                .IsUnique()
                .HasFilter("(state = ANY (ARRAY['Queued'::text, 'Running'::text, 'Uncertain'::text]))");

            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Attempt).WithOne(p => p.RepositoryOperation)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("repository_operations_attempt_id_fkey");
        });

        modelBuilder.Entity<WorkCommand>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("work_commands_pkey");

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Work).WithMany(p => p.WorkCommands)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("work_commands_work_id_fkey");
        });

        modelBuilder.Entity<WorkItem>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("work_items_pkey");

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.Status).HasDefaultValueSql("'Ready'::text");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.Version).HasDefaultValue(1L);

            entity.HasOne(d => d.Agent).WithMany(p => p.WorkItems).HasConstraintName("work_items_agent_id_fkey");
        });

        modelBuilder.Entity<WorkspaceCheckpoint>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("workspace_checkpoints_pkey");

            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Attempt).WithMany(p => p.WorkspaceCheckpoints)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("workspace_checkpoints_attempt_id_fkey");

            entity.HasOne(d => d.Work).WithMany(p => p.WorkspaceCheckpoints)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("workspace_checkpoints_work_id_fkey");
        });

        modelBuilder.Entity<WorkspaceSession>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("workspace_sessions_pkey");

            entity.HasIndex(e => e.WorkId, "one_inspection_per_work")
                .IsUnique()
                .HasFilter("(state = ANY (ARRAY['Queued'::text, 'Starting'::text, 'Available'::text, 'Stopping'::text]))");

            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Attempt).WithMany(p => p.WorkspaceSessions)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("workspace_sessions_attempt_id_fkey");

            entity.HasOne(d => d.Checkpoint).WithMany(p => p.WorkspaceSessions).HasConstraintName("workspace_sessions_checkpoint_id_fkey");

            entity.HasOne(d => d.Work).WithOne(p => p.WorkspaceSession)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("workspace_sessions_work_id_fkey");
        });
        modelBuilder.HasSequence("work_event_ids");

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
