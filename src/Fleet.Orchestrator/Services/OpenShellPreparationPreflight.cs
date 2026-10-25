using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Openshell.Middleware.V1;

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
/// Concrete static external qualification run before any mutation: the pinned hook binary, its
/// actually-generated Describe/ValidateConfig replies (the real <see cref="MiddlewareManifest"/>
/// and <see cref="ValidateConfigResponse"/> wire messages, not handwritten shapes), the
/// ValidateConfig request that binds those replies to the selected hook registration and the
/// approved plan's middleware config, receiver binding, ingress shape, and the
/// nonroot/no-socket/no-env/restart-never constraints re-verified independently of the compiler
/// call that produced the plan. The live current policy comes only from
/// <see cref="IOpenShellCurrentPolicyReader"/>; without that authorized reader the qualification
/// refuses, it does not substitute a Docker-plugin Settings guess.
/// </summary>
/// <remarks>
/// <para>
/// The qualification root is outside-custody material an operator installs (same trust posture as
/// the artifact custody root); this class reads, verifies digests and refuses — it never writes,
/// installs, starts or pulls anything. A missing, unreadable or unparseable input is a refusal,
/// never a default: absence of evidence is never evidence of qualification.
/// </para>
/// <para>
/// File contract inside the qualification root (all files rotate together):
/// <c>hook</c> — pinned binary bytes; <c>hook-sha256</c> — 64 lowercase hex of those bytes;
/// <c>describe.json</c> — the hook's MiddlewareManifest reply in proto3 JSON;
/// <c>validate-request.json</c> — the ValidateConfigRequest actually sent (registration name +
/// the plan's middleware config); <c>validate-config.json</c> — the ValidateConfigResponse reply;
/// <c>config.json</c> — the qualified current-policy acknowledgement; and <c>records-sha256</c> —
/// 64 lowercase hex over the canonical framing of the <c>hook</c> binary plus the four record
/// files, so substituting or mixing record files across hook generations — or replacing the
/// binary and its <c>hook-sha256</c> sidecar as a self-consistent pair while keeping old
/// replies — is detected. The digest is substitution/corruption detection, not authentication:
/// the operator-owned read-only root is the trust root. Bindings are three-way on the
/// registration: the configured name, the registration the approved plan selected (its single
/// <c>NetworkMiddlewares</c> entry) and the request's <c>middleware_name</c> must all agree, and
/// the request's config must equal the middleware config inside the approved plan.
/// </para>
/// <para>
/// Custody: the root lives outside the repository and outside any agent mount, is installed and
/// owned by the operator, files are read-only (0400/0600), and rotation replaces every file in
/// one step because the records cross-bind each other. See docs/openshell-preflight-runbook.md.
/// </para>
/// </remarks>
public sealed class OpenShellPreparationPreflight(
    HttpClient engine, string qualificationRoot, string hookPluginName,
    OpenShellBootstrapReceiverOptions receiver, IOpenShellCurrentPolicyReader? currentPolicy)
    : IOpenShellPreparationPreflight, IDisposable
{
    // The pinned binary itself is inside the seal: replacing hook AND hook-sha256 with a new
    // self-consistent pair while keeping the old replies is caught before any reply is trusted.
    private static readonly string[] SealedFiles = ["config.json", "describe.json", "hook", "validate-config.json", "validate-request.json"];

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
        var middlewares = spec.Policy?.NetworkMiddlewares;

        // Re-verify the static bootstrap invariants at the mutation boundary rather than trusting
        // that the compiler ran: digest-pinned image, empty driver/socket/environment surfaces,
        // restart-never, the fixed uid/gid pair, workdir never included, and the exact command.
        if (template is null || process is null || filesystem is null || middlewares is null
            || middlewares.Count != 1 || middlewares.Values.First().Config is null
            || middlewares.First().Key != middlewares.First().Value.Middleware
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
        VerifyRecordSet();

        // The configured registration, the registration the approved plan selected, and the
        // registration the recorded request was sent to must be the SAME middleware registration.
        var registration = middlewares.First().Key;
        if (hookPluginName != registration) throw Blocked("bootstrap_hook_registration_mismatch");

        // The recorded replies are the actual generated wire messages; internal validity alone
        // proves nothing about which hook or plan produced them — the request binds them.
        var manifest = Parse<MiddlewareManifest>(ReadText(Path.Combine(qualificationRoot, "describe.json")));
        if (string.IsNullOrWhiteSpace(manifest.ExpectedAudience) || manifest.ExpectedAudience.Length > 256
            || manifest.ExpectedAudience.Any(char.IsControl)
            || manifest.Extension?.ProtocolVersion is not { Major: 1, Minor: 0 }
            || !manifest.Extension.SupportedCapabilities.Contains("openshell.supervisor-middleware.contract")
            || !manifest.Bindings.Any(b => b.Operation == SupervisorMiddlewareOperation.HttpRequest
                && b.Phase == SupervisorMiddlewarePhase.PreCredentials && b.MaxPayloadBytes > 0
                && (b.RequestTimeout is null || (b.RequestTimeout.ToTimeSpan() >= TimeSpan.FromMilliseconds(10)
                    && b.RequestTimeout.ToTimeSpan() <= TimeSpan.FromSeconds(30)))))
            throw Blocked("bootstrap_hook_describe_unqualified");
        var request = Parse<ValidateConfigRequest>(ReadText(Path.Combine(qualificationRoot, "validate-request.json")));
        // The plan-selected registration and the approved plan's own middleware config — a record
        // set for another hook/config is refused even when it is internally consistent.
        if (request.Config is null) throw Blocked("bootstrap_hook_validate_request_config_missing");
        if (request.MiddlewareName != registration || !request.Config.Equals(middlewares.First().Value.Config))
            throw Blocked("bootstrap_hook_validate_request_unqualified");
        var response = Parse<ValidateConfigResponse>(ReadText(Path.Combine(qualificationRoot, "validate-config.json")));
        if (!response.Valid || response.Reason.Length != 0)
            throw Blocked("bootstrap_hook_validate_config_unqualified");

        // The external ingress is validated on its own; the backend receiver on its own. The
        // ingress front port and the receiver listen port are deliberately NOT required to be
        // equal (a fronting ingress may publish a different port) — only the API port is refused
        // as a receiver bind target.
        if (!Uri.TryCreate(plan.BootstrapIngress, UriKind.Absolute, out var ingress)
            || ingress.Scheme != "https" || ingress.AbsolutePath != "/identity/ready"
            || ingress.Query != "" || ingress.Fragment != "" || ingress.UserInfo != "")
            throw Blocked("bootstrap_ingress_unqualified");
        if (!receiver.Enabled || receiver.Port is < 1 or > 65535 or 3600
            || string.IsNullOrWhiteSpace(receiver.IngressDnsName)
            || !string.Equals(ingress.Host, receiver.IngressDnsName, StringComparison.OrdinalIgnoreCase))
            throw Blocked("bootstrap_receiver_binding_unqualified");

        // Current policy only through the authorized capabilities reader. Absent reader or an
        // absent/foreign policy refuses; a read failure propagates as refusal, never "unneeded".
        var live = currentPolicy is null ? throw Blocked("bootstrap_current_policy_unavailable")
            : await currentPolicy.ReadCurrentPolicyAsync(ct);
        if (live is null) throw Blocked("bootstrap_current_policy_unavailable");
        // Binds the plan's approval acknowledgement to the qualified config and the live policy.
        plan.ReadResourceAdmission().Validate(plan.Workspace, live);
        var config = ReadText(Path.Combine(qualificationRoot, "config.json"));
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

    /// <summary>Canonical framing: sorted names, UTF-8 name + NUL + decimal byte length + NUL + bytes.</summary>
    private void VerifyRecordSet()
    {
        var expected = ReadText(Path.Combine(qualificationRoot, "records-sha256")).Trim();
        if (expected.Length != 64 || !expected.All(char.IsAsciiHexDigitLower))
            throw Blocked("bootstrap_qualification_record_unqualified");
        using var sha = SHA256.Create();
        foreach (var name in SealedFiles.OrderBy(n => n, StringComparer.Ordinal))
        {
            var bytes = Read(Path.Combine(qualificationRoot, name));
            var header = Encoding.UTF8.GetBytes($"{name}\0{bytes.Length}\0");
            sha.TransformBlock(header, 0, header.Length, null, 0);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        if (Convert.ToHexStringLower(sha.Hash!) != expected) throw Blocked("bootstrap_qualification_record_unqualified");
    }

    private static T Parse<T>(string json) where T : class, IMessage, new()
    {
        try { return JsonParser.Default.Parse<T>(json); }
        catch (Exception ex) when (ex is InvalidProtocolBufferException or JsonException)
        { throw Blocked("bootstrap_qualification_record_malformed"); }
    }

    private byte[] Read(string path)
    {
        if (!File.Exists(path)) throw Blocked("bootstrap_artifact_missing");
        try { return File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw Blocked("bootstrap_artifact_missing"); }
    }

    private string ReadText(string path) => Encoding.UTF8.GetString(Read(path));

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
/// See docs/openshell-preflight-runbook.md for operator examples.
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
