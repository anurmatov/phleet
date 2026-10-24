using Fleet.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Fleet.Orchestrator.Data;

public class OrchestratorDbContext(DbContextOptions<OrchestratorDbContext> options) : DbContext(options)
{
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<AgentTool> AgentTools => Set<AgentTool>();
    public DbSet<AgentProject> AgentProjects => Set<AgentProject>();
    public DbSet<AgentMcpEndpoint> AgentMcpEndpoints => Set<AgentMcpEndpoint>();
    public DbSet<Instruction> Instructions => Set<Instruction>();
    public DbSet<InstructionVersion> InstructionVersions => Set<InstructionVersion>();
    public DbSet<AgentInstruction> AgentInstructions => Set<AgentInstruction>();
    public DbSet<AgentEnvRef> AgentEnvRefs => Set<AgentEnvRef>();
    public DbSet<AgentTelegramUser> AgentTelegramUsers => Set<AgentTelegramUser>();
    public DbSet<AgentTelegramGroup> AgentTelegramGroups => Set<AgentTelegramGroup>();
    public DbSet<AgentNetwork> AgentNetworks => Set<AgentNetwork>();

    // Project Contexts
    public DbSet<ProjectContext> ProjectContexts => Set<ProjectContext>();
    public DbSet<ProjectContextVersion> ProjectContextVersions => Set<ProjectContextVersion>();

    // Repositories
    public DbSet<Repository> Repositories => Set<Repository>();

    // Universal Workflow Engine
    public DbSet<WorkflowDefinition> WorkflowDefinitions => Set<WorkflowDefinition>();
    public DbSet<WorkflowDefinitionVersion> WorkflowDefinitionVersions => Set<WorkflowDefinitionVersion>();

    // Credential Files
    public DbSet<CredentialFile> CredentialFiles => Set<CredentialFile>();
    public DbSet<AgentCredentialMount> AgentCredentialMounts => Set<AgentCredentialMount>();

    // Credentials Audit
    public DbSet<CredentialsAudit> CredentialsAudit => Set<CredentialsAudit>();

    // Agent Project Access (memory ACL)
    public DbSet<AgentProjectAccess> AgentProjectAccess => Set<AgentProjectAccess>();

    // Named output styles (per-agent tone/register, rendered per provider)
    public DbSet<OutputStyle> OutputStyles => Set<OutputStyle>();

    // Epic grants (#436): human-approved grant scopes and the permanent per-visit decision record.
    public DbSet<EpicGrant> EpicGrants => Set<EpicGrant>();
    public DbSet<EpicGrantBinding> EpicGrantBindings => Set<EpicGrantBinding>();
    public DbSet<EpicGrantRun> EpicGrantRuns => Set<EpicGrantRun>();
    public DbSet<EpicGrantDecision> EpicGrantDecisions => Set<EpicGrantDecision>();

