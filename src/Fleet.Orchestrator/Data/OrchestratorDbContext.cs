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
            e.Property(x => x.WorkDir).HasMaxLength(500).HasDefaultValue("/workspace");
            e.Property(x => x.GroupListenMode).HasMaxLength(50).HasDefaultValue("mention");
            e.Property(x => x.ShortName).HasMaxLength(100).HasDefaultValue("");
            e.Property(x => x.Image).HasMaxLength(200);
            e.Property(x => x.Effort).HasMaxLength(20);
            e.Property(x => x.CodexSandboxMode).HasMaxLength(30);
            e.Property(x => x.AutoMemoryEnabled).HasDefaultValue(true);
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
            e.HasIndex(x => x.AgentName);
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
