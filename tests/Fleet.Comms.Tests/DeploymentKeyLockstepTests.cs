using System.Text.RegularExpressions;

namespace Fleet.Comms.Tests;

/// <summary>
/// Every operator-facing key exists in the deployment files, and the compose file's substitutions
/// exist in <c>.env.example</c>.
/// </summary>
/// <remarks>
/// <para>
/// A key the code reads and no deployment file mentions is a setting nobody can discover. It works
/// fine for whoever added it — their environment already has the value — and breaks for every fresh
/// install, which is the shape that gets shipped because the author's own deployment is the one
/// place it cannot fail.
/// </para>
/// <para>
/// Asserted by reading the files rather than by a checklist in a pull request, because a checklist
/// is satisfied by ticking it.
/// </para>
/// </remarks>
public class DeploymentKeyLockstepTests
{
    /// <summary>
    /// The conversation feature's operator keys, and where each must appear.
    /// </summary>
    /// <remarks>
    /// Committed as data. Adding a key to the options class without adding it here is possible; what
    /// is not possible is adding it here and shipping it undocumented.
    /// </remarks>
    public static TheoryData<string> ConversationKeys() =>
    [
        "FLEET_COMMS_CONVERSATION_DB",
        "FLEET_COMMS_CONVERSATION_MIGRATION_DB",
        "FLEET_COMMS_SOUTH_BIND",
        "FLEET_COMMS_SOUTH_TOKEN",
        "FLEET_COMMS_AGENT_NAME",
        "FLEET_COMMS_BROKER",
        "FLEET_COMMS_CLAIM_RETENTION",
    ];

    [Theory]
    [MemberData(nameof(ConversationKeys))]
    public void Every_conversation_key_is_documented_in_the_env_example(string key)
    {
        Assert.Contains(key, Read(".env.example"), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ConversationKeys))]
    public void Every_conversation_key_is_wired_in_the_example_compose(string key)
    {
        Assert.Contains($"${{{key}", Read("docker-compose.example.yml"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Every <c>${...}</c> substitution the compose file makes on the comms services is present in
    /// <c>.env.example</c>.
    /// </summary>
    /// <remarks>
    /// The other direction of the same rule, and the one that catches a key added to compose in a
    /// hurry. Scoped to <c>FLEET_COMMS_</c> because that is this feature's namespace; the wider file
    /// has its own conventions and is not this test's business.
    /// </remarks>
    [Fact]
    public void Every_comms_substitution_in_the_compose_file_exists_in_the_env_example()
    {
        var compose = Read("docker-compose.example.yml");
        var env = Read(".env.example");

        var referenced = Regex.Matches(compose, @"\$\{(FLEET_COMMS_[A-Z0-9_]+)")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // The list is non-trivial, so a regex that silently matched nothing cannot pass this.
        Assert.NotEmpty(referenced);

        var undocumented = referenced
            .Where(key => !env.Contains(key, StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(undocumented);
    }

    /// <summary>
    /// The south listener is never published as a host port.
    /// </summary>
    /// <remarks>
    /// It carries an administrative surface: a caller who reached <c>/turns:commit</c> could write a
    /// terminal for someone else's turn. Asserted against the compose file because that is where the
    /// mistake would be made — a `ports:` entry for 8082 added while debugging and never removed.
    /// </remarks>
    [Fact]
    public void The_south_listener_is_not_published_as_a_host_port()
    {
        var compose = Read("docker-compose.example.yml");

        var published = Regex.Matches(compose, @"^\s*-\s*""[^""]*:(\d+)""\s*$", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToArray();

        Assert.DoesNotContain("8082", published);
    }

    /// <summary>
    /// The conversation database publishes no host port either.
    /// </summary>
    /// <remarks>
    /// It holds conversation history. It is reached by the service over the Docker network and by
    /// nothing else — a published port would put it on a host interface, which is a different
    /// exposure from the one the operator agreed to when they published the client API.
    /// </remarks>
    [Fact]
    public void The_conversation_database_publishes_no_host_port()
    {
        var compose = Read("docker-compose.example.yml");

        var service = compose[compose.IndexOf("  comms-mysql:", StringComparison.Ordinal)..];
        var end = service.IndexOf("\n  fleet-comms-ops:", StringComparison.Ordinal);
        if (end > 0) service = service[..end];

        Assert.DoesNotContain("ports:", service, StringComparison.Ordinal);
    }

    /// <summary>
    /// The runtime account is provisioned WITHOUT DDL grants.
    /// </summary>
    /// <remarks>
    /// This is the mutation the design names — "grant DDL to the runtime account" — expressed where
    /// the grant is actually written. With DDL, a process that migrated on startup would succeed,
    /// and the property that makes refusing to migrate meaningful would be gone while every other
    /// assertion still passed.
    /// </remarks>
    [Fact]
    public void The_provisioned_runtime_account_holds_no_ddl_grant()
    {
        var sql = Read(Path.Combine("deploy", "comms-mysql-init", "01-accounts.sql"));

        var runtimeGrant = sql.Split('\n')
            .Single(line => line.Contains("TO 'comms_runtime'@", StringComparison.Ordinal));

        Assert.Contains("SELECT, INSERT, UPDATE, DELETE", runtimeGrant, StringComparison.Ordinal);

        foreach (var ddl in new[] { "ALL PRIVILEGES", "CREATE", "ALTER", "DROP", "INDEX", "REFERENCES" })
            Assert.DoesNotContain(ddl, runtimeGrant, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryRoot().FullName, relativePath));

    private static DirectoryInfo RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fleet.sln")))
            directory = directory.Parent;

        Assert.True(directory is not null, "could not locate the repository root");
        return directory!;
    }
}
