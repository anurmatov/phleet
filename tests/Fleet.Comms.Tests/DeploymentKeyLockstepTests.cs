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
        "FLEET_COMMS_MYSQL_DDL_PASSWORD",
        "FLEET_COMMS_MYSQL_RUNTIME_PASSWORD",
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

    /// <summary>The journal's operator keys (#375). Same two rules as the conversation keys.</summary>
    public static TheoryData<string> JournalKeys() =>
    [
        "FLEET_COMMS_JOURNAL_ENABLED",
        "FLEET_COMMS_JOURNAL_BIND",
        "FLEET_COMMS_JOURNAL_KEY",
        "FLEET_COMMS_JOURNAL_EXCLUDED_CHAT_IDS",
        "FLEET_COMMS_JOURNAL_RETENTION",
        "FLEET_COMMS_JOURNAL_READ_ALL_SUBJECTS",
    ];

    [Theory]
    [MemberData(nameof(JournalKeys))]
    public void Every_journal_key_is_documented_in_the_env_example(string key)
    {
        Assert.Contains(key, Read(".env.example"), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(JournalKeys))]
    public void Every_journal_key_is_wired_in_the_example_compose(string key)
    {
        Assert.Contains($"${{{key}", Read("docker-compose.example.yml"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Both scripts unset every journal key before they read <c>.env</c>, so a stale export in the
    /// operator's shell cannot override the recorded decision — for the read-all grant (#394), a
    /// forgotten export would silently widen a reader's scope on the next recreate.
    /// </summary>
    [Theory]
    [MemberData(nameof(JournalKeys))]
    public void Every_journal_key_is_unset_by_setup_and_upgrade(string key)
    {
        Assert.Contains(key, UnsetNames(Read("setup.sh")));
        Assert.Contains(key, UnsetNames(Read("upgrade.sh")));
    }

    /// <summary>
    /// The read-all grant reaches the one service that serves the read tools. The operator one-shot
    /// never serves a read, and a copy of the list anywhere else is a second place it can drift.
    /// </summary>
    [Fact]
    public void The_read_all_grant_is_given_only_to_fleet_comms()
    {
        var compose = Read("docker-compose.example.yml");

        var holders = Regex.Matches(compose, @"^  ([a-z0-9-]+):\s*$", RegexOptions.Multiline)
            .Select(m => (Name: m.Groups[1].Value, Start: m.Index))
            .ToList();

        var withGrant = holders
            .Select((service, i) => (service.Name, Body: compose[service.Start..(i + 1 < holders.Count ? holders[i + 1].Start : compose.Length)]))
            .Where(service => service.Body.Contains("${FLEET_COMMS_JOURNAL_READ_ALL_SUBJECTS", StringComparison.Ordinal))
            .Select(service => service.Name)
            .ToArray();

        Assert.Equal(["fleet-comms"], withGrant);
        Assert.Contains(
            "Comms__Journal__ReadAllSubjects=${FLEET_COMMS_JOURNAL_READ_ALL_SUBJECTS:-}", compose, StringComparison.Ordinal);
    }

    /// <summary>Every name an <c>unset</c> statement (with its <c>\</c> continuations) names.</summary>
    private static IReadOnlySet<string> UnsetNames(string script)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var lines = script.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("unset ", StringComparison.Ordinal)) continue;

            for (var j = i; j < lines.Length; j++)
            {
                var line = lines[j].TrimEnd();
                foreach (var word in line.TrimEnd('\\').Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    names.Add(word);

                if (!line.EndsWith('\\')) break;
            }
        }

        names.Remove("unset");
        return names;
    }

    /// <summary>The journal media keys (#388). Same two rules as the journal keys.</summary>
    public static TheoryData<string> MediaKeys() =>
    [
        "FLEET_COMMS_MEDIA_ENDPOINT",
        "FLEET_COMMS_MEDIA_BUCKET",
        "FLEET_COMMS_MEDIA_BACKUP_DIR",
        "FLEET_COMMS_MEDIA_ACCESS_KEY",
        "FLEET_COMMS_MEDIA_SECRET_KEY",
        "FLEET_COMMS_MINIO_ROOT_USER",
        "FLEET_COMMS_MINIO_ROOT_PASSWORD",
    ];

    [Theory]
    [MemberData(nameof(MediaKeys))]
    public void Every_media_key_is_documented_in_the_env_example(string key)
    {
        Assert.Contains(key, Read(".env.example"), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(MediaKeys))]
    public void Every_media_key_is_wired_in_the_example_compose(string key)
    {
        Assert.Contains($"${{{key}", Read("docker-compose.example.yml"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The bucket publishes no host port, and its network is internal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two independent guards, because they fail differently. A <c>ports:</c> entry on
    /// <c>comms-minio</c> puts archived conversation media on a host interface. Dropping
    /// <c>internal: true</c> from <c>comms-media</c> does not publish anything on its own, but it
    /// removes the property that makes the absence of <c>ports:</c> structural: on a non-internal
    /// network, a later service added to it can reach the outside and be reached.
    /// </para>
    /// <para>
    /// This is the same rule the journal listener and the conversation database are held to. The
    /// bucket is the third member of that family, and the only one whose contents cannot be
    /// regenerated: a lost journal MESSAGE is a row, a lost object is the photo.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_media_bucket_publishes_no_host_port_and_sits_on_an_internal_network()
    {
        var compose = Read("docker-compose.example.yml");

        var service = CommsServiceBlock(compose, "comms-minio", "comms-minio-init");
        Assert.DoesNotContain("ports:", service, StringComparison.Ordinal);

        var network = TopLevelBlock(compose, "comms-media");
        Assert.Contains("internal: true", network, StringComparison.Ordinal);
    }

    /// <summary>
    /// The media credentials reach only the two services that need them.
    /// </summary>
    /// <remarks>
    /// MUST NOT: "Only fleet-comms and fleet-comms-ops hold media credentials." The scoped pair is
    /// the least-privilege half of that rule; the root pair is the more dangerous one and reaches
    /// the server and its one-shot init container and nothing else.
    /// </remarks>
    [Fact]
    public void The_media_credentials_are_given_only_to_their_consumers()
    {
        var compose = Read("docker-compose.example.yml");

        var holders = Regex.Matches(compose, @"^  ([a-z0-9-]+):\s*$", RegexOptions.Multiline)
            .Select(m => (Name: m.Groups[1].Value, Start: m.Index))
            .ToList();

        var bodies = holders
            .Select((service, i) => (service.Name, Body: compose[service.Start..(i + 1 < holders.Count ? holders[i + 1].Start : compose.Length)]))
            .ToArray();

        // The credential COMMS reads, not the name the init container is handed. Its own
        // environment entry has to pass the substitution through to reach `mc`, which is why
        // matching on the environment VARIABLE name alone would list a provisioner as a holder.
        var scoped = bodies
            .Where(b => b.Body.Contains("Comms__Media__AccessKey=${FLEET_COMMS_MEDIA_ACCESS_KEY", StringComparison.Ordinal))
            .Select(b => b.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["fleet-comms", "fleet-comms-ops"], scoped);

        var secret = bodies
            .Where(b => b.Body.Contains("Comms__Media__SecretKey=${FLEET_COMMS_MEDIA_SECRET_KEY", StringComparison.Ordinal))
            .Select(b => b.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["fleet-comms", "fleet-comms-ops"], secret);

        // Root reaches the server and the one-shot that provisions it. Nothing long-running that
        // serves a request, and nothing that reads the bucket.
        var root = bodies
            .Where(b => b.Body.Contains("MINIO_ROOT_USER: ${FLEET_COMMS_MINIO_ROOT_USER", StringComparison.Ordinal))
            .Select(b => b.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["comms-minio", "comms-minio-init"], root);
    }

    /// <summary>The init script carries no literal credential.</summary>
    /// <remarks>
    /// Same rule as the MySQL account script, and for the same reason: a value in a tracked file is
    /// a value every checkout — and every deployment that copied it without noticing — holds.
    /// </remarks>
    [Fact]
    public void The_media_init_script_reads_its_credentials_from_the_environment()
    {
        var script = Read(Path.Combine("deploy", "comms-minio-init", "init.sh"));

        foreach (var key in new[]
                 {
                     "MINIO_ROOT_USER", "MINIO_ROOT_PASSWORD",
                     "FLEET_COMMS_MEDIA_ACCESS_KEY", "FLEET_COMMS_MEDIA_SECRET_KEY",
                 })
        {
            Assert.Contains("${" + key + ":?", script, StringComparison.Ordinal);
        }

        // And the policy is generated rather than literal: a bucket name baked into the tracked
        // JSON is a policy scoped to a bucket that does not exist.
        var policy = Read(Path.Combine("deploy", "comms-minio-init", "comms-runtime-policy.json"));
        Assert.Contains("${comms-journal}", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("\"comms-journal\"", policy, StringComparison.Ordinal);
    }

    /// <summary>
    /// The init script runs inside the pinned MinIO client image, so it may not call another
    /// binary, and its bash-only substitution needs bash to run in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MUST NOT: reintroduce <c>sed</c>, <c>awk</c>, <c>grep</c> or <c>envsubst</c> here, or switch
    /// the shebang or the compose entrypoint back to <c>sh</c>.
    /// </para>
    /// <para>
    /// The pinned <c>minio/mc</c> release image is built from upstream <c>Dockerfile.release</c> on
    /// <c>ubi9/ubi-micro</c>: it has a shell and coreutils (<c>cat</c> included), but not <c>sed</c>,
    /// <c>awk</c>, <c>grep</c> or <c>envsubst</c>. The <c>FROM scratch</c> / "no shell at all" claim
    /// that used to be written here came from reading the dev <c>Dockerfile</c> instead. The old
    /// script substituted the bucket name with <c>sed</c>, which exited 127 three steps AFTER the
    /// bucket was created and the runtime user added — so the deployment came up with a scoped user
    /// holding no policy and every upload, list and download answering <c>Access Denied</c>, while
    /// the bucket and the user both looked correctly created. Nothing outside a real host could see
    /// it. The allowlist below stays stricter than the image requires so the script never depends on
    /// which coreutils a given base image happens to ship.
    /// </para>
    /// <para>
    /// This is the cheap half of the guard; <c>scripts/check-minio-init-deps.sh</c> is the
    /// exhaustive half and runs in CI. This one exists so the rule fails in the same place the
    /// script is edited, and so <c>dotnet test</c> alone is enough to catch it.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_media_init_script_needs_nothing_the_minio_client_image_lacks()
    {
        var script = Read(Path.Combine("deploy", "comms-minio-init", "init.sh"));

        // Strip comments and the shebang: the rule is about what the shell executes, not what the
        // file explains.
        var executable = string.Join(
            '\n',
            script.Split('\n')
                .Select((line, i) => (line, i))
                .Where(x => x.i > 0 && !x.line.TrimStart().StartsWith("#", StringComparison.Ordinal))
                .Select(x => x.line));

        // Anchored to a command position rather than searched as a substring: `mc admin policy
        // update` contains "date", and a test that fires on that is a test people delete.
        foreach (var binary in new[]
                 {
                     "sed", "awk", "envsubst", "cat", "grep", "tr", "cut", "head", "tail", "jq",
                     "curl", "wget", "date", "mktemp", "dirname", "basename", "python", "openssl",
                 })
        {
            Assert.False(
                Regex.IsMatch(
                    executable,
                    @"(^|[;&|(`]|$\()[ \t]*" + Regex.Escape(binary) + @"[ \t<]",
                    RegexOptions.Multiline),
                $"init.sh calls `{binary}`, which the pinned minio/mc release image does not ship "
                + "(ubi9/ubi-micro: shell and coreutils, no sed/awk/grep/envsubst). "
                + "Read a file with $(<file), substitute with ${var//pattern/replacement}, and "
                + "build text with the printf builtin.");
        }

        // The substitution is a bashism, so BOTH places that choose the interpreter have to say
        // bash. A POSIX `sh` answers `${var//pattern/replacement}` with "Bad substitution" (measured
        // on dash 0.5.12), which is how a cosmetic revert to `sh` would reintroduce the same broken
        // policy — minio/mc's own /bin/sh is bash, so this is stated rather than assumed.
        Assert.StartsWith("#!/bin/bash", script);
        Assert.Contains(
            "entrypoint: [\"/bin/bash\", \"/init/init.sh\"]",
            Read("docker-compose.example.yml"),
            StringComparison.Ordinal);

        // The `mc` behaviour this script depends on (credentials on stdin, the `mc admin policy`
        // verbs) is release-specific, so the image is pinned and the pin is checked here: an
        // untagged `minio/mc` is a different client on every pull, and nothing else in the repo or
        // in CI would notice the change.
        Assert.Contains(
            "image: minio/mc:RELEASE.2025-08-13T08-35-41Z",
            Read("docker-compose.example.yml"),
            StringComparison.Ordinal);

        // And the script must refuse a policy whose placeholder survived, rather than hand `mc` a
        // document that grants nothing.
        Assert.Contains("case \"$policy_json\" in", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// The signing key reaches Comms, its operator one-shot, and the orchestrator token minter.
    /// </summary>
    [Fact]
    public void The_journal_key_is_given_only_to_its_three_consumers()
    {
        var compose = Read("docker-compose.example.yml");

        var holders = Regex.Matches(compose, @"^  ([a-z0-9-]+):\s*$", RegexOptions.Multiline)
            .Select(m => (Name: m.Groups[1].Value, Start: m.Index))
            .ToList();

        var withKey = holders
            .Select((service, i) => (service.Name, Body: compose[service.Start..(i + 1 < holders.Count ? holders[i + 1].Start : compose.Length)]))
            .Where(service => service.Body.Contains("${FLEET_COMMS_JOURNAL_KEY", StringComparison.Ordinal))
            .Select(service => service.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["fleet-comms", "fleet-comms-ops", "fleet-orchestrator"], withKey);
    }

    /// <summary>
    /// The journal listener is never published as a host port (MUST NOT 8).
    /// </summary>
    [Fact]
    public void The_journal_listener_is_not_published_as_a_host_port()
    {
        Assert.DoesNotContain("8083", PublishedContainerPorts(Read("docker-compose.example.yml")));
    }

    /// <summary>The check above can fail: a compose file that publishes 8083, in any spelling, is caught.</summary>
    [Theory]
    [InlineData("    ports:\n      - \"127.0.0.1:3500:8080\"\n      - \"8083:8083\"\n")]
    [InlineData("    ports:\n      - 8083:8083\n")]
    [InlineData("    ports:\n      - \"0.0.0.0:18083:8083\"\n")]
    [InlineData("    ports: [\"8083:8083\"]\n")]
    [InlineData("    ports:\n      - target: 8083\n        published: 18083\n")]
    public void A_compose_file_publishing_8083_is_detected(string portsBlock)
    {
        var compose = "services:\n  fleet-comms:\n    image: fleet:comms\n" + portsBlock;

        Assert.Contains("8083", PublishedContainerPorts(compose));
    }

    /// <summary>
    /// Every port number that appears in any <c>ports:</c> entry, host or container side: short
    /// syntax quoted or not (including <c>${VAR:-default}</c> substitutions), flow lists, and the long
    /// syntax. Deliberately over-inclusive — a false alarm costs a look, a miss publishes a listener.
    /// </summary>
    private static IReadOnlyList<string> PublishedContainerPorts(string compose)
    {
        var ports = new List<string>();
        var lines = compose.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("ports:", StringComparison.Ordinal)) continue;

            ports.AddRange(Numbers(trimmed["ports:".Length..]));

            var indent = line.Length - line.TrimStart().Length;
            for (var j = i + 1; j < lines.Length; j++)
            {
                var entry = lines[j];
                if (entry.Trim().Length == 0 || entry.TrimStart().StartsWith('#')) continue;
                if (entry.Length - entry.TrimStart().Length <= indent) break;

                ports.AddRange(Numbers(entry));
            }
        }

        return ports;

        static IEnumerable<string> Numbers(string text) =>
            Regex.Matches(text, "(?<![0-9])[0-9]{1,5}(?![0-9])").Select(m => m.Value);
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
    /// The account-provisioning script contains no literal password.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It reads them from the container's environment. A password in a tracked file is a password
    /// every checkout holds — and a <i>placeholder</i> in one is worse, because an operator who does
    /// not notice it ships a database whose accounts have the value this repository published.
    /// </para>
    /// <para>
    /// Asserted on the shape rather than on a denylist of known-bad strings: every
    /// <c>IDENTIFIED BY</c> must interpolate a variable, so a literal of any value fails whether or
    /// not anyone thought to add it to a list.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_account_script_carries_no_literal_password()
    {
        var script = Read(Path.Combine("deploy", "comms-mysql-init", "01-accounts.sh"));

        var identifiedBy = Regex.Matches(script, @"IDENTIFIED BY\s+'([^']*)'")
            .Select(m => m.Groups[1].Value)
            .ToArray();

        // Both accounts, and no more — a third would be an account nothing else here knows about.
        Assert.Equal(2, identifiedBy.Length);

        foreach (var value in identifiedBy)
        {
            Assert.StartsWith("${", value, StringComparison.Ordinal);
            Assert.EndsWith("}", value, StringComparison.Ordinal);
        }

        // And the old .sql form is gone rather than sitting beside the new one, where a stale copy
        // in the same directory would still be executed by the entrypoint.
        Assert.False(
            File.Exists(Path.Combine(
                RepositoryRoot().FullName, "deploy", "comms-mysql-init", "01-accounts.sql")),
            "the superseded .sql init file is still present and would still be run");
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
        var sql = Read(Path.Combine("deploy", "comms-mysql-init", "01-accounts.sh"));

        var runtimeGrant = sql.Split('\n')
            .Single(line => line.Contains("TO 'comms_runtime'@", StringComparison.Ordinal));

        Assert.Contains("SELECT, INSERT, UPDATE, DELETE", runtimeGrant, StringComparison.Ordinal);

        foreach (var ddl in new[] { "ALL PRIVILEGES", "CREATE", "ALTER", "DROP", "INDEX", "REFERENCES" })
            Assert.DoesNotContain(ddl, runtimeGrant, StringComparison.Ordinal);
    }

    /// <summary>
    /// One service's <c>services:</c> block, from its header up to the next service header.
    /// </summary>
    private static string CommsServiceBlock(string compose, string service, string nextService)
    {
        var start = compose.IndexOf($"  {service}:", StringComparison.Ordinal);
        Assert.True(start >= 0, $"service {service} is not declared");

        var after = compose[(start + 1)..];
        var end = after.IndexOf($"\n  {nextService}:", StringComparison.Ordinal);
        return end < 0 ? compose[start..] : compose[start..(start + 1 + end)];
    }

    /// <summary>
    /// One top-level block (<c>services:</c>, <c>networks:</c>, <c>volumes:</c>) or one entry inside
    /// one, sliced by indentation rather than by a named neighbour — the next entry is exactly the
    /// thing an edit adds or removes.
    /// </summary>
    private static string TopLevelBlock(string compose, string key)
    {
        var lines = compose.Split('\n');
        var start = Array.FindIndex(lines, l => l == $"  {key}:");
        Assert.True(start >= 0, $"{key} is not declared");

        var end = Array.FindIndex(lines, start + 1, l => l.Length > 0 && l[0] != ' ' && l[0] != '#');
        return string.Join("\n", lines[start..(end < 0 ? lines.Length : end)]);
    }


    [Fact]
    public void Seaweedfs_keys_are_documented_and_generated_only_on_opt_in()
    {
        foreach (var key in new[] { "FLEET_COMMS_MEDIA_STORE", "FLEET_COMMS_SEAWEEDFS_SIGNING_KEY" })
        {
            Assert.Contains("#" + key + "=", Read(".env.example"));
            Assert.Contains(key, Read("setup.sh"));
            Assert.Contains(key, Read("upgrade.sh"));
        }
    }

    [Fact]
    public void Seaweedfs_is_isolated_and_its_pin_matches_ci()
    {
        const string image = "chrislusf/seaweedfs:4.48@sha256:4e61d15fd35994cb1e43e1e553dff106794841fd9a99ade2fc8c8bfce4d7872d";
        var compose = Read("docker-compose.example.yml");
        var server = CommsServiceBlock(compose, "comms-seaweedfs", "comms-seaweedfs-init");
        var init = CommsServiceBlock(compose, "comms-seaweedfs-init", "fleet-dashboard");
        Assert.Contains(image, server);
        Assert.Contains(image, init);
        Assert.Contains(image, Read(".github/workflows/ci.yml"));
        Assert.DoesNotContain("ports:", server);
        Assert.DoesNotContain("fleet-net", server);
        Assert.Contains("networks:\n      - comms-media", server);
        Assert.Contains("network_mode: \"service:comms-seaweedfs\"", init);
        Assert.DoesNotContain("ACCESS_KEY", init);
        Assert.DoesNotContain("SECRET_KEY", init);
        Assert.DoesNotContain("SIGNING_KEY", init);
        var holders = Regex.Matches(compose, @"^  ([a-z0-9-]+):\s*$", RegexOptions.Multiline)
            .Select(m => (Name: m.Groups[1].Value, Start: m.Index)).ToList();
        var signingHolders = holders.Select((h, i) => (h.Name, Body: compose[h.Start..(i + 1 < holders.Count ? holders[i + 1].Start : compose.Length)]))
            .Where(h => h.Body.Contains("${FLEET_COMMS_SEAWEEDFS_SIGNING_KEY"))
            .Select(h => h.Name).ToArray();
        Assert.Equal(["comms-seaweedfs"], signingHolders);
        var runtimeHolders = holders.Select((h, i) => (h.Name, Body: compose[h.Start..(i + 1 < holders.Count ? holders[i + 1].Start : compose.Length)]))
            .Where(h => h.Body.Contains("${FLEET_COMMS_MEDIA_ACCESS_KEY"))
            .Select(h => h.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["comms-minio-init", "comms-seaweedfs", "fleet-comms", "fleet-comms-ops"], runtimeHolders);
    }

    [Fact]
    public void Legacy_minio_service_bodies_are_byte_identical()
    {
        var compose = Read("docker-compose.example.yml");
        foreach (var name in new[] { "comms-minio", "comms-minio-init" })
        {
            var service = Regex.Match(compose, @"^  " + name + @":\n(?:^    [^\n]*\n|^\n)*", RegexOptions.Multiline).Value;
            // Blank separator lines between services are not part of either body.
            Assert.Equal(Read("tests/fixtures/legacy-" + name + ".txt"), service.TrimEnd('\n') + "\n");
        }
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
