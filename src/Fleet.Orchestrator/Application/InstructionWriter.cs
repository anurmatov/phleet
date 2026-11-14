using System.Text.RegularExpressions;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Application;

/// <summary>A stored instruction version and the advisory size report for it (#346).</summary>
public sealed record InstructionWritten(string Name, int Version, PromptSizeReport Size);

/// <summary>
/// The one implementation of "create an instruction", "add a version" and "roll back". The
/// <c>/api/instructions</c> writes and the three instruction tools are adapters over it.
/// </summary>
/// <remarks>
/// Version history is capped at <see cref="MaxVersions"/>; the oldest rows are pruned in the same
/// save that adds the new one. A new instruction and its first version are one save. The size
/// report is advisory: it is computed after the commit and never refuses a write.
/// </remarks>
public sealed partial class InstructionWriter(IServiceScopeFactory scopes, PromptSizePolicy sizePolicy)
{
    public const int MaxVersions = 20;

    public async Task<WriteOutcome<InstructionWritten>> CreateAsync(
        string? name, string? content, string createdBy, string? reason, WriteCaller caller, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
        if (db is null) return WriteFaults.NoDatabase;

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(content))
            return WriteFaults.Invalid("name_and_content_required", "name and content are required");
        if (!ValidName().IsMatch(name))
            return WriteFaults.Invalid("name_invalid", "name must contain only letters, digits, hyphens, or underscores");

        if (await db.Instructions.AnyAsync(i => i.Name == name, ct))
            return WriteFaults.Conflict("instruction_exists", $"Instruction '{name}' already exists");

        var instruction = new Instruction { Name = name, CurrentVersion = 1 };
        instruction.Versions.Add(new InstructionVersion
        {
            VersionNumber = 1,
            Content = content,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = createdBy,
            Reason = reason ?? "Initial creation",
        });
        db.Instructions.Add(instruction);
        await db.SaveChangesAsync(ct);

        return Report(name, 1, previousContent: null, content, caller);
    }

    public async Task<WriteOutcome<InstructionWritten>> AddVersionAsync(
        string name, string? content, string createdBy, string? reason, WriteCaller caller, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
        if (db is null) return WriteFaults.NoDatabase;

        if (string.IsNullOrWhiteSpace(content))
            return WriteFaults.Invalid("content_required", "content is required");

        var instruction = await LoadAsync(db, name, ct);
        if (instruction is null)
            return WriteFaults.NotFound("instruction_not_found", $"Instruction '{name}' not found");

        var (version, previous) = Append(db, instruction, content, createdBy, reason);
        await db.SaveChangesAsync(ct);
        return Report(name, version, previous, content, caller);
    }

    /// <summary>Rolls back by adding a new version with the target's content. Non-destructive.</summary>
    public async Task<WriteOutcome<InstructionWritten>> RollbackAsync(
        string name, int targetVersion, WriteCaller caller, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
        if (db is null) return WriteFaults.NoDatabase;

        var instruction = await LoadAsync(db, name, ct);
        if (instruction is null)
            return WriteFaults.NotFound("instruction_not_found", $"Instruction '{name}' not found");

        var target = instruction.Versions.FirstOrDefault(v => v.VersionNumber == targetVersion);
        if (target is null)
            return WriteFaults.NotFound("instruction_version_not_found", $"Version {targetVersion} not found");

        var content = target.Content;
        var (version, previous) = Append(db, instruction, content, "rollback", $"rollback to v{targetVersion}");
        await db.SaveChangesAsync(ct);
        return Report(name, version, previous, content, caller);
    }

    /// <summary>Marks an instruction active or inactive.</summary>
    public async Task<WriteOutcome<bool>> SetActiveAsync(string name, bool isActive, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
        if (db is null) return WriteFaults.NoDatabase;

        var instruction = await db.Instructions.FirstOrDefaultAsync(i => i.Name == name, ct);
        if (instruction is null)
            return WriteFaults.NotFound("instruction_not_found", $"Instruction '{name}' not found");

        instruction.IsActive = isActive;
        await db.SaveChangesAsync(ct);
        return WriteOutcome<bool>.Ok(isActive);
    }

    private static Task<Instruction?> LoadAsync(OrchestratorDbContext db, string name, CancellationToken ct) =>
        db.Instructions.Include(i => i.Versions.OrderBy(v => v.VersionNumber)).FirstOrDefaultAsync(i => i.Name == name, ct);

    /// <summary>
    /// Stages the next version and prunes history past the cap. Returns the new version number and
    /// the content it replaces, captured before the add so the new row is never mistaken for it
    /// (null when the current row is missing: pruned or never written).
    /// </summary>
    private static (int Version, string? PreviousContent) Append(
        OrchestratorDbContext db, Instruction instruction, string content, string createdBy, string? reason)
    {
        var previous = instruction.Versions.FirstOrDefault(v => v.VersionNumber == instruction.CurrentVersion)?.Content;
        var version = instruction.CurrentVersion + 1;
        instruction.Versions.Add(new InstructionVersion
        {
            InstructionId = instruction.Id,
            VersionNumber = version,
            Content = content,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = createdBy,
            Reason = reason,
        });
        instruction.CurrentVersion = version;

        var excess = instruction.Versions.Count - MaxVersions;
        if (excess > 0)
            db.InstructionVersions.RemoveRange(instruction.Versions.OrderBy(v => v.VersionNumber).Take(excess).ToList());

        return (version, previous);
    }

    private WriteOutcome<InstructionWritten> Report(string name, int version, string? previousContent, string content, WriteCaller caller)
    {
        var size = sizePolicy.Evaluate(PromptSizeKind.Instruction, name, previousContent, content);
        sizePolicy.LogWrite(size, caller.Tag, name);
        return WriteOutcome<InstructionWritten>.Ok(new InstructionWritten(name, version, size));
    }

    [GeneratedRegex("^[a-zA-Z0-9_-]+$")]
    private static partial Regex ValidName();
}
