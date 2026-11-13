using Fleet.Orchestrator.Auth;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Endpoints;

/// <summary>
/// The REST surface behind the dashboard's Credentials view: connection status, restarting the
/// services that read a changed credential, and credential files with their mounts into agents.
/// </summary>
public static class CredentialEndpoints
{
    public static IEndpointRouteBuilder MapCredentialEndpoints(this IEndpointRouteBuilder app)
    {
        var callerAuthenticator = app.ServiceProvider.GetRequiredService<CallerAuthenticator>();

        // GET /api/credentials/connections — rich status with masked values + last-validated
        app.MapGet("/api/credentials/connections", (SetupService setupService) =>
            Results.Ok(setupService.GetRichConnectionsStatus()));

        // POST /api/services/restart
        app.MapPost("/api/services/restart", async (HttpRequest request, DockerService docker, CancellationToken ct) =>
        {
            var body = await request.ReadFromJsonAsync<RestartServicesRequest>();
            var errors = new Dictionary<string, string>();
            var restarted = new List<string>();
            foreach (var svc in body?.Services ?? [])
            {
                try
                {
                    await docker.StopContainerAsync(svc);
                    await docker.StartContainerAsync(svc);
                    restarted.Add(svc);
                }
                catch (Exception ex)
                {
                    errors[svc] = ex.Message;
                }
            }
            return errors.Count > 0
                ? Results.Json(new { restarted, errors }, statusCode: 207)
                : Results.Ok(new { restarted });
        });

        // GET /api/credential-files
        app.MapGet("/api/credential-files", async (IServiceScopeFactory scopeFactory) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null) return Results.Problem("Database not configured");

            var files = await db.CredentialFiles
                .Include(f => f.Mounts).ThenInclude(m => m.Agent)
                .OrderBy(f => f.Name)
                .Select(f => new
                {
                    f.Id, f.Name, f.Type, f.FileName, f.SizeBytes, f.CreatedAt,
                    mounts = f.Mounts.Select(m => new { m.Id, agentName = m.Agent.Name, m.MountPath, m.Mode })
                })
                .ToListAsync();

            return Results.Ok(files);
        });

        // POST /api/credential-files — multipart upload
        app.MapPost("/api/credential-files", async (HttpRequest request, IServiceScopeFactory scopeFactory, IConfiguration config) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null) return Results.Problem("Database not configured");

            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "multipart/form-data required" });

            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            var name = form["name"].FirstOrDefault()?.Trim();
            var type = form["type"].FirstOrDefault()?.Trim() ?? "generic";

            if (file is null) return Results.BadRequest(new { error = "file is required" });
            if (string.IsNullOrEmpty(name)) return Results.BadRequest(new { error = "name is required" });

            if (await db.CredentialFiles.AnyAsync(f => f.Name == name))
                return Results.Conflict(new { error = $"Credential file '{name}' already exists" });

            var baseDir = config["Provisioning:BaseDir"]
                ?? throw new InvalidOperationException("Provisioning:BaseDir not configured");
            var credDir = Path.Combine(baseDir, "credentials");
            Directory.CreateDirectory(credDir);

            var safeFileName = Path.GetFileName(file.FileName).Replace("..", "_");
            // Ensure unique filename
            var finalFileName = safeFileName;
            var counter = 1;
            while (File.Exists(Path.Combine(credDir, finalFileName)))
                finalFileName = $"{Path.GetFileNameWithoutExtension(safeFileName)}_{counter++}{Path.GetExtension(safeFileName)}";

            var filePath = Path.Combine(credDir, finalFileName);
            await using var stream = File.Create(filePath);
            await file.CopyToAsync(stream);

            // chmod 600 for SSH keys
            if (type == "ssh-private-key")
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            var credFile = new CredentialFile
            {
                Name = name,
                Type = type,
                FileName = finalFileName,
                FilePath = filePath,
                SizeBytes = file.Length,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.CredentialFiles.Add(credFile);
            await db.SaveChangesAsync();

            return Results.Created($"/api/credential-files/{credFile.Id}", new { credFile.Id, credFile.Name, credFile.Type, credFile.FileName, credFile.SizeBytes });
        });

        // DELETE /api/credential-files/{id}
        app.MapDelete("/api/credential-files/{id:int}", async (int id, IServiceScopeFactory scopeFactory) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null) return Results.Problem("Database not configured");

            var file = await db.CredentialFiles.FindAsync(id);
            if (file is null) return Results.NotFound();

            try { if (File.Exists(file.FilePath)) File.Delete(file.FilePath); }
            catch { /* best-effort */ }

            db.CredentialFiles.Remove(file);
            await db.SaveChangesAsync();
            return Results.Ok(new { message = $"Credential file '{file.Name}' deleted" });
        });

        // GET /api/credential-files/{id}/download (auth-gated)
        app.MapGet("/api/credential-files/{id:int}/download", async (int id, IServiceScopeFactory scopeFactory, HttpContext ctx) =>
        {
            // A credential file is a secret. The service bearer or a signed-in browser may download one;
            // an enrolled app may not, for the same reason it cannot reach /api/config.
            if (callerAuthenticator.IsRequired
                && CallerIdentity.Of(ctx).Kind is not (CallerKind.Service or CallerKind.Browser))
            {
                return Results.Unauthorized();
            }

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null) return Results.Problem("Database not configured");

            var file = await db.CredentialFiles.FindAsync(id);
            if (file is null) return Results.NotFound();
            if (!File.Exists(file.FilePath)) return Results.NotFound(new { error = "File not found on disk" });

            var bytes = await File.ReadAllBytesAsync(file.FilePath);
            return Results.File(bytes, "application/octet-stream", file.FileName);
        });

        // POST /api/credential-files/{id}/mount
        app.MapPost("/api/credential-files/{id:int}/mount", async (int id, HttpRequest request, IServiceScopeFactory scopeFactory) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null) return Results.Problem("Database not configured");

            var body = await request.ReadFromJsonAsync<MountCredentialRequest>();
            if (body is null || string.IsNullOrWhiteSpace(body.AgentName) || string.IsNullOrWhiteSpace(body.MountPath))
                return Results.BadRequest(new { error = "agentName and mountPath are required" });

            var file = await db.CredentialFiles.FindAsync(id);
            if (file is null) return Results.NotFound(new { error = "Credential file not found" });

            var agent = await db.Agents.FirstOrDefaultAsync(a => a.Name == body.AgentName);
            if (agent is null) return Results.NotFound(new { error = $"Agent '{body.AgentName}' not found" });

            // Remove existing mount for same agent+path if any
            var existing = await db.AgentCredentialMounts
                .FirstOrDefaultAsync(m => m.AgentId == agent.Id && m.MountPath == body.MountPath);
            if (existing is not null) db.AgentCredentialMounts.Remove(existing);

            db.AgentCredentialMounts.Add(new AgentCredentialMount
            {
                AgentId = agent.Id,
                CredentialFileId = id,
                MountPath = body.MountPath,
                Mode = body.Mode ?? "ro",
            });
            await db.SaveChangesAsync();

            return Results.Ok(new { message = $"Mounted '{file.Name}' to agent '{body.AgentName}' at '{body.MountPath}'" });
        });

        // DELETE /api/credential-files/{fileId}/mount/{mountId}
        app.MapDelete("/api/credential-files/{fileId:int}/mount/{mountId:int}", async (int fileId, int mountId, IServiceScopeFactory scopeFactory) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null) return Results.Problem("Database not configured");

            var mount = await db.AgentCredentialMounts.FindAsync(mountId);
            if (mount is null || mount.CredentialFileId != fileId) return Results.NotFound();

            db.AgentCredentialMounts.Remove(mount);
            await db.SaveChangesAsync();
            return Results.Ok(new { message = "Mount removed" });
        });

        return app;
    }
}

internal sealed record RestartServicesRequest(string[] Services);
internal sealed record MountCredentialRequest(string AgentName, string MountPath, string? Mode);
