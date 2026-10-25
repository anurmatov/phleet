using System.Security.Cryptography;
using System.Text.Json;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// Authorized reader of the CURRENT admission policy from ComputeDriver.GetCapabilities
/// .resource_admission_policy. NO production implementation exists: that capability field is not
/// in the vendored upstream contract, and an approved reader depends on the reviewed upstream
/// commit (the same ownership-contract dependency as the D2 evidence reader). Implementing it
/// against any other surface — plugin Settings arrays, config files or caches — is invented wire
/// and is a review blocker. A registered implementation returns the current qualified policy
/// acknowledgement, or null only for a known-absent capability; unknown state must throw.
/// </summary>
public interface IOpenShellCurrentPolicyReader
{
    Task<string?> ReadCurrentPolicyAsync(CancellationToken ct);
}

/// <summary>
/// Concrete static external qualification run before any mutation: the pinned hook binary with its
/// actually-generated Describe/ValidateConfig replies (bound to that binary, its config and the
/// approved plan), the qualified policy config, receiver binding, ingress shape, and the
/// nonroot/no-socket/no-env/restart-never constraints re-verified independently of the compiler
/// call that produced the plan. The live current policy comes only from
/// <see cref="IOpenShellCurrentPolicyReader"/>; without that authorized reader the qualification
/// refuses, it does not substitute a Docker-plugin Settings guess.
/// </summary>
/// <remarks>
/// <para>
/// The qualification root is outside-custody material an operator installs (same trust posture as
/// the artifact custody root); this class reads, verifies digests and refuses — it never writes,
/// installs, starts or pulls anything. A missing or unreadable input is a refusal, never a
/// default: absence of evidence is never evidence of qualification.
/// </para>
/// <para>
/// The file contract inside the qualification root is exact, and the whole set rotates together:
/// <c>hook</c> (pinned binary bytes), <c>hook-sha256</c> (64 lowercase hex of those bytes),
/// <c>config.json</c> (the qualified policy acknowledgement the hook was validated against),
/// <c>describe.json</c> (recorded Describe reply, fields exactly <c>plugin</c>/<c>scope</c>/
/// <c>hook_sha256</c>/<c>config_sha256</c>) and <c>validate-config.json</c> (recorded ValidateConfig
/// reply, fields exactly <c>accepted</c>/<c>plugin</c>/<c>hook_sha256</c>/<c>config_sha256</c>/
/// <c>plan_hash</c>). The digest and plan-hash fields bind each recorded reply to THIS binary,
/// THIS config and THIS approved plan: a self-consistent record set generated for another hook,
/// config or plan is refused, not accepted because it is internally valid.
/// </para>
/// <para>
/// Custody: the root lives outside the repository and outside any agent mount, is installed and
/// owned by the operator, files are read-only (0400/0600), and rotation replaces all five files in
/// one step because every record cross-binds the others' digests.
/// </para>
/// </remarks>
public sealed class OpenShellPreparationPreflight(
    HttpClient engine, string qualificationRoot, string hookPluginName,
    OpenShellBootstrapReceiverOptions receiver, IOpenShellCurrentPolicyReader? currentPolicy)
    : IOpenShellPreparationPreflight, IDisposable
{
    public async Task VerifyAsync(OpenShellPreparationScope selection, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var plan = selection.Plan;
        if (plan.Hash != selection.PlanHash) throw Blocked("bootstrap_preflight_plan_changed");
        if (hookPluginName.Length is 0 or > 128 || !hookPluginName.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            || !Path.IsPathFullyQualified(qualificationRoot))
            throw Blocked("bootstrap_preflight_unconfigured");
        var spec = plan.ReadSpec();
        var template = spec.Template;
        var process = spec.Policy?.Process;
        var filesystem = spec.Policy?.Filesystem;

        // Re-verify the static bootstrap invariants at the mutation boundary rather than trusting
        // that the compiler ran: digest-pinned image, empty driver/socket/environment surfaces,
        // restart-never, the fixed uid/gid pair, workdir never included, and the exact command.
        if (template is null || process is null || filesystem is null
            || plan.ImageHash.Length != 64 || !plan.ImageHash.All(char.IsAsciiHexDigitLower)
            || !template.Image.EndsWith("@sha256:" + plan.ImageHash, StringComparison.Ordinal)
            || template.DriverConfig is { Fields.Count: > 0 }
            || template.AgentSocket.Length != 0 || template.Environment.Count != 0
            || spec.Environment.Count != 0
            || spec.RestartPolicy != Openshell.V1.SandboxRestartPolicy.Never
            || process is not { RunAsUser: "1000", RunAsGroup: "1000" }
            || filesystem.IncludeWorkdir
            || !spec.Command.SequenceEqual(new[] { "dotnet", "/app/Fleet.Agent.dll", "--OpenShell:Bootstrap=true" }))
            throw Blocked("bootstrap_static_qualification_changed");

        var hook = Read(Path.Combine(qualificationRoot, "hook"));
        var pinned = ReadText(Path.Combine(qualificationRoot, "hook-sha256")).Trim();
        if (pinned.Length != 64 || !pinned.All(char.IsAsciiHexDigitLower)
            || Convert.ToHexStringLower(SHA256.HashData(hook)) != pinned)
            throw Blocked("bootstrap_hook_artifact_unqualified");

        // The recorded replies must belong to this binary, this config and this approved plan —
        // internal validity alone proves nothing about which hook or plan produced them.
        var config = ReadText(Path.Combine(qualificationRoot, "config.json"));
        var configDigest = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(config)));
        var describe = ReadJson(Path.Combine(qualificationRoot, "describe.json"));
        Fields(describe, "plugin", "scope", "hook_sha256", "config_sha256");
        if (describe.GetProperty("plugin").GetString() != hookPluginName
            || describe.GetProperty("scope").GetString() != "local"
            || describe.GetProperty("hook_sha256").GetString() != pinned
            || describe.GetProperty("config_sha256").GetString() != configDigest)
            throw Blocked("bootstrap_hook_describe_unqualified");
        var validated = ReadJson(Path.Combine(qualificationRoot, "validate-config.json"));
        Fields(validated, "accepted", "plugin", "hook_sha256", "config_sha256", "plan_hash");
        if (validated.GetProperty("accepted").ValueKind != JsonValueKind.True
            || validated.GetProperty("plugin").GetString() != hookPluginName
            || validated.GetProperty("hook_sha256").GetString() != pinned
            || validated.GetProperty("config_sha256").GetString() != configDigest
            || validated.GetProperty("plan_hash").GetString() != selection.PlanHash)
            throw Blocked("bootstrap_hook_validate_config_unqualified");

        // The external ingress is validated on its own, then bound to the dedicated receiver;
        // the receiver is checked on its own (backend port must not collide with the API port).
        if (!Uri.TryCreate(plan.BootstrapIngress, UriKind.Absolute, out var ingress)
            || ingress.Scheme != "https" || ingress.AbsolutePath != "/identity/ready"
            || ingress.Query != "" || ingress.Fragment != "" || ingress.UserInfo != "")
            throw Blocked("bootstrap_ingress_unqualified");
        if (!receiver.Enabled || receiver.Port is < 1 or > 65535 or 3600
            || string.IsNullOrWhiteSpace(receiver.IngressDnsName)
            || !string.Equals(ingress.Host, receiver.IngressDnsName, StringComparison.OrdinalIgnoreCase)
            || (!ingress.IsDefaultPort && ingress.Port != receiver.Port))
            throw Blocked("bootstrap_receiver_binding_unqualified");

        // Current policy only through the authorized capabilities reader. Absent reader or an
        // absent/foreign policy refuses; a read failure propagates as refusal, never "unneeded".
        var live = currentPolicy is null ? throw Blocked("bootstrap_current_policy_unavailable")
            : await currentPolicy.ReadCurrentPolicyAsync(ct);
        if (live is null) throw Blocked("bootstrap_current_policy_unavailable");
        // Binds the plan's approval acknowledgement to the qualified config and the live policy.
        plan.ReadResourceAdmission().Validate(plan.Workspace, live);
        if (live.Trim() != config) throw Blocked("bootstrap_current_policy_unqualified");
    }

    /// <summary>
    /// Fails closed by construction until a reviewed upstream commit ships the approved read-only
    /// ownership contract: complete authenticated runtime/process/broker ownership cannot be
    /// established from labels, names, leases or queue depth, and no local substitute is permitted.
    /// Deleting this throw without that pin is a review blocker, not a simplification.
    /// </summary>
    public Task VerifyLegacyOwnershipAsync(OpenShellPreparationScope selection, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        throw Blocked("legacy_ownership_inventory_unqualified");
    }

    /// <summary>Stops the exact immutable legacy container ID through the engine; never by name.</summary>
    /// <remarks>
    /// Only the stop itself lives here — confirmation of absence/drain belongs to
    /// <see cref="IOpenShellRuntimeEvidenceReader"/>, which remains unregistered until the D2
    /// contract ships, so the coordinator still cannot reach this call in production.
    /// </remarks>
    public async Task StopLegacyExactAsync(string expectedId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (expectedId.Length != 64 || !expectedId.All(char.IsAsciiHexDigitLower))
            throw Blocked("legacy_stop_id_unqualified");
        using var response = await engine.PostAsync("/v1.41/containers/" + expectedId + "/stop?t=10", null, ct);
        // 204 stopped; 304 already stopped — both are the requested state for this exact ID.
        // 404/409/anything else is an unknown or foreign target and refuses.
        if (response.StatusCode is not (System.Net.HttpStatusCode.NoContent or System.Net.HttpStatusCode.NotModified))
            throw Blocked("legacy_stop_unconfirmed");
    }

    private byte[] Read(string path)
    {
        if (!File.Exists(path)) throw Blocked("bootstrap_artifact_missing");
        try { return File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw Blocked("bootstrap_artifact_missing"); }
    }

    private string ReadText(string path) => System.Text.Encoding.UTF8.GetString(Read(path));

    private JsonElement ReadJson(string path)
    {
        try { return JsonDocument.Parse(ReadText(path)).RootElement.Clone(); }
        catch (JsonException) { throw Blocked("bootstrap_qualification_record_malformed"); }
    }

    private static void Fields(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Blocked("bootstrap_qualification_record_malformed");
        var names = value.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != expected.Length || names.Distinct(StringComparer.Ordinal).Count() != expected.Length
            || names.Any(n => !expected.Contains(n, StringComparer.Ordinal)))
            throw Blocked("bootstrap_qualification_record_malformed");
    }

    private static OpenShellStateConflictException Blocked(string code) => new(code);

    public void Dispose() => engine.Dispose();
}

