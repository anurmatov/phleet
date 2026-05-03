using Fleet.Memory.Configuration;
using Fleet.Memory.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Memory.Tests;

/// <summary>
/// Unit tests for AclCacheService.CanRead() — verifies project-scoped ACL logic without
/// a live orchestrator or RabbitMQ connection.
///
/// AclCacheService is exercised via its internal InjectAcl helper (test-only) and the
/// public CanRead() / IsAclEnabled / IsAvailable surface.
/// </summary>
public class MemoryAclTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates an AclCacheService with ACL enabled and a pre-populated cache.
    /// Bypasses HTTP and RabbitMQ (StartAsync is not called).
    /// </summary>
    private static AclCacheService MakeService(
        Dictionary<string, List<string>> acl,
        string[] publicProjects = null!,
        string operatorAgent = "")
    {
        var aclOpts = Options.Create(new AclOptions
        {
            EnableProjectScopedAcl = true,
            AclPublicProjects = publicProjects ?? [],
            AclOperatorAgent = operatorAgent,
        });
        var orchOpts = Options.Create(new OrchestratorOptions { BaseUrl = "http://unused" });
        var svc = new AclCacheService(aclOpts, orchOpts, NullLogger<AclCacheService>.Instance);
        svc.InjectAclForTesting(acl);
        return svc;
    }

    /// <summary>Creates a service with ACL disabled (feature flag off).</summary>
    private static AclCacheService MakeDisabled()
    {
        var aclOpts = Options.Create(new AclOptions { EnableProjectScopedAcl = false });
        var orchOpts = Options.Create(new OrchestratorOptions { BaseUrl = "http://unused" });
        var svc = new AclCacheService(aclOpts, orchOpts, NullLogger<AclCacheService>.Instance);
        svc.InjectAclForTesting([]);
        return svc;
    }

    // ── EnableProjectScopedAcl = false bypasses all filtering ─────────────────

    [Fact]
    public void Disabled_AllAgentsReadEverything()
    {
        var svc = MakeDisabled();
        var (allowed, _) = svc.CanRead("agent-x", "beta");
        Assert.True(allowed);
    }

    [Fact]
    public void Disabled_NoProjectMemoryReadable()
    {
        var svc = MakeDisabled();
        var (allowed, _) = svc.CanRead("agent-x", "");
        Assert.True(allowed);
    }

    // ── Agent with allow-list ["alpha"] ───────────────────────────────────────

    [Fact]
    public void AllowedProject_CanRead()
    {
        var svc = MakeService(new() { ["agent-a"] = ["alpha"] });
        var (allowed, _) = svc.CanRead("agent-a", "alpha");
        Assert.True(allowed);
    }

    [Fact]
    public void DeniedProject_CannotRead()
    {
        var svc = MakeService(new() { ["agent-a"] = ["alpha"] });
        var (allowed, reason) = svc.CanRead("agent-a", "beta");
        Assert.False(allowed);
        Assert.NotNull(reason);
    }

    [Fact]
    public void NoProjectSet_DeniedToNonWildcard()
    {
        var svc = MakeService(new() { ["agent-a"] = ["alpha"] });
        var (allowed, _) = svc.CanRead("agent-a", "");
        Assert.False(allowed);
    }

    [Fact]
    public void NoProjectSet_DeniedToNullProject()
    {
        var svc = MakeService(new() { ["agent-a"] = ["alpha"] });
        var (allowed, _) = svc.CanRead("agent-a", null);
        Assert.False(allowed);
    }

    // ── Agent with no rows (empty allow-list) ─────────────────────────────────

    [Fact]
    public void AgentNoRows_CannotReadProjectBeta()
    {
        var svc = MakeService(new()); // empty ACL map
        var (allowed, _) = svc.CanRead("new-agent", "beta");
        Assert.False(allowed);
    }

    [Fact]
    public void AgentNoRows_CannotReadNoProjectMemory()
    {
        var svc = MakeService(new());
        var (allowed, _) = svc.CanRead("new-agent", "");
        Assert.False(allowed);
    }

    // ── Wildcard agent ────────────────────────────────────────────────────────

    [Fact]
    public void WildcardAgent_CanReadAllProjects()
    {
        var svc = MakeService(new() { ["acto"] = ["*"] });
        Assert.True(svc.CanRead("acto", "alpha").allowed);
        Assert.True(svc.CanRead("acto", "beta").allowed);
        Assert.True(svc.CanRead("acto", "").allowed);  // no-project memory
    }

    [Fact]
    public void WildcardAgent_CanReadNullProject()
    {
        var svc = MakeService(new() { ["acto"] = ["*"] });
        var (allowed, _) = svc.CanRead("acto", null);
        Assert.True(allowed);
    }

    // ── Public projects ───────────────────────────────────────────────────────

    [Fact]
    public void PublicProject_AnyAgentCanRead()
    {
        var svc = MakeService(
            acl: new() { ["agent-a"] = ["alpha"] },
            publicProjects: ["runbooks"]);
        var (allowed, _) = svc.CanRead("agent-a", "runbooks");
        Assert.True(allowed);
    }

    [Fact]
    public void PublicProject_AgentWithNoRows_CanRead()
    {
        var svc = MakeService(
            acl: new(),
            publicProjects: ["runbooks"]);
        var (allowed, _) = svc.CanRead("new-agent", "runbooks");
        Assert.True(allowed);
    }

    // ── Agent name case-insensitivity ─────────────────────────────────────────

    [Fact]
    public void AgentNameCaseInsensitive()
    {
        var svc = MakeService(new() { ["agent-a"] = ["alpha"] });
        Assert.True(svc.CanRead("AGENT-A", "alpha").allowed);
        Assert.True(svc.CanRead("Agent-A", "alpha").allowed);
    }

    // ── IsAclEnabled flag ─────────────────────────────────────────────────────

    [Fact]
    public void IsAclEnabled_TrueWhenFlagOn()
    {
        var svc = MakeService(new());
        Assert.True(svc.IsAclEnabled);
    }

    [Fact]
    public void IsAclEnabled_FalseWhenFlagOff()
    {
        var svc = MakeDisabled();
        Assert.False(svc.IsAclEnabled);
    }
}