    // Workflow failure accountability (#476)
    public DbSet<WorkflowOutcomeRun> WorkflowOutcomeRuns => Set<WorkflowOutcomeRun>();
    public DbSet<WorkflowOutcomeObservation> WorkflowOutcomeObservations => Set<WorkflowOutcomeObservation>();
    public DbSet<WorkflowOutcomeCheckpoint> WorkflowOutcomeCheckpoints => Set<WorkflowOutcomeCheckpoint>();
    public DbSet<WorkflowOutcomeBatch> WorkflowOutcomeBatches => Set<WorkflowOutcomeBatch>();
    public DbSet<WorkflowOutcomeBatchItem> WorkflowOutcomeBatchItems => Set<WorkflowOutcomeBatchItem>();
    public DbSet<WorkflowOutcomeDecision> WorkflowOutcomeDecisions => Set<WorkflowOutcomeDecision>();
    public DbSet<WorkflowRecoveryOperation> WorkflowRecoveryOperations => Set<WorkflowRecoveryOperation>();
    public DbSet<WorkflowOutcomeLateResult> WorkflowOutcomeLateResults => Set<WorkflowOutcomeLateResult>();
    public DbSet<WorkflowOutcomeHealth> WorkflowOutcomeHealth => Set<WorkflowOutcomeHealth>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Agent>(e =>
        {
            e.ToTable("agents");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Role).HasMaxLength(100).IsRequired();
            e.Property(x => x.Model).HasMaxLength(100).IsRequired();
            e.Property(x => x.Provider).HasMaxLength(20).HasDefaultValue("claude");
            e.Property(x => x.ContainerName).HasMaxLength(200).IsRequired();
            e.Property(x => x.PermissionMode).HasMaxLength(50).HasDefaultValue("acceptEdits");
            e.Property(x => x.MaxTurns).HasDefaultValue(50);
            e.Property(x => x.WarmupTimeoutSeconds).HasDefaultValue(WarmupTimeout.DefaultSeconds);
            e.Property(x => x.WorkDir).HasMaxLength(500).HasDefaultValue("/workspace");
            e.Property(x => x.GroupListenMode).HasMaxLength(50).HasDefaultValue("mention");
            e.Property(x => x.ShortName).HasMaxLength(100).HasDefaultValue("");
            e.Property(x => x.Image).HasMaxLength(200);
            e.Property(x => x.Effort).HasMaxLength(20);
            e.Property(x => x.CodexSandboxMode).HasMaxLength(30);
            // Not a relationship on purpose — see Agent.OutputStyle.
            e.Property(x => x.OutputStyle).HasMaxLength(100);
            // #382: property renamed, column kept, so there is no migration and rollback reads it.
            e.Property(x => x.LocalBaseUrl).HasColumnName("AnthropicBaseUrl").HasMaxLength(500);
            e.Property(x => x.AutoMemoryEnabled).HasDefaultValue(true);
            e.Property(x => x.JournalEnabled).HasColumnName("journal_enabled").HasDefaultValue(false);
            e.Property(x => x.JournalCrossChatEnabled).HasColumnName("journal_cross_chat_enabled").HasDefaultValue(false);
            e.Property(x => x.HarnessExtra).HasColumnName("harness_extra").HasColumnType("text");
            e.Property(x => x.CanReceiveChatRequests).HasDefaultValue(false);
            e.Property(x => x.RequestReceivedMessage).HasMaxLength(500);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.IsEnabled);
        });

        modelBuilder.Entity<AgentTelegramUser>(e =>
        {
            e.ToTable("agent_telegram_users");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.AgentId, x.UserId }).IsUnique();
            e.HasOne(x => x.Agent)
             .WithMany(a => a.TelegramUsers)
             .HasForeignKey(x => x.AgentId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentTelegramGroup>(e =>
        {
            e.ToTable("agent_telegram_groups");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.AgentId, x.GroupId }).IsUnique();
            e.HasOne(x => x.Agent)
             .WithMany(a => a.TelegramGroups)
             .HasForeignKey(x => x.AgentId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentNetwork>(e =>
        {
            e.ToTable("agent_networks");
            e.HasKey(x => x.Id);
            e.Property(x => x.NetworkName).HasMaxLength(200).IsRequired();
            e.HasIndex(x => new { x.AgentId, x.NetworkName }).IsUnique();
            e.HasOne(x => x.Agent)
             .WithMany(a => a.Networks)
             .HasForeignKey(x => x.AgentId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentTool>(e =>
        {
            e.ToTable("agent_tools");
            e.HasKey(x => x.Id);
            e.Property(x => x.ToolName).HasMaxLength(200).IsRequired();
            e.HasIndex(x => new { x.AgentId, x.ToolName }).IsUnique();
            e.HasOne(x => x.Agent)
             .WithMany(a => a.Tools)
             .HasForeignKey(x => x.AgentId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentProject>(e =>
        {
            e.ToTable("agent_projects");
            e.HasKey(x => x.Id);
            e.Property(x => x.ProjectName).HasMaxLength(200).IsRequired();
            e.HasIndex(x => new { x.AgentId, x.ProjectName }).IsUnique();
            e.HasOne(x => x.Agent)
             .WithMany(a => a.Projects)
             .HasForeignKey(x => x.AgentId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentMcpEndpoint>(e =>
        {
            e.ToTable("agent_mcp_endpoints");
            e.HasKey(x => x.Id);
            e.Property(x => x.McpName).HasMaxLength(100).IsRequired();
            e.Property(x => x.Url).HasMaxLength(500).IsRequired();
            e.Property(x => x.TransportType).HasMaxLength(50).IsRequired();
            e.HasIndex(x => new { x.AgentId, x.McpName }).IsUnique();
            e.HasOne(x => x.Agent)
             .WithMany(a => a.McpEndpoints)
             .HasForeignKey(x => x.AgentId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Instruction>(e =>
        {
            e.ToTable("instructions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<InstructionVersion>(e =>
        {
            e.ToTable("instruction_versions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Content).HasColumnType("longtext").IsRequired();
            e.Property(x => x.CreatedBy).HasMaxLength(100);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.HasIndex(x => new { x.InstructionId, x.VersionNumber }).IsUnique();
            e.HasIndex(x => x.CreatedAt);
            e.HasOne(x => x.Instruction)
             .WithMany(i => i.Versions)
             .HasForeignKey(x => x.InstructionId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentInstruction>(e =>
        {
            e.ToTable("agent_instructions");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.AgentId, x.InstructionId }).IsUnique();
            e.HasIndex(x => new { x.AgentId, x.LoadOrder });
            e.HasOne(x => x.Agent)
             .WithMany(a => a.Instructions)
             .HasForeignKey(x => x.AgentId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Instruction)
             .WithMany(i => i.AgentInstructions)
             .HasForeignKey(x => x.InstructionId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentEnvRef>(e =>
        {
            e.ToTable("agent_env_refs");
            e.HasKey(x => x.Id);
            e.Property(x => x.EnvKeyName).HasMaxLength(200).IsRequired();
            e.HasIndex(x => new { x.AgentId, x.EnvKeyName }).IsUnique();
            e.HasOne(x => x.Agent)
             .WithMany(a => a.EnvRefs)
             .HasForeignKey(x => x.AgentId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // Project Context tables
        modelBuilder.Entity<ProjectContext>(e =>
        {
            e.ToTable("project_contexts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<ProjectContextVersion>(e =>
        {
            e.ToTable("project_context_versions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Content).HasColumnType("longtext").IsRequired();
            e.Property(x => x.CreatedBy).HasMaxLength(100);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.HasIndex(x => new { x.ProjectContextId, x.VersionNumber }).IsUnique();
            e.HasIndex(x => x.CreatedAt);
            e.HasOne(x => x.ProjectContext)
             .WithMany(p => p.Versions)
             .HasForeignKey(x => x.ProjectContextId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // Repositories table
        modelBuilder.Entity<Repository>(e =>
        {
            e.ToTable("repositories");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.FullName).HasMaxLength(200).IsRequired();
            e.Property(x => x.IsActive).HasDefaultValue(true);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.FullName).IsUnique();
        });

        // Universal Workflow Engine tables
        modelBuilder.Entity<WorkflowDefinition>(e =>
        {
            e.ToTable("workflow_definitions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Namespace).HasMaxLength(50).IsRequired();
            e.Property(x => x.TaskQueue).HasMaxLength(50).IsRequired();
            e.Property(x => x.Description).HasColumnType("text");
            e.Property(x => x.Definition).HasColumnType("json").IsRequired();
            e.Property(x => x.Version).HasDefaultValue(1);
            e.Property(x => x.IsActive).HasDefaultValue(true);
            e.Property(x => x.CreatedBy).HasMaxLength(50);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.IsActive);
        });

        modelBuilder.Entity<WorkflowDefinitionVersion>(e =>
        {
            e.ToTable("workflow_definition_versions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Definition).HasColumnType("json").IsRequired();
            e.Property(x => x.Reason).HasColumnType("text");
            e.Property(x => x.CreatedBy).HasMaxLength(50);
            e.HasIndex(x => new { x.WorkflowDefinitionId, x.Version }).IsUnique();
            e.HasOne(x => x.WorkflowDefinition)
             .WithMany(d => d.Versions)
             .HasForeignKey(x => x.WorkflowDefinitionId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CredentialFile>(e =>
        {
            e.ToTable("credential_files");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Type).HasMaxLength(50).HasDefaultValue("generic");
            e.Property(x => x.FileName).HasMaxLength(500).IsRequired();
            e.Property(x => x.FilePath).HasMaxLength(1000).IsRequired();
            e.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<AgentCredentialMount>(e =>
        {
            e.ToTable("agent_credential_mounts");
            e.HasKey(x => x.Id);
            e.Property(x => x.MountPath).HasMaxLength(500).IsRequired();
            e.Property(x => x.Mode).HasMaxLength(20).HasDefaultValue("ro");
            e.HasIndex(x => new { x.AgentId, x.CredentialFileId, x.MountPath }).IsUnique();
            e.HasOne(x => x.Agent)
             .WithMany(a => a.CredentialMounts)
             .HasForeignKey(x => x.AgentId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.CredentialFile)
             .WithMany(f => f.Mounts)
             .HasForeignKey(x => x.CredentialFileId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CredentialsAudit>(e =>
        {
            e.ToTable("credentials_audit");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.KeyName).HasMaxLength(255).IsRequired();
            e.Property(x => x.Actor).HasMaxLength(255).HasDefaultValue("CEO");
            e.HasIndex(x => x.ChangedAt);
        });

        modelBuilder.Entity<AgentProjectAccess>(e =>
        {
            e.ToTable("agent_project_access");
            e.HasKey(x => new { x.AgentName, x.Project });
            e.Property(x => x.AgentName).HasMaxLength(100).IsRequired();
            e.Property(x => x.Project).HasMaxLength(100).IsRequired();
            e.Property(x => x.Source).HasMaxLength(20).IsRequired()
                .HasDefaultValue(AgentProjectAccessSource.Manual);
            e.HasIndex(x => x.AgentName);
        });

        modelBuilder.Entity<OutputStyle>(e =>
        {
            e.ToTable("output_styles");
            e.HasKey(x => x.Name);
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Body).HasColumnType("longtext").IsRequired();
            e.Property(x => x.Description).HasMaxLength(500);
        });

        modelBuilder.Entity<EpicGrant>(e =>
        {
            e.ToTable("epic_grants");
            e.HasKey(x => x.Id);
            // Guid → char(36), Pomelo's native GUID column.
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Status).HasMaxLength(16).IsRequired();
            e.Property(x => x.Mode).HasMaxLength(16).HasDefaultValue("delegated").IsRequired();
            e.Property(x => x.PreflightJson).HasColumnType("longtext");
            e.Property(x => x.PreflightSha256).HasMaxLength(64).IsFixedLength();
            e.Property(x => x.ScopeJson).HasColumnType("longtext").IsRequired();
            e.Property(x => x.ScopeSha256).HasMaxLength(64).IsFixedLength().IsRequired();
            e.Property(x => x.DriverNamespace).HasMaxLength(64).IsRequired();
            e.Property(x => x.DriverWorkflowId).HasMaxLength(255).IsRequired();
            e.Property(x => x.DriverRunId).HasMaxLength(36).IsRequired();
            e.Property(x => x.CtoAgent).HasMaxLength(128).IsRequired();
            e.Property(x => x.RevokeReason).HasMaxLength(500);
        });

        modelBuilder.Entity<EpicGrantBinding>(e =>
        {
            e.ToTable("epic_grant_bindings");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Namespace).HasMaxLength(64).IsRequired();
            e.Property(x => x.Repo).HasMaxLength(200).IsRequired();
            e.Property(x => x.WorkflowId).HasMaxLength(255).IsRequired();
            e.Property(x => x.RunId).HasMaxLength(64).IsRequired();
            e.Property(x => x.ReviewRef).HasMaxLength(64).IsFixedLength().IsRequired();
            e.Property(x => x.CreatedAt).HasColumnType("datetime(6)");
            e.HasIndex(x => new { x.GrantId, x.Repo, x.Issue }).IsUnique();
            e.HasOne(x => x.Grant).WithMany().HasForeignKey(x => x.GrantId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<EpicGrantRun>(e =>
        {
            e.ToTable("epic_grant_runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Namespace).HasMaxLength(64).IsRequired();
            e.Property(x => x.WorkflowId).HasMaxLength(255).IsRequired();
            e.Property(x => x.RunId).HasMaxLength(64).IsRequired();
            e.Property(x => x.WorkflowType).HasMaxLength(100).IsRequired();
            e.Property(x => x.Repo).HasMaxLength(200).IsRequired();
            e.Property(x => x.MaxFixRound).HasDefaultValue(0);
            e.Property(x => x.FirstReservedAt).HasColumnType("datetime(6)");
            e.HasIndex(x => new { x.GrantId, x.Namespace, x.WorkflowId, x.RunId }).IsUnique();
            e.HasOne(x => x.Grant).WithMany().HasForeignKey(x => x.GrantId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<EpicGrantDecision>(e =>
        {
            e.ToTable("epic_grant_decisions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Namespace).HasMaxLength(64).IsRequired();
            e.Property(x => x.WorkflowId).HasMaxLength(255).IsRequired();
            e.Property(x => x.RunId).HasMaxLength(36).IsRequired();
            e.Property(x => x.Gate).HasMaxLength(32).IsRequired();
            e.Property(x => x.VisitId).HasMaxLength(64).IsRequired();
            e.Property(x => x.ArtifactRef).HasMaxLength(64).IsRequired();
            e.Property(x => x.Evidence).HasMaxLength(500).IsRequired();
            e.Property(x => x.Caller).HasMaxLength(128).IsRequired();
            e.Property(x => x.Status).HasMaxLength(16).IsRequired();
            // The at-most-once guard: one delegated decision per gate visit, forever.
            e.HasIndex(x => new { x.Namespace, x.WorkflowId, x.RunId, x.Gate, x.VisitId }).IsUnique();
            e.HasIndex(x => x.GrantId);
            e.HasOne(x => x.Grant)
             .WithMany()
             .HasForeignKey(x => x.GrantId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // ─── Workflow failure accountability (#476) ───────────────────────────
        modelBuilder.Entity<WorkflowOutcomeRun>(e =>
        {
            e.ToTable("workflow_outcome_runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Namespace).HasMaxLength(64).IsRequired();
            e.Property(x => x.WorkflowId).HasMaxLength(255).IsRequired();
            e.Property(x => x.RunId).HasMaxLength(64).IsRequired();
            // Binary-collated hash key: the unique identity guard. Originals are compared on match.
            var identityHash = e.Property(x => x.IdentityHash).HasMaxLength(64).IsFixedLength().IsRequired();
            // SQLite test hosts have no ascii_bin collation; MySQL's model and snapshot keep it.
            if (Database.IsMySql()) identityHash.UseCollation("ascii_bin");
            e.Property(x => x.FirstExecutionRunId).HasMaxLength(64);
            e.Property(x => x.PreviousRunId).HasMaxLength(64);
            e.Property(x => x.NextRunId).HasMaxLength(64);
            e.Property(x => x.ParentWorkflowId).HasMaxLength(255);
            e.Property(x => x.ParentRunId).HasMaxLength(64);
            e.Property(x => x.WorkflowType).HasMaxLength(255).IsRequired();
            e.Property(x => x.DefinitionName).HasMaxLength(255);
            e.Property(x => x.DefinitionHash).HasMaxLength(64);
            e.Property(x => x.StartTime).HasColumnType("datetime(6)");
            e.Property(x => x.CloseTime).HasColumnType("datetime(6)");
            e.Property(x => x.Status).HasMaxLength(32).IsRequired();
            e.Property(x => x.TerminalEventType).HasMaxLength(64);
            e.Property(x => x.TerminalEventHash).HasMaxLength(64);
            e.Property(x => x.Classification).HasMaxLength(32).IsRequired();
            e.Property(x => x.ReasonCode).HasMaxLength(64);
            e.Property(x => x.CauseRefsJson).HasColumnType("longtext");
            e.Property(x => x.ClassifierId).HasMaxLength(128);
            e.Property(x => x.ClassifierHash).HasMaxLength(64);
            e.Property(x => x.ProjectName).HasMaxLength(100);
            e.Property(x => x.ProjectEvidenceJson).HasColumnType("longtext");
            e.Property(x => x.OwnershipPolicyHash).HasMaxLength(64);
            e.Property(x => x.SourceTaskRefsJson).HasColumnType("longtext");
            e.Property(x => x.ArtifactRefsJson).HasColumnType("longtext");
            e.Property(x => x.Repo).HasMaxLength(200);
            e.Property(x => x.InputHash).HasMaxLength(64);
            e.Property(x => x.Coordinator).HasMaxLength(128);
            e.Property(x => x.TaskOwner).HasMaxLength(128);
            e.Property(x => x.EvidenceState).HasMaxLength(32).IsRequired();
            e.Property(x => x.PayloadCiphertext).HasColumnType("longblob");
            e.Property(x => x.PayloadKeyPurpose).HasMaxLength(128);
            e.Property(x => x.QuarantineReason).HasMaxLength(200);
            e.Property(x => x.DetectedAt).HasColumnType("datetime(6)");
            e.Property(x => x.StoredAt).HasColumnType("datetime(6)");
            e.Property(x => x.UpdatedAt).HasColumnType("datetime(6)");
            e.Property(x => x.ClassificationPendingSince).HasColumnType("datetime(6)");
            e.Property(x => x.MetadataExpiresAt).HasColumnType("datetime(6)");
            e.Property(x => x.ResolvedAt).HasColumnType("datetime(6)");
            e.HasIndex(x => x.IdentityHash).IsUnique();
            e.HasIndex(x => new { x.Classification, x.Version });
            e.HasIndex(x => new { x.Namespace, x.Status });
            e.HasIndex(x => x.FailureIncidentId);
            e.HasIndex(x => x.MetadataExpiresAt);
        });

        modelBuilder.Entity<WorkflowOutcomeObservation>(e =>
        {
            e.ToTable("workflow_outcome_observations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.EventType).HasMaxLength(64).IsRequired();
            e.Property(x => x.EventHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.Status).HasMaxLength(32).IsRequired();
            e.Property(x => x.Classification).HasMaxLength(32).IsRequired();
            e.Property(x => x.ReasonCode).HasMaxLength(64);
            e.Property(x => x.ObservedAt).HasColumnType("datetime(6)");
            e.HasIndex(x => new { x.RunRowId, x.EventId, x.EventType, x.EventHash }).IsUnique();
            e.HasOne(x => x.Run).WithMany().HasForeignKey(x => x.RunRowId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WorkflowOutcomeCheckpoint>(e =>
        {
            e.ToTable("workflow_outcome_checkpoints");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Namespace).HasMaxLength(64).IsRequired();
            e.Property(x => x.WindowKind).HasMaxLength(16).IsRequired();
            e.Property(x => x.WindowStart).HasColumnType("datetime(6)");
            e.Property(x => x.WindowEnd).HasColumnType("datetime(6)");
            e.Property(x => x.WindowKey).HasMaxLength(160).IsRequired();
            e.Property(x => x.QueryHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.PageToken).HasColumnType("longtext");
            e.Property(x => x.PassStartedAt).HasColumnType("datetime(6)");
            e.Property(x => x.PassCompletedAt).HasColumnType("datetime(6)");
            e.Property(x => x.LastError).HasMaxLength(200);
            e.Property(x => x.ArchivalState).HasMaxLength(32);
            e.Property(x => x.EnrolledAt).HasColumnType("datetime(6)");
            e.Property(x => x.EnrolledCoordinator).HasMaxLength(128);
            e.Property(x => x.PolicyHashes).HasMaxLength(200);
            e.Property(x => x.LeaseOwner).HasMaxLength(128);
            e.Property(x => x.LeaseExpiresAt).HasColumnType("datetime(6)");
            e.Property(x => x.UpdatedAt).HasColumnType("datetime(6)");
            e.Property(x => x.LeaseEpoch).IsConcurrencyToken();
            e.HasIndex(x => x.WindowKey).IsUnique();
        });

        modelBuilder.Entity<WorkflowOutcomeBatch>(e =>
        {
            e.ToTable("workflow_outcome_batches");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Coordinator).HasMaxLength(128).IsRequired();
            e.Property(x => x.State).HasMaxLength(32).IsRequired();
            e.Property(x => x.Nonce).HasMaxLength(64).IsRequired();
            e.Property(x => x.ItemVersionsHash).HasMaxLength(64).IsFixedLength().IsRequired();
            e.Property(x => x.ReceiptHash).HasMaxLength(64);
            e.Property(x => x.CreatedAt).HasColumnType("datetime(6)");
            e.Property(x => x.LeasedAt).HasColumnType("datetime(6)");
            e.Property(x => x.DeliveredAt).HasColumnType("datetime(6)");
            e.Property(x => x.AckedAt).HasColumnType("datetime(6)");
            e.HasIndex(x => new { x.Coordinator, x.State, x.CreatedAt });
            e.HasMany(x => x.Items).WithOne(x => x.Batch).HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WorkflowOutcomeBatchItem>(e =>
        {
            e.ToTable("workflow_outcome_batch_items");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.State).HasMaxLength(32).IsRequired();
            e.Property(x => x.DeliveredAt).HasColumnType("datetime(6)");
            e.Property(x => x.AckedAt).HasColumnType("datetime(6)");
            e.Property(x => x.AckedBy).HasMaxLength(128);
            e.Property(x => x.AckSource).HasMaxLength(16);
            e.HasIndex(x => new { x.OutcomeId, x.OutcomeVersion, x.DeliveryGeneration }).IsUnique();
            e.HasIndex(x => x.BatchId);
        });

        modelBuilder.Entity<WorkflowOutcomeDecision>(e =>
        {
            e.ToTable("workflow_outcome_decisions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Operation).HasMaxLength(32).IsRequired();
            e.Property(x => x.DecidedBy).HasMaxLength(128).IsRequired();
            e.Property(x => x.DecidedVia).HasMaxLength(16).IsRequired();
            e.Property(x => x.Owner).HasMaxLength(200).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(2000).IsRequired();
            e.Property(x => x.EvidenceRefsJson).HasColumnType("longtext").IsRequired();
            e.Property(x => x.TaskRef).HasMaxLength(500);
            e.Property(x => x.ArtifactRef).HasMaxLength(500);
            e.Property(x => x.StopProofRef).HasMaxLength(500);
            e.Property(x => x.DueAt).HasColumnType("datetime(6)");
            e.Property(x => x.AuthorityRef).HasMaxLength(500);
            e.Property(x => x.Status).HasMaxLength(16).IsRequired();
            e.Property(x => x.RejectReason).HasMaxLength(64);
            e.Property(x => x.CreatedAt).HasColumnType("datetime(6)");
            e.HasIndex(x => new { x.OutcomeId, x.CreatedAt });
        });

        modelBuilder.Entity<WorkflowRecoveryOperation>(e =>
        {
            e.ToTable("workflow_recovery_operations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Operation).HasMaxLength(32).IsRequired();
            e.Property(x => x.SourceNamespace).HasMaxLength(64).IsRequired();
            e.Property(x => x.SourceWorkflowId).HasMaxLength(255).IsRequired();
            e.Property(x => x.SourceRunId).HasMaxLength(64).IsRequired();
            e.Property(x => x.SourceTaskHash).HasMaxLength(64);
            e.Property(x => x.SourceArtifactHash).HasMaxLength(64);
            e.Property(x => x.TargetNamespace).HasMaxLength(64).IsRequired();
            e.Property(x => x.TargetWorkflowType).HasMaxLength(255);
            e.Property(x => x.TargetTaskQueue).HasMaxLength(255);
            e.Property(x => x.TargetAgent).HasMaxLength(128);
            e.Property(x => x.InputSha256).HasMaxLength(64);
            e.Property(x => x.ReplacementWorkflowId).HasMaxLength(255).IsRequired();
            e.Property(x => x.Scope).HasMaxLength(500).IsRequired();
            e.Property(x => x.Coordinator).HasMaxLength(128).IsRequired();
            e.Property(x => x.StopProofRefsJson).HasColumnType("longtext").IsRequired();
            e.Property(x => x.AuthorityRef).HasMaxLength(500).IsRequired();
            e.Property(x => x.State).HasMaxLength(32).IsRequired();
            e.Property(x => x.ReservedBy).HasMaxLength(128).IsRequired();
            e.Property(x => x.ReservedAt).HasColumnType("datetime(6)");
            e.Property(x => x.DispatchingAt).HasColumnType("datetime(6)");
            e.Property(x => x.ResultRunId).HasMaxLength(64);
            e.Property(x => x.ReceiptEventRef).HasMaxLength(500);
            e.Property(x => x.ReceiptJson).HasColumnType("longtext");
            e.Property(x => x.BlockedReason).HasMaxLength(200);
            e.Property(x => x.UpdatedAt).HasColumnType("datetime(6)");
            e.HasIndex(x => x.DecisionId).IsUnique();
            e.HasIndex(x => x.ReplacementWorkflowId).IsUnique();
            e.HasIndex(x => new { x.OutcomeId, x.State });
        });

        modelBuilder.Entity<WorkflowOutcomeLateResult>(e =>
        {
            e.ToTable("workflow_outcome_late_results");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Namespace).HasMaxLength(64).IsRequired();
            e.Property(x => x.TaskId).HasMaxLength(300).IsRequired();
            e.Property(x => x.ResultHash).HasMaxLength(64).IsFixedLength().IsRequired();
            e.Property(x => x.MessageId).HasMaxLength(200).IsRequired();
            e.Property(x => x.Status).HasMaxLength(16).IsRequired();
            e.Property(x => x.ResultStatus).HasMaxLength(32).IsRequired();
            e.Property(x => x.Ciphertext).HasColumnType("longblob");
            e.Property(x => x.SourceCorrelation).HasMaxLength(500);
            e.Property(x => x.QuarantineReason).HasMaxLength(200);
            e.Property(x => x.ReceivedAt).HasColumnType("datetime(6)");
            e.Property(x => x.StoredAt).HasColumnType("datetime(6)");
            e.HasIndex(x => new { x.Namespace, x.TaskId, x.ResultHash }).IsUnique();
        });

        modelBuilder.Entity<WorkflowOutcomeHealth>(e =>
        {
            e.ToTable("workflow_outcome_health");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Component).HasMaxLength(64).IsRequired();
            e.Property(x => x.OutageKey).HasMaxLength(200).IsRequired();
            e.Property(x => x.State).HasMaxLength(16).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(500).IsRequired();
            e.Property(x => x.Namespace).HasMaxLength(64);
            e.Property(x => x.GapStart).HasColumnType("datetime(6)");
            e.Property(x => x.GapEnd).HasColumnType("datetime(6)");
            e.Property(x => x.CauseReviewRefsJson).HasColumnType("longtext");
            e.Property(x => x.FirstSeenAt).HasColumnType("datetime(6)");
            e.Property(x => x.LastSeenAt).HasColumnType("datetime(6)");
            e.Property(x => x.ResolvedAt).HasColumnType("datetime(6)");
            e.HasIndex(x => new { x.Component, x.OutageKey }).IsUnique();
            e.HasIndex(x => x.State);
        });
    }
}

public class OrchestratorDbContextFactory : IDesignTimeDbContextFactory<OrchestratorDbContext>
{
    public OrchestratorDbContext CreateDbContext(string[] args)
    {
        var cs = "Server=localhost;Port=3306;Database=fleet_orchestrator;User=fleet;Password=fleetpass;";
        var opts = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseMySql(cs, new MySqlServerVersion(new Version(8, 0, 0)))
            .Options;
        return new OrchestratorDbContext(opts);
    }
}
