using System.Text.Json;
using Fleet.Orchestrator.Application;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// #593 A3/A4: mapped MCP headers through the real <see cref="ContainerProvisioningService"/>. Values
/// come from the env file and the secret plan one provision captured; a fault refuses before the old
/// container or any generated file is touched. All names, hosts and values are synthetic.
/// </summary>
public class McpHeaderEnvTests
{
    private const string Url = "https://mcp.example.test/mcp";
    private const string Host = "mcp.example.test";
    private const string KeyRef = "EXAMPLE_KEY";
    private const string KeyAlias = "EXAMPLE_ALIAS";
    private const string SecretRef = "EXAMPLE_SECRET";
    private const string PlainRef = "PLAIN_TOKEN";
    private const string KeyValue = "synthetic-key-value-3f9e1a";
    private const string SecretValue = "synthetic-secret-value-c07d52";
    private const string PlainValue = "synthetic-plain-value-8b44e0";
    private const string GeminiKeyRef = ProvisioningFaults.GeminiApiKeyEnvRef;
    private const string GeminiKeyValue = "synthetic-gemini-key-51ce07";

    /// <summary>The variable the native acceptance script sets to receive the fixture (A7).</summary>
    public const string FixtureDirectoryVariable = "FLEET_MCP_HEADERS_FIXTURE_DIR";

    private static readonly Dictionary<string, string?> Enabled = new() { ["SecretProxy:Enabled"] = "true" };

    private static string Mappings(params (string Header, string Ref)[] pairs) =>
        McpHeaderMappings.Serialize([.. pairs.Select(p => new McpHeaderEnvRef(p.Header, p.Ref))])!;

    private static Agent NewAgent(string provider = "claude") => new()
    {
        Name = "agent1",
        DisplayName = "agent1",
        Role = "developer",
        Model = provider == "codex" ? "gpt-5.5" : provider == "gemini" ? "gemini-4-argon-medium" : "claude-sonnet-4-6",
        Provider = provider,
        ContainerName = "fleet-agent1",
        MemoryLimitMb = 512,
        EnvRefs = [new() { EnvKeyName = KeyRef }, new() { EnvKeyName = SecretRef }, new() { EnvKeyName = PlainRef }],
        Tools = [new() { ToolName = "mcp__example__ping" }],
        McpEndpoints =
        [
            new() { McpName = "example", Url = Url, TransportType = "http",
                HeaderEnvRefsJson = Mappings(("X-Example-Key", KeyRef), ("X-Example-Secret", SecretRef)) },
            new() { McpName = "plain", Url = "http://plain.example.test/mcp", TransportType = "http",
                HeaderEnvRefsJson = Mappings(("X-Plain-Token", PlainRef)) },
        ],
    };

    private static List<SecretBinding> Bindings() =>
    [
        new() { EnvKeyName = KeyRef, Hosts = Host, HeaderName = "X-Example-Key", ExposeAs = KeyAlias },
        new() { EnvKeyName = SecretRef, Hosts = Host, HeaderName = "X-Example-Secret" },
    ];

    private static async Task<ProvisioningHarness> StartAsync(Action<OrchestratorDbContext>? seed = null, bool enabled = true)
    {
        var harness = ProvisioningHarness.Create(enabled ? Enabled : null);
        File.AppendAllText(harness.EnvFile, $"{KeyRef}={KeyValue}\n{SecretRef}={SecretValue}\n{PlainRef}={PlainValue}\n");
        var ca = Path.Combine(harness.BaseDir, SecretProxyState.CaDirectory);
        Directory.CreateDirectory(ca);
        File.WriteAllText(Path.Combine(ca, SecretProxyState.CaCertificateFile), "synthetic certificate\n");
        await harness.SeedAsync(db =>
        {
            if (seed is not null) { seed(db); return; }
            db.SecretBindings.AddRange(Bindings());
            db.Agents.Add(NewAgent());
        });
        return harness;
    }

    private static string McpPath(ProvisioningHarness harness) => Path.Combine(harness.GeneratedDir("fleet-agent1"), ".mcp.json");

    private static JsonElement Headers(ProvisioningHarness harness, string server) =>
        JsonDocument.Parse(File.ReadAllText(McpPath(harness))).RootElement.GetProperty("mcpServers").GetProperty(server).GetProperty("headers");

