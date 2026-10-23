using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Fleet.Orchestrator.Data;

internal static class OpenShellModel
{
    internal static void Configure(ModelBuilder model, bool mysql)
    {
        // Desired state only. No running/stopped disposition or existing auth is changed.
        model.Entity<Agent>().Property<string>("ExecutionShell").HasColumnName("execution_shell")
            .HasMaxLength(16).IsRequired().HasDefaultValue("openshell");
        model.Entity<Agent>().Property<string>("OpenShellPolicy").HasColumnName("open_shell_policy")
            .HasMaxLength(32).IsRequired().HasDefaultValue("normal");
        model.Entity<Agent>().Property<string>("OpenShellMigrationState").HasColumnName("open_shell_migration_state")
            .HasMaxLength(16).IsRequired().HasDefaultValue("pending");
        model.Entity<OpenShellLaunch>(e =>
        {
            e.ToTable("agent_open_shell_launches");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ExpectedOldRuntimeId).HasMaxLength(128);
            e.Property(x => x.GatewayId).HasMaxLength(128);
            e.Property(x => x.SandboxId).HasMaxLength(128);
            // Opaque IDs are ordinal. SQLite already defaults to BINARY; the MySQL default
            // collation is case-insensitive, so pin only that provider to its binary collation.
            if (mysql)
            {
                e.Property(x => x.GatewayId).UseCollation("utf8mb4_bin");
                e.Property(x => x.SandboxId).UseCollation("utf8mb4_bin");
            }
            e.Property(x => x.Phase).HasMaxLength(32).IsRequired();
            e.HasIndex(x => x.ActiveAgentId).IsUnique();
            e.HasIndex(x => new { x.GatewayId, x.SandboxId }).IsUnique();
            e.HasOne(x => x.Agent).WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<OpenShellLease>(e =>
        {
            e.ToTable("agent_open_shell_leases");
            e.HasKey(x => x.AgentId);
            e.HasOne(x => x.Agent).WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<OpenShellRetiredId>(e =>
        {
            e.ToTable("open_shell_retired_ids");
            e.HasKey(x => new { x.GatewayId, x.SandboxId });
            e.Property(x => x.GatewayId).HasMaxLength(128);
            e.Property(x => x.SandboxId).HasMaxLength(128);
            // Opaque IDs are ordinal. SQLite already defaults to BINARY; the MySQL default
            // collation is case-insensitive, so pin only that provider to its binary collation.
            if (mysql)
            {
                e.Property(x => x.GatewayId).UseCollation("utf8mb4_bin");
                e.Property(x => x.SandboxId).UseCollation("utf8mb4_bin");
            }
        });
    }

    internal static void ValidateChanges(ChangeTracker tracker)
    {
        tracker.DetectChanges();
        foreach (var row in tracker.Entries<OpenShellRetiredId>())
            if (row.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("OpenShell retired IDs are permanent.");
        foreach (var row in tracker.Entries<OpenShellLaunch>().Where(x => x.State == EntityState.Modified))
        {
            foreach (var name in new[] { nameof(OpenShellLaunch.AgentId), nameof(OpenShellLaunch.IdentityEpoch),
                         nameof(OpenShellLaunch.ExpectedOldRuntimeId), nameof(OpenShellLaunch.CreatedAt) })
                if (row.Property(name).IsModified)
                    throw new InvalidOperationException("OpenShell launch identity is immutable.");
            foreach (var name in new[] { nameof(OpenShellLaunch.GatewayId), nameof(OpenShellLaunch.SandboxId) })
                if (row.Property(name).IsModified && row.Property(name).OriginalValue is not null)
                    throw new InvalidOperationException("OpenShell sandbox IDs cannot be rebound.");
            if (row.Property(x => x.RevokedAt).IsModified && row.Property(x => x.RevokedAt).OriginalValue is not null)
                throw new InvalidOperationException("OpenShell revocation cannot be undone.");
        }
    }
}
