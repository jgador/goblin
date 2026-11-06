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

    public virtual DbSet<ConnectionModelCatalog> ConnectionModelCatalogs { get; set; }

    public virtual DbSet<Conversation> Conversations { get; set; }

    public virtual DbSet<ConversationMessage> ConversationMessages { get; set; }

    public virtual DbSet<ExecutionAttempt> ExecutionAttempts { get; set; }

    public virtual DbSet<ExternalConversation> ExternalConversations { get; set; }

    public virtual DbSet<ExternalIdentity> ExternalIdentities { get; set; }

    public virtual DbSet<ExternalLinkRequest> ExternalLinkRequests { get; set; }

    public virtual DbSet<ExternalMessage> ExternalMessages { get; set; }

    public virtual DbSet<GitRepositoryOperation> GitRepositoryOperations { get; set; }

    public virtual DbSet<GitRepositorySetupMemory> GitRepositorySetupMemories { get; set; }

    public virtual DbSet<GithubConnection> GithubConnections { get; set; }

    public virtual DbSet<GithubRepository> GithubRepositories { get; set; }

    public virtual DbSet<WorkCommand> WorkCommands { get; set; }

    public virtual DbSet<WorkItem> WorkItems { get; set; }

    public virtual DbSet<WorkspaceCheckpoint> WorkspaceCheckpoints { get; set; }

    public virtual DbSet<WorkspaceSession> WorkspaceSessions { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Agent>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("agent_pkey");

            entity.HasOne(d => d.Connection).WithMany(p => p.Agents)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("agent_connection_id_fkey");
        });

        modelBuilder.Entity<Connection>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("connection_pkey");

            entity.Property(e => e.Availability).HasDefaultValueSql("'Disconnected'::text");
            entity.Property(e => e.ChangedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<ConnectionModelCatalog>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("connection_model_catalog_pkey");

            entity.HasOne(d => d.Connection).WithOne(p => p.ConnectionModelCatalog)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("connection_model_catalog_connection_id_fkey");
        });

        modelBuilder.Entity<Conversation>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("conversation_pkey");

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Work).WithMany(p => p.Conversations).HasConstraintName("conversation_work_id_fkey");
        });

        modelBuilder.Entity<ConversationMessage>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("conversation_message_pkey");

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Conversation).WithMany(p => p.ConversationMessages)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("conversation_message_conversation_id_fkey");
        });

        modelBuilder.Entity<ExecutionAttempt>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("execution_attempt_pkey");

            entity.HasIndex(e => e.ConnectionId, "one_execution_per_connection")
                .IsUnique()
                .HasFilter("((status = ANY (ARRAY['Starting'::text, 'Running'::text, 'CancellationRequested'::text, 'Uncertain'::text])) OR cleanup_pending OR workspace_retained)");

            entity.Property(e => e.TurnNumber).HasDefaultValue(1);

            entity.HasOne(d => d.Agent).WithMany(p => p.ExecutionAttempts)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("execution_attempt_agent_id_fkey");

            entity.HasOne(d => d.Connection).WithOne(p => p.ExecutionAttempt)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("execution_attempt_connection_id_fkey");

            entity.HasOne(d => d.GithubConnection).WithMany(p => p.ExecutionAttempts).HasConstraintName("execution_attempt_github_connection_id_fkey");

            entity.HasOne(d => d.Work).WithMany(p => p.ExecutionAttempts)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("execution_attempt_work_id_fkey");
        });

        modelBuilder.Entity<ExternalConversation>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("external_conversation_pkey");

            entity.HasOne(d => d.Conversation).WithMany(p => p.ExternalConversations)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("external_conversation_conversation_id_fkey");
        });

        modelBuilder.Entity<ExternalIdentity>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("external_identity_pkey");

            entity.Property(e => e.Enabled).HasDefaultValue(true);
        });

        modelBuilder.Entity<ExternalLinkRequest>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("external_link_request_pkey");
        });

        modelBuilder.Entity<ExternalMessage>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("external_message_pkey");

            entity.HasIndex(e => new { e.InstallationId, e.Id }, "external_pending").HasFilter("(state = ANY (ARRAY['Pending'::text, 'PendingLink'::text]))");

            entity.Property(e => e.ReceivedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.State).HasDefaultValueSql("'Pending'::text");

            entity.HasOne(d => d.ConversationMessage).WithMany(p => p.ExternalMessages).HasConstraintName("external_message_conversation_message_id_fkey");

            entity.HasOne(d => d.Work).WithMany(p => p.ExternalMessages).HasConstraintName("external_message_work_id_fkey");
        });

        modelBuilder.Entity<GitRepositoryOperation>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("repository_operation_pkey");

            entity.HasIndex(e => e.AttemptId, "one_repository_operation")
                .IsUnique()
                .HasFilter("(state = ANY (ARRAY['Queued'::text, 'Running'::text, 'Uncertain'::text]))");

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Attempt).WithOne(p => p.GitRepositoryOperation)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("repository_operation_attempt_id_fkey");
        });

        modelBuilder.Entity<GitRepositorySetupMemory>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("repository_setup_memory_pkey");

            entity.Property(e => e.VerifiedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Attempt).WithMany(p => p.GitRepositorySetupMemories)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("repository_setup_memory_attempt_id_fkey");

            entity.HasOne(d => d.Checkpoint).WithMany(p => p.GitRepositorySetupMemories)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("repository_setup_memory_checkpoint_id_fkey");

            entity.HasOne(d => d.GitRepository).WithMany(p => p.GitRepositorySetupMemories)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("repository_setup_memory_repository_id_fkey");

            entity.HasOne(d => d.GithubConnection).WithMany(p => p.GitRepositorySetupMemories)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("repository_setup_memory_github_connection_id_fkey");

            entity.HasOne(d => d.Work).WithMany(p => p.GitRepositorySetupMemories)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("repository_setup_memory_work_id_fkey");
        });

        modelBuilder.Entity<GithubConnection>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("github_connection_pkey");

            entity.Property(e => e.Availability).HasDefaultValueSql("'Disconnected'::text");
        });

        modelBuilder.Entity<GithubRepository>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("github_repository_pkey");

            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Enabled).HasDefaultValue(true);

            entity.HasOne(d => d.Connection).WithMany(p => p.GithubRepositories)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("github_repository_connection_id_fkey");
        });

        modelBuilder.Entity<WorkCommand>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("work_command_pkey");

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Work).WithMany(p => p.WorkCommands)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("work_command_work_id_fkey");
        });

        modelBuilder.Entity<WorkItem>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("work_item_pkey");

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.Status).HasDefaultValueSql("'Ready'::text");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.Version).HasDefaultValue(1L);

            entity.HasOne(d => d.Agent).WithMany(p => p.WorkItems).HasConstraintName("work_item_agent_id_fkey");
        });

        modelBuilder.Entity<WorkspaceCheckpoint>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("workspace_checkpoint_pkey");

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Attempt).WithMany(p => p.WorkspaceCheckpoints)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("workspace_checkpoint_attempt_id_fkey");

            entity.HasOne(d => d.Work).WithMany(p => p.WorkspaceCheckpoints)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("workspace_checkpoint_work_id_fkey");
        });

        modelBuilder.Entity<WorkspaceSession>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("workspace_session_pkey");

            entity.HasIndex(e => e.WorkId, "one_inspection_per_work")
                .IsUnique()
                .HasFilter("(state = ANY (ARRAY['Queued'::text, 'Starting'::text, 'Available'::text, 'Stopping'::text]))");

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Attempt).WithMany(p => p.WorkspaceSessions)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("workspace_session_attempt_id_fkey");

            entity.HasOne(d => d.Work).WithOne(p => p.WorkspaceSession)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("workspace_session_work_id_fkey");
        });
        modelBuilder.HasSequence("work_event_id_seq");

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
