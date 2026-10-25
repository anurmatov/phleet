using System.Security.Cryptography;
using System.Text.Json;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// Concrete static external qualification run before any mutation: plan-pinned image artifact, the
/// pinned hook binary with its recorded Describe/ValidateConfig, the CURRENT admission policy read
/// live from the engine plugin, receiver binding, and the nonroot/no-socket/no-env/restart-never
/// constraints re-verified independently of the compiler call that produced the plan.
/// </summary>
/// <remarks>
/// <para>
/// The qualification root is outside-custody material an operator installs (owner-only permissions,
/// same trust posture as the artifact custody root); this class reads, verifies digests and refuses —
/// it never writes, installs, starts or pulls anything. A missing or unreadable input is a refusal,
/// never a default: absence of evidence is never evidence of qualification.
/// </para>
/// <para>
/// The file contract inside the qualification root is exact: <c>hook</c> (pinned binary bytes),
/// <c>hook-sha256</c> (64 lowercase hex of those bytes), <c>describe.json</c> (recorded hook
/// Describe output, fields exactly <c>plugin</c>/<c>scope</c> with <c>scope</c> == "local" and
/// <c>plugin</c> == the configured plugin name), <c>validate-config.json</c> (recorded hook
/// ValidateConfig acceptance, fields exactly <c>accepted</c> == true).
/// </para>
/// <para>
/// The current admission policy is read live from the engine plugin's published settings
/// (<c>resource_admission_policy=v1:{…}</c>, exactly one entry) and bound to the plan through
/// <see cref="OpenShellResourceAdmission.Validate"/>; a drifted or absent current policy refuses.
/// It does NOT mint operator approval, register an adapter on the engine, or enable the authority;
/// the coordinator remains default-deny, and the D2 runtime-evidence and lost-Create ownership
/// readers stay blocked pending the reviewed upstream read-only ownership contract.
/// </para>
/// </remarks>
public sealed class OpenShellPreparationPreflight(
    HttpClient engine, string qualificationRoot, string hookPluginName,
    OpenShellBootstrapReceiverOptions receiver) : IOpenShellPreparationPreflight, IDisposable
{
    private const string PolicySettingPrefix = "resource_admission_policy=";

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
        var describe = ReadJson(Path.Combine(qualificationRoot, "describe.json"));
        Fields(describe, "plugin", "scope");
        if (describe.GetProperty("plugin").GetString() != hookPluginName
            || describe.GetProperty("scope").GetString() != "local")
            throw Blocked("bootstrap_hook_describe_unqualified");
        var validated = ReadJson(Path.Combine(qualificationRoot, "validate-config.json"));
        Fields(validated, "accepted");
        if (validated.GetProperty("accepted").ValueKind != JsonValueKind.True)
            throw Blocked("bootstrap_hook_validate_config_refused");

        // The plan's ingress must be the configured dedicated receiver, not merely some HTTPS URL.
        if (!receiver.Enabled || receiver.Port is < 1 or > 65535 or 3600
            || string.IsNullOrWhiteSpace(receiver.IngressDnsName)
            || !Uri.TryCreate(plan.BootstrapIngress, UriKind.Absolute, out var ingress)
            || ingress.Host != receiver.IngressDnsName
            || (!ingress.IsDefaultPort && ingress.Port != receiver.Port))
            throw Blocked("bootstrap_receiver_binding_unqualified");

        // Current policy, read live. A read failure refuses; it is never read as "no policy needed".
        using (var response = await engine.GetAsync("/v1.41/plugins/" + hookPluginName + "/json", ct))
        {
            if (!response.IsSuccessStatusCode) throw Blocked("bootstrap_current_policy_unavailable");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var plugin = document.RootElement;
            if ((plugin.GetProperty("Name").GetString() ?? "").Split(':')[0] != hookPluginName
                || plugin.GetProperty("Enabled").ValueKind != JsonValueKind.True)
                throw Blocked("bootstrap_plugin_identity_changed");
            var published = plugin.GetProperty("Settings").EnumerateArray()
                .Select(s => s.GetString() ?? "")
                .Where(s => s.StartsWith(PolicySettingPrefix, StringComparison.Ordinal)).ToArray();
            if (published.Length != 1) throw Blocked("bootstrap_current_policy_unavailable");
            // Binds the plan's approval acknowledgement to the policy the plugin is running NOW.
            plan.ReadResourceAdmission().Validate(plan.Workspace, published[0][PolicySettingPrefix.Length..]);
        }
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
/// Registers the concrete preflight ONLY when explicitly configured. Empty/absent configuration
/// registers nothing, and the coordinator then fails with "preparation peers unqualified" exactly
/// as before — the default-deny posture of the preparation chain is unchanged by this source.
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
            sp.GetRequiredService<OpenShellBootstrapReceiverOptions>()));
    }
}