/// <summary>
/// Registers the concrete preflight ONLY when explicitly configured. Disabled means
/// <c>OpenShellBootstrapPreflight:Enabled</c> is not exactly "true" (the default), or the
/// qualification root / plugin name is missing — in every disabled case nothing is registered and
/// the coordinator fails with "preparation peers unqualified" exactly as before, so the
/// default-deny posture of the preparation chain is unchanged by this source. The authorized
/// current-policy reader is resolved when present; it has no production implementation, so even an
/// explicitly configured preflight refuses at the live-policy step until the upstream pin ships.
/// </summary>
public static class OpenShellPreparationPreflightRegistration
{
    public static void AddOpenShellPreparationPreflight(this Microsoft.AspNetCore.Builder.WebApplicationBuilder builder)
    {
        if (!string.Equals(builder.Configuration["OpenShellBootstrapPreflight:Enabled"], "true", StringComparison.Ordinal))
            return;
        var root = builder.Configuration["OpenShellBootstrapPreflight:HookQualificationRoot"] ?? "";
        var plugin = builder.Configuration["OpenShellBootstrapPreflight:HookPluginName"] ?? "";
        if (root.Length == 0 || plugin.Length == 0) return; // configured-but-incomplete stays unregistered
        var socketPath = builder.Configuration["Docker:SocketPath"] ?? "/var/run/docker.sock";
        var handler = new System.Net.Http.SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix,
                    System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
                await socket.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(socketPath), ct);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            },
        };
        builder.Services.AddSingleton<IOpenShellPreparationPreflight>(sp => new OpenShellPreparationPreflight(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost") }, root, plugin,
            sp.GetRequiredService<OpenShellBootstrapReceiverOptions>(),
            sp.GetService<IOpenShellCurrentPolicyReader>()));
    }
}