    private static string EnvOf(string createBody, string name) =>
        JsonDocument.Parse(createBody).RootElement.GetProperty("Env").EnumerateArray().Select(e => e.GetString()!)
            .Single(e => e.StartsWith(name + "=", StringComparison.Ordinal))[(name.Length + 1)..];

    // ── A3: resolution from one captured plan ─────────────────────────────────

    [Fact]
    public async Task Bound_references_render_only_their_plan_placeholders_and_unbound_the_captured_value()
    {
        await using var harness = await StartAsync();

        var result = await harness.Service.ProvisionAsync("agent1");

        Assert.True(result.Success, result.Message);
        var body = harness.CreateBodies.Single();
        var keyPlaceholder = EnvOf(body, KeyAlias);       // exposed under its alias, mapped by its own name
        var secretPlaceholder = EnvOf(body, SecretRef);
        Assert.True(SecretPlaceholders.IsPlaceholder(keyPlaceholder));
        Assert.NotEqual(keyPlaceholder, secretPlaceholder);

        var example = Headers(harness, "example");
        Assert.Equal(keyPlaceholder, example.GetProperty("X-Example-Key").GetString());
        Assert.Equal(secretPlaceholder, example.GetProperty("X-Example-Secret").GetString());
        Assert.Equal(PlainValue, Headers(harness, "plain").GetProperty("X-Plain-Token").GetString());

        // No bound value anywhere it could be read by the agent or an operator.
        foreach (var file in Directory.EnumerateFiles(Path.Combine(harness.BaseDir, "workspaces"), "*", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain(KeyValue, text);
            Assert.DoesNotContain(SecretValue, text);
        }
        var logs = string.Join("\n", harness.Logs.Entries.Select(e => e.Message)) + result.Message;
        foreach (var secret in new[] { KeyValue, SecretValue, PlainValue, keyPlaceholder, secretPlaceholder })
            Assert.DoesNotContain(secret, logs);

        // The flag that lets a header-carrying server through, without the journal.
        var agent = JsonDocument.Parse(File.ReadAllText(Path.Combine(harness.GeneratedDir("fleet-agent1"), "appsettings.json"))).RootElement;
        Assert.True(agent.GetProperty("Agent").GetProperty("McpHeaderSupport").GetBoolean());
        Assert.False(agent.TryGetProperty("Journal", out _));
    }

    [Fact]
    public async Task A_value_changed_after_capture_does_not_reach_the_rendered_headers()
    {
        await using var harness = await StartAsync();
        Assert.True((await harness.Service.ProvisionAsync("agent1")).Success);
        harness.ContainerExists = true;

        // Between the captured plan and the file writes: the old container is being stopped.
        harness.OnDockerRequest = request =>
        {
            if (request.EndsWith("/stop", StringComparison.Ordinal))
                File.WriteAllText(harness.EnvFile, File.ReadAllText(harness.EnvFile).Replace(PlainValue, "synthetic-changed-later"));
            return Task.CompletedTask;
        };

        var result = await harness.Service.ReprovisionAsync("agent1");

        Assert.True(result.Success, result.Message);
        Assert.Contains("synthetic-changed-later", File.ReadAllText(harness.EnvFile));
        Assert.Equal(PlainValue, Headers(harness, "plain").GetProperty("X-Plain-Token").GetString());
        Assert.Equal(PlainValue, EnvOf(harness.CreateBodies.Last(), PlainRef));
    }

    [Fact]
    public async Task The_generated_mcp_json_is_owner_only_even_when_it_existed()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var harness = await StartAsync();
        Directory.CreateDirectory(harness.GeneratedDir("fleet-agent1"));
        File.WriteAllText(McpPath(harness), "{}");
        File.SetUnixFileMode(McpPath(harness), (UnixFileMode)0b110_100_100);   // 0644

        Assert.True((await harness.Service.ProvisionAsync("agent1")).Success);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(McpPath(harness)));
        // The owner, which is the process that wrote it, still reads it.
        Assert.Equal(PlainValue, Headers(harness, "plain").GetProperty("X-Plain-Token").GetString());
    }

    [Fact]
    public void Headerless_agents_render_the_bytes_they_always_did()
    {
        var agent = NewAgent();
        foreach (var endpoint in agent.McpEndpoints) endpoint.HeaderEnvRefsJson = null;

        var (headers, fault) = AgentConfigFiles.ResolveMcpHeaders(agent, new Dictionary<string, string>(), [], SecretPlan.None);

        Assert.Null(fault);
        Assert.Empty(headers);
        Assert.Equal(AgentConfigFiles.GenerateMcpJson(agent, "http://fleet-memory:3100"),
            AgentConfigFiles.GenerateMcpJson(agent, "http://fleet-memory:3100", mappedHeaders: headers));
        Assert.Equal(AgentConfigFiles.GenerateAppsettingsJson(agent, "cto-agent"),
            AgentConfigFiles.GenerateAppsettingsJson(agent, "cto-agent", mappedMcpHeaders: false));
        Assert.DoesNotContain("McpHeaderSupport", AgentConfigFiles.GenerateAppsettingsJson(agent, "cto-agent"));
        Assert.DoesNotContain("headers", AgentConfigFiles.GenerateMcpJson(agent, "http://fleet-memory:3100"));
    }

    /// <summary>
    /// Gemini is the Antigravity CLI, which sends an endpoint's configured headers on every MCP
    /// request, like the other two. A provider that is not in the table sends none.
    /// </summary>
    [Fact]
    public void Every_known_provider_sends_headers_and_one_outside_the_table_does_not()
    {
        Assert.True(AgentConfigFiles.McpHeaderSupport["gemini"]);
        Assert.True(AgentConfigFiles.McpHeaderSupport["claude"]);
        Assert.True(AgentConfigFiles.McpHeaderSupport["codex"]);
        Assert.Equal(AgentProviders.All.Order(), AgentConfigFiles.McpHeaderSupport.Keys.Order());

        Assert.False(AgentConfigFiles.SupportsMcpHeaders("other-harness"));
        var fault = McpHeaderMappings.DescribeEndpointFault("example", Url, "http", "other-harness");
        Assert.Equal(McpHeaderMappings.Unsupported, fault?.Code);
        foreach (var provider in AgentProviders.All)
            Assert.Null(McpHeaderMappings.DescribeEndpointFault("example", Url, "http", provider));
    }

    /// <summary>
    /// A stored mapping on an agent whose provider sends no headers refuses the provision by the
    /// mapping rule, wherever the provider check that comes before it would let it through.
    /// </summary>
    [Fact]
    public void A_stored_mapping_on_a_provider_that_sends_no_headers_is_a_fault_naming_no_value()
    {
        var agent = NewAgent();
        agent.Provider = "other-harness";

        var (headers, fault) = AgentConfigFiles.ResolveMcpHeaders(
            agent, new Dictionary<string, string> { [KeyRef] = KeyValue, [SecretRef] = SecretValue, [PlainRef] = PlainValue }, [], SecretPlan.None);

        Assert.StartsWith(McpHeaderMappings.Unsupported, fault);
        Assert.Empty(headers);
        foreach (var secret in new[] { KeyValue, SecretValue, PlainValue })
            Assert.DoesNotContain(secret, fault);
    }

    /// <summary>
    /// A gemini agent with mapped headers provisions, and its <c>.mcp.json</c> carries for each
    /// endpoint exactly what a claude and a codex agent with the same rows get: the bound
    /// references as that provision's placeholders, the unbound one as its captured value.
    /// </summary>
    [Fact]
    public async Task A_gemini_agent_with_mapped_headers_provisions_and_gets_what_claude_and_codex_get()
    {
        var rendered = new Dictionary<string, string>();
        foreach (var provider in new[] { "claude", "codex", "gemini" })
        {
            await using var harness = await StartAsync(db =>
            {
                db.SecretBindings.AddRange(Bindings());
                var agent = NewAgent(provider);
                if (provider == "gemini") agent.EnvRefs.Add(new() { EnvKeyName = GeminiKeyRef });
                db.Agents.Add(agent);
            });
            File.AppendAllText(harness.EnvFile, $"{GeminiKeyRef}={GeminiKeyValue}\n");

            var result = await harness.Service.ProvisionAsync("agent1");

            Assert.True(result.Success, result.Message);
            var body = harness.CreateBodies.Single();
            var example = Headers(harness, "example");
            Assert.Equal(EnvOf(body, KeyAlias), example.GetProperty("X-Example-Key").GetString());
            Assert.Equal(EnvOf(body, SecretRef), example.GetProperty("X-Example-Secret").GetString());
            Assert.True(SecretPlaceholders.IsPlaceholder(example.GetProperty("X-Example-Key").GetString()!));
            Assert.Equal(PlainValue, Headers(harness, "plain").GetProperty("X-Plain-Token").GetString());
            var appsettings = JsonDocument.Parse(File.ReadAllText(Path.Combine(harness.GeneratedDir("fleet-agent1"), "appsettings.json"))).RootElement;
            Assert.True(appsettings.GetProperty("Agent").GetProperty("McpHeaderSupport").GetBoolean());

            // Placeholders are minted per provision; with them named, the file is the same file.
            rendered[provider] = File.ReadAllText(McpPath(harness))
                .Replace(EnvOf(body, KeyAlias), "<key-placeholder>").Replace(EnvOf(body, SecretRef), "<secret-placeholder>");
        }

        Assert.Equal(rendered["claude"], rendered["gemini"]);
        Assert.Equal(rendered["codex"], rendered["gemini"]);
        Assert.Contains("\"X-Plain-Token\": \"" + PlainValue + "\"", rendered["gemini"]);
        Assert.Contains("fa1.agent.agent1.", rendered["gemini"]);
    }

    [Theory]
    [InlineData("synthetic\rvalue")]
    [InlineData("synthetic\nvalue")]
    [InlineData("synthetic\0value")]
    [InlineData("")]
    [InlineData("<secret>")]
    public void An_unusable_unbound_value_is_refused_without_echoing_it(string value)
    {
        var agent = NewAgent();
        agent.McpEndpoints.RemoveAll(e => e.McpName == "example");

        var (_, fault) = AgentConfigFiles.ResolveMcpHeaders(agent, new Dictionary<string, string> { [PlainRef] = value }, [], SecretPlan.None);

        Assert.StartsWith(McpHeaderMappings.ValueMissing, fault);
        if (value.Length > 0) Assert.DoesNotContain("synthetic", fault);
    }

    // ── A4: a refusal keeps the old container and the generated files ─────────

    public static TheoryData<string, string> Faults => new()
    {
        { "malformed", McpHeaderMappings.Invalid },
        { "dangling", McpHeaderMappings.RefMissing },
        { "missing-value", McpHeaderMappings.ValueMissing },
        { "nul-value", McpHeaderMappings.ValueMissing },
        // A switch to gemini no longer loses the headers: what it lacks is the agent's API key.
        { "gemini-without-its-key", ProvisioningFaults.GeminiApiKeyMissing },
        { "sse", McpHeaderMappings.Unsupported },
        { "other-host", McpHeaderMappings.BindingMismatch },
        { "other-header", McpHeaderMappings.BindingMismatch },
        { "plain-http", McpHeaderMappings.BindingMismatch },
        { "other-port", McpHeaderMappings.BindingMismatch },
        { "ssh", McpHeaderMappings.BindingMismatch },
        { "userinfo-unbound", McpHeaderMappings.Invalid },
        { "userinfo-bound", McpHeaderMappings.Invalid },
        { "not-http", McpHeaderMappings.Invalid },
    };

    [Theory]
    [MemberData(nameof(Faults))]
    public async Task A_fault_refuses_before_the_old_container_or_any_generated_file_is_touched(string fault, string code)
    {
        await using var first = await StartAsync();
        Assert.True((await first.Service.ProvisionAsync("agent1")).Success);
        var before = Directory.EnumerateFiles(first.GeneratedDir("fleet-agent1"), "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, File.ReadAllBytes);
        var creates = first.CreateBodies.Count;
        var requests = first.DockerRequests.Count;
        first.ContainerExists = true;

        await first.MutateAsync(async db =>
        {
            var agent = await db.Agents.Include(a => a.McpEndpoints).Include(a => a.EnvRefs).SingleAsync();
            var example = agent.McpEndpoints.Single(e => e.McpName == "example");
            var binding = await db.SecretBindings.SingleAsync(b => b.EnvKeyName == SecretRef);
            switch (fault)
            {
                case "malformed": example.HeaderEnvRefsJson = """[{"headerName":"X-Example-Key","envRefName":"EXAMPLE_KEY","extra":1}]"""; break;
                case "dangling": db.AgentEnvRefs.Remove(agent.EnvRefs.Single(r => r.EnvKeyName == PlainRef)); break;
                case "missing-value": File.WriteAllText(first.EnvFile, File.ReadAllText(first.EnvFile).Replace($"{PlainRef}={PlainValue}\n", "")); break;
                case "nul-value": File.AppendAllText(first.EnvFile, $"{PlainRef}=synthetic\0nul\n"); break;
                case "gemini-without-its-key": agent.Provider = "gemini"; break;
                case "sse": example.TransportType = "sse"; break;
                case "other-host": example.Url = "https://other.example.test/mcp"; break;
                case "other-header": example.HeaderEnvRefsJson = Mappings(("X-Example-Key", KeyRef), ("X-Other", SecretRef)); break;
                case "plain-http": example.Url = "http://mcp.example.test/mcp"; break;
                case "other-port": example.Url = "https://mcp.example.test:8443/mcp"; break;
                // A credential in a hand-edited URL is refused whether the endpoint's references are bound or not.
                case "userinfo-unbound": agent.McpEndpoints.Single(e => e.McpName == "plain").Url = "http://user:synthetic-pw@plain.example.test/mcp"; break;
                case "userinfo-bound": example.Url = "https://user:synthetic-pw@mcp.example.test/mcp"; break;
                case "not-http": agent.McpEndpoints.Single(e => e.McpName == "plain").Url = "ftp://plain.example.test/mcp"; break;
                case "ssh":
                    binding.Kind = SecretBindingRules.KindSshKey;
                    binding.Hosts = "deploy@" + Host;
                    binding.SshKnownHosts = Host + " ssh-ed25519 " + new string('A', 68);
                    break;
            }
        });

        var result = await first.Service.ReprovisionAsync("agent1");

        Assert.False(result.Success);
        Assert.StartsWith(code, result.Message);
        foreach (var secret in new[] { KeyValue, SecretValue, PlainValue, "synthetic-pw" })
            Assert.DoesNotContain(secret, result.Message);
        Assert.Equal(creates, first.CreateBodies.Count);
        Assert.DoesNotContain(first.DockerRequests.Skip(requests), r => r.StartsWith("DELETE ", StringComparison.Ordinal) || r.EndsWith("/stop", StringComparison.Ordinal));
        foreach (var (file, bytes) in before)
            Assert.Equal(bytes, File.ReadAllBytes(file));
    }

    [Fact]
    public async Task With_the_forwarder_off_a_mapped_bound_reference_refuses_and_keeps_the_old_files()
    {
        await using var harness = await StartAsync(enabled: false);
        Directory.CreateDirectory(harness.GeneratedDir("fleet-agent1"));
        File.WriteAllText(McpPath(harness), "{\"previous\":true}");
        harness.ContainerExists = true;

        var result = await harness.Service.ReprovisionAsync("agent1");

        Assert.False(result.Success);
        Assert.StartsWith("secret_proxy_disabled", result.Message);
        Assert.Empty(harness.CreateBodies);
        Assert.DoesNotContain(harness.DockerRequests, r => r.StartsWith("DELETE ", StringComparison.Ordinal) || r.EndsWith("/stop", StringComparison.Ordinal));
        Assert.Equal("{\"previous\":true}", File.ReadAllText(McpPath(harness)));
    }

    // ── A7: the fixture the native acceptance script runs a real client against ─

    /// <summary>
    /// Renders mapped headers, mandatory agent identity and the config header through production helpers. When
    /// <see cref="FixtureDirectoryVariable"/> is set (by <c>tests/secret-proxy/mcp-headers.sh</c>), it
    /// also writes the files that script starts the real clients from.
    /// </summary>
    [Fact]
    public void Native_fixture_export()
    {
        var bindings = new List<SecretBinding>
        {
            new() { EnvKeyName = KeyRef, Hosts = Host, HeaderName = "X-Example-Key" },
            new() { EnvKeyName = SecretRef, Hosts = Host, HeaderName = "X-Example-Secret" },
        };
        var files = new Dictionary<string, string>();
        // Minted once and handed to both plans, so both client configurations carry the same placeholders.
        string[] minted = [SecretPlaceholders.Create(), SecretPlaceholders.Create()];
        var placeholders = new Dictionary<string, string>();
        foreach (var provider in new[] { "claude", "codex" })
        {
            var agent = NewAgent(provider);
            agent.Id = 1;
            agent.EnvRefs.RemoveAll(r => r.EnvKeyName == PlainRef);
            agent.McpEndpoints.RemoveAll(e => e.McpName == "plain");
            agent.McpEndpoints[0].McpName = "fleet-orchestrator";
            agent.McpEndpoints[0].HeaderEnvRefsJson = Mappings(
                ("X-Example-Key", KeyRef), ("X-Example-Secret", SecretRef),
                ("X-Fleet-Config-Token", "ORCHESTRATOR_CONFIG_TOKEN"));
            agent.EnvRefs.Add(new() { EnvKeyName = "ORCHESTRATOR_CONFIG_TOKEN" });
            agent.Tools = [new() { ToolName = "mcp__fleet-orchestrator__ping" }];
            var next = 0;
            var (plan, planFault) = SecretGrantPlanner.Plan(agent, bindings, SecretProxySettings.ForTests(), caPresent: true, () => minted[next++]);
            Assert.Null(planFault);
            foreach (var grant in plan!.Grants) placeholders[grant.EnvKeyName] = grant.Placeholder;

            var (headers, fault) = AgentConfigFiles.ResolveMcpHeaders(agent, new Dictionary<string, string> { ["ORCHESTRATOR_CONFIG_TOKEN"] = "synthetic-config-token" }, bindings, plan!);
            Assert.Null(fault);
            var identity = AgentConfigFiles.IdentityTokenFor(agent, [new byte[32]]);
            var identityHeaders = AgentConfigFiles.WithIdentityHeaders(agent, headers, identity,
                ["https://mcp.example.test", "http://fleet-memory:3100"], "http://fleet-memory:3100");
            files[$"{provider}.mcp.json"] = AgentConfigFiles.GenerateMcpJson(agent, "http://fleet-memory:3100", mappedHeaders: identityHeaders);
            files[$"{provider}.appsettings.json"] = AgentConfigFiles.GenerateAppsettingsJson(agent, "cto-agent", mappedMcpHeaders: headers.Count > 0);
        }

        var server = JsonDocument.Parse(files["claude.mcp.json"]).RootElement.GetProperty("mcpServers").GetProperty("fleet-orchestrator");
        Assert.Equal(Url, server.GetProperty("url").GetString());
        Assert.Equal(placeholders[KeyRef], server.GetProperty("headers").GetProperty("X-Example-Key").GetString());
        Assert.Equal(placeholders[SecretRef], server.GetProperty("headers").GetProperty("X-Example-Secret").GetString());
        Assert.Equal(files["claude.mcp.json"], files["codex.mcp.json"]);
        var authorization = server.GetProperty("headers").GetProperty("Authorization").GetString()!;
        Assert.True(AgentIdentityTokens.TryVerify(AgentIdentityTokens.BearerOf(authorization),
            [new byte[32]], out var purpose, out var subject));
        Assert.Equal("agent", purpose);
        Assert.Equal("agent1", subject);
        Assert.Equal("synthetic-config-token", server.GetProperty("headers").GetProperty("X-Fleet-Config-Token").GetString());
        Assert.DoesNotContain(new string('A', 43), string.Join("", files.Values));
        Assert.DoesNotContain("fa1.service.", string.Join("", files.Values));
        files["native-identity.json"] = JsonSerializer.Serialize(new
            { authorization, configToken = "synthetic-config-token" });

        if (Environment.GetEnvironmentVariable(FixtureDirectoryVariable) is not { Length: > 0 } directory)
            return;
        Directory.CreateDirectory(directory);
        foreach (var (name, text) in files)
            File.WriteAllText(Path.Combine(directory, name), text);
        File.WriteAllText(Path.Combine(directory, "placeholders.env"), string.Concat(placeholders.Select(p =>
            $"{p.Key}_PLACEHOLDER={p.Value}\n{p.Key}_SHA256={SecretPlaceholders.Hash(p.Value)}\n")));
    }
}
