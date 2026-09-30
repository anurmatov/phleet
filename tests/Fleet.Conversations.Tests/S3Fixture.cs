using Amazon.S3;
using Amazon.S3.Model;
using Fleet.Conversations.Journal;
using Fleet.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Conversations.Tests;

/// <summary>
/// A real S3-compatible bucket behind the real <see cref="S3ObjectStore"/> (#388).
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>This fixture FAILS when the bucket is absent. It never skips</b> — <see cref="MySqlFixture"/>
/// and <see cref="RabbitMqFixture"/> follow the same rule, and the reason is not a preference: a
/// suite that skipped for want of a bucket is indistinguishable from a passing one in a run summary,
/// and the media acceptance criteria are exactly the ones that would quietly stop being evidence.
/// </para>
/// <para>
/// <b>The fixture is SeaweedFS, not MinIO, and that is a deliberate substitution.</b> MinIO's images
/// stopped being built from public source, so pinning one would pin a binary nobody can audit or
/// rebuild. SeaweedFS's <c>weed server -s3</c> implements the S3 calls this store actually makes —
/// path-style PUT, GET, HEAD, DELETE and ListObjectsV2 — from source we can read.
/// </para>
/// <para>
/// ⚠️ What that substitution does NOT cover, and what no test here should be described as proving:
/// the start-up credential classes (<c>credentials_rejected</c> against a wrong secret,
/// <c>bucket_public</c> against an anonymous list) and the behaviour of a MinIO that refuses the
/// SDK's default checksum trailer. Those are properties of the specific server, and a fixture that
/// is not that server cannot establish them. The three <see cref="S3ObjectStore"/> choices that ARE
/// exercised here — path-style addressing, a known-length stream, and <c>WHEN_REQUIRED</c> checksums
/// — are the ones that fail identically on any path-style server.
/// </para>
/// </remarks>
public sealed class S3Fixture : IAsyncLifetime
{
    public const string EndpointVariable = "FLEET_COMMS_S3_ENDPOINT";
    public const string AccessVariable = "FLEET_COMMS_S3_ACCESS_KEY";
    public const string SecretVariable = "FLEET_COMMS_S3_SECRET_KEY";

    /// <summary>
    /// ⚠️ <b>Set this to run the suite against a bucket that actually authenticates.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default fixture sends anonymous requests, which is enough to prove the data plane and
    /// nothing else. A second bucket configured with a real identity — SeaweedFS
    /// <c>-s3.iam.config=&lt;file&gt;</c> with an <c>Admin</c> identity — lets the suite prove the
    /// things the anonymous fixture structurally cannot: that <c>HeadBucket</c> succeeds with the
    /// shipped runtime policy, that a WRONG secret is classified <c>credentials_rejected</c> rather
    /// than retried, and the operator commands (<c>media backup</c> / <c>media restore</c> /
    /// <c>journal verify-media</c>), which build their own <see cref="S3ObjectStore"/> from the
    /// environment and therefore always sign.
    /// </para>
    /// <para>
    /// ⚠️ <b>My earlier comment on this file claimed SeaweedFS answers a signed request with 400
    /// rather than 403, and that a signed fixture was therefore a false green.</b> That is true only
    /// of a server with NO identity configured. Measured 2026-09-29 against the pinned image with
    /// <c>-s3.iam.config</c> holding an Admin identity: a signed PUT succeeds, a wrong secret is
    /// answered <c>403 AccessDenied</c>, and an unauthenticated request is refused. The credential
    /// classes are provable on this fixture, so this variable exists and the claim is retired here
    /// rather than left as folk law.
    /// </para>
    /// </remarks>
    public const string SignedEndpointVariable = "FLEET_COMMS_S3_SIGNED_ENDPOINT";
    public const string SignedBucketVariable = "FLEET_COMMS_S3_SIGNED_BUCKET";

    /// <summary>
    /// The identity the signed fixture authenticates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ SeaweedFS looks an access key up <i>exactly</i> (its own <c>accessKeyIdent</c> map), so the
    /// value here must be byte-identical to the one in the identity file. Measured 2026-09-29
    /// against the pinned image: a key the file does not name is answered
    /// <c>403 InvalidAccessKeyId</c>, which is the correct behaviour and not a bug to work around.
    /// </para>
    /// <para>
    /// The pair is <c>placeholder</c>/<c>placeholder</c> — the same values the anonymous fixture
    /// already passes to <see cref="JournalMediaOptions"/> — so one identity serves both the
    /// credential-class bucket and the bucket the operator commands run against. They are fixture
    /// values for throwaway containers on ephemeral runners; nothing in this repo or any deployment
    /// uses them, and <see cref="JournalMediaOptions.Validate"/> still requires them to be present.
    /// </para>
    /// </remarks>
    public const string SignedAccessKey = "placeholder";
    public const string SignedSecretKey = "placeholder";

    public string Endpoint { get; private set; } = string.Empty;
    public string AccessKey { get; private set; } = string.Empty;
    public string SecretKey { get; private set; } = string.Empty;
    public string Bucket { get; private set; } = string.Empty;

    /// <summary>Endpoint of the identity-configured bucket, or null when none was supplied.</summary>
    public string? SignedEndpoint { get; private set; }

    /// <summary>The bucket behind <see cref="SignedEndpoint"/>; auto-created by an admin PUT.</summary>
    public string SignedBucket { get; private set; } = "journal-ci-signed";

    /// <summary>
    /// True when a bucket that authenticates is available. Tests that need real credentials
    /// <b>fail</b> when this is false — they never skip silently.
    /// </summary>
    public bool HasSignedBucket => SignedEndpoint is not null;

    /// <summary>Reads the signed fixture's environment. Empty when the endpoint is not set.</summary>
    public void ReadSignedEnvironment()
    {
        SignedEndpoint = Environment.GetEnvironmentVariable(SignedEndpointVariable);
        var bucket = Environment.GetEnvironmentVariable(SignedBucketVariable);
        if (!string.IsNullOrWhiteSpace(bucket)) SignedBucket = bucket;
    }

    /// <summary>
    /// A store over the SIGNED bucket — real credentials through the store's own constructor, so
    /// the client is the same one Comms builds. Unlike <see cref="Store"/>, this signs.
    /// </summary>
    /// <exception cref="InvalidOperationException">No signed bucket was supplied to the run.</exception>
    public S3ObjectStore SignedStore()
    {
        if (SignedEndpoint is null)
            throw new InvalidOperationException(
                $"{SignedEndpointVariable} is not set, so this run has no bucket that authenticates. "
                + "The assertions that need one FAIL rather than skip — see the class remarks.");

        return new S3ObjectStore(new JournalMediaOptions
        {
            Endpoint = SignedEndpoint,
            Bucket = SignedBucket,
            AccessKey = SignedAccessKey,
            SecretKey = SignedSecretKey,
            Region = "us-east-1",
        }, NullLogger.Instance);
    }

    /// <summary>
    /// ⚠️ <b>Both fixture containers name an identity config, and this suite signs.</b> There is no
    /// anonymous container any more, because an anonymous client cannot create the bucket it wants:
    /// on SeaweedFS the only call that creates a bucket is an authenticated PUT for an <c>Admin</c>
    /// identity (<c>autoCreateBucket</c> → <c>isUserAdmin</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The filer's HTTP API is NOT a way to create a bucket, which is what an earlier version of
    /// this file assumed. <c>weed s3</c> takes its bucket root from the filer's
    /// <c>GetFilerConfiguration().DirBuckets</c> (<c>/buckets</c> unless
    /// <c>filer.options.buckets_folder</c> says otherwise), and <c>Filer.IsBucket</c> is true only
    /// for a directory whose parent is exactly that path. A multipart POST to the filer root makes a
    /// top-level directory, so it answers 201 and the S3 <c>HeadBucket</c> for the same name stays
    /// 404 forever. Run 36652704531 measured exactly that, 15 times.
    /// </para>
    /// <para>
    /// The primary container names an <c>anonymous</c> identity carrying <c>Admin</c> as well as the
    /// keyed one, so a request this suite sends unsigned still resolves — SeaweedFS only resolves
    /// anonymous requests against an identity the config actually NAMES, which is why simply naming
    /// a key is not enough to keep the old behaviour available.
    /// </para>
    /// </remarks>
    public async Task InitializeAsync()
    {
        var endpoint = Environment.GetEnvironmentVariable(EndpointVariable);
        var access = Environment.GetEnvironmentVariable(AccessVariable);
        var secret = Environment.GetEnvironmentVariable(SecretVariable);

        if (string.IsNullOrWhiteSpace(endpoint)
            || string.IsNullOrWhiteSpace(access)
            || string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException(
                $"{EndpointVariable}, {AccessVariable} and {SecretVariable} are not all set, so the "
                + "media suite has no bucket to run against.\n\n"
                + "  This FAILS rather than skipping on purpose: these tests prove a byte written "
                + "through the real SDK is readable back with the digest the store computed, and a "
                + "skipped run claims that happened when it did not.\n\n"
                + "  CI starts two SeaweedFS containers. Locally:\n"
                + "    weed server -s3 -dir=/tmp/sw \\\n"
                + "      -s3.iam.config=tests/fixtures/seaweedfs-primary-identity.json\n"
                + $"    export {EndpointVariable}='http://127.0.0.1:8333'\n"
                + "    # the fixture creates the bucket itself, with an admin PUT\n"
                + $"    export {AccessVariable}='placeholder' {SecretVariable}='placeholder'");

        Endpoint = endpoint;
        AccessKey = access;
        SecretKey = secret;
        Bucket = Environment.GetEnvironmentVariable("FLEET_COMMS_S3_BUCKET") ?? "journal-ci";

        // Fail at fixture time, not inside the first assertion: an unreachable bucket is a missing
        // prerequisite, not a failing test.
        var reachable = await HeadAsync(ProbeUrl);
        if (reachable is not (200 or 403 or 404))
            throw new InvalidOperationException(
                $"the object store at {Endpoint} answered {reachable} for bucket {Bucket}. "
                + "Start one and point the fixture at it (see the class remarks).");

        if (reachable == 404)
        {
            // ⚠️ The filer trick this used to rely on NEVER created a bucket. Measured against the
            // pinned image on CI run 36652704531: `POST /<bucket>/` answered 201 and the S3
            // `HeadBucket` for the same name stayed 404, on both containers, on a clean server.
            //
            // The reason is in the SeaweedFS source, and it is structural rather than a timing
            // problem that a longer wait could fix:
            //
            //   - `weed s3` takes its bucket root from the filer: `GetFilerConfiguration()` returns
            //     `DirBuckets`, and `startS3Server` stores it as `S3ApiServerOption.BucketsPath`
            //     (`/buckets` unless `filer.options.buckets_folder` says otherwise).
            //   - `Filer.IsBucket` is true only for a directory whose PARENT is exactly that path.
            //   - A multipart POST to the filer root creates a top-level directory, so its parent
            //     is `/` — not a bucket, invisible to the S3 layer, forever.
            //
            // So the primary bucket is created the way the signed one always was: an authenticated
            // PUT, which auto-creates for an `Admin` identity (`autoCreateBucket` → `isUserAdmin`).
            // That needs an identity on the primary container too, and an `anonymous` identity with
            // `Admin` keeps the unauthenticated data-plane calls working — `LookupAnonymous` only
            // resolves an identity the config NAMES.
            await EnsureBucketAsync(Store(), Bucket, "primary");
            if (await HeadAsync(ProbeUrl) is not (200 or 403))
                throw new InvalidOperationException(
                    $"could not create bucket {Bucket}. An admin PUT was accepted and the bucket "
                    + "still does not answer HeadBucket — see EnsureBucketAsync for how a bucket is "
                    + "actually created on this server.");
        }

        using var store = Store();
        if (!await store.ProbeAsync())
            throw new InvalidOperationException(
                $"the store could not read bucket {Bucket} at {Endpoint}. The bucket exists — the "
                + "fixture checked — so this is a store or addressing failure.");

        await EmptyJournalPrefixAsync(store);

        // The signed bucket is optional at FIXTURE level and mandatory at TEST level: reading it
        // here must not fail a run that only wants the data plane, but SignedStore() throws when a
        // test asks for it and the run did not supply one. A silently-skipped credential test is
        // the exact failure shape this file exists to prevent.
        ReadSignedEnvironment();
        if (SignedEndpoint is not null) await EnsureSignedBucketAsync();
    }

    /// <summary>Creates the signed bucket, or fails the run explaining why it could not.</summary>
    private Task EnsureSignedBucketAsync() => EnsureBucketAsync(SignedStore(), SignedBucket, "signed");

    /// <summary>
    /// Creates <paramref name="bucket"/> by an authenticated PUT, then proves it answers
    /// <c>HeadBucket</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>An authenticated PUT is the only way this server creates a bucket, for the anonymous
    /// fixture as much as for the signed one.</b> The filer route is not a shortcut: S3 buckets are
    /// the directories immediately under the filer's <c>DirBuckets</c> directory, and a POST to the
    /// filer root creates a directory somewhere the S3 layer never looks. Measured against the
    /// pinned image, <c>POST /&lt;bucket&gt;/</c> returned 201 while the S3 <c>HeadBucket</c> for the
    /// same name still answered 404.
    /// </para>
    /// <para>
    /// ⚠️ The PUT must come FIRST, and that is not incidental: the bucket does not exist until
    /// something creates it, and <c>HeadBucket</c> on a missing bucket answers 404 — which the store
    /// classifies as a startup failure. Probing before creating inverts the dependency and fails the
    /// fixture for a reason that has nothing to do with the code.
    /// </para>
    /// <para>
    /// The key is a real ULID under the journal prefix because SeaweedFS's auto-create only fires
    /// for an <c>Admin</c> identity on an ordinary PUT (<c>autoCreateBucket</c> →
    /// <c>isUserAdmin</c>); it is deleted immediately, so the orphan sweep never sees a byte the
    /// suite did not intend to leave.
    /// </para>
    /// </remarks>
    private static async Task EnsureBucketAsync(S3ObjectStore store, string bucket, string label)
    {
        using var _ = store;

        var keep = JournalObjectKeys.For(Fleet.Protocol.Ulid.NewUlid());
        var marker = new byte[16];
        Random.Shared.NextBytes(marker);

        await using (var body = new MemoryStream(marker))
        {
            var written = await store.PutAsync(keep, body, marker.LongLength, "application/octet-stream");
            if (!written.Succeeded)
                throw new InvalidOperationException(
                    $"could not create bucket {bucket} on the {label} fixture: the store refused an "
                    + "authenticated PUT. Check that -s3.iam.config names an identity with Admin "
                    + "and that its access key matches the identity named in -s3.iam.config "
                    + "exactly. See SignedAccessKey.");
        }

        await store.DeleteAsync(keep);

        if (!await store.ProbeAsync())
            throw new InvalidOperationException(
                $"bucket {bucket} exists on the {label} fixture but HeadBucket refused it. The "
                + "identity's policy does not grant s3:ListBucket on the bucket — which is exactly "
                + "the failure the shipped scoped policy must not have, so this is worth reading "
                + "as a real finding.");
    }

    /// <summary>
    /// Remove every object under the journal prefix before any test runs.
    /// </summary>
    /// <remarks>
    /// ⚠️ The bucket is a shared, named resource, and several tests assert on a COUNT of the journal
    /// prefix. Anything left there by a previous run — including one that crashed mid-test — makes
    /// those counts wrong for reasons that have nothing to do with the code under test, and the
    /// failure looks like a bug in the sweeper. Deleting the prefix first is what makes the count
    /// assertions mean what they say.
    /// </remarks>
    /// <remarks>
    /// ⚠️ <b>Journal prefix only, and it must stay that way.</b> This deletes without asking. The
    /// bucket is configurable through <c>FLEET_COMMS_S3_BUCKET</c>, so a developer pointing the
    /// fixture at a bucket that also holds something real would otherwise have a test suite that
    /// erases it. <see cref="Fleet.Conversations.Journal.JournalObjectKeys.Prefix"/> is the widest
    /// thing this is allowed to touch; a broader prefix here is a defect, not a convenience.
    /// </remarks>
    private async Task EmptyJournalPrefixAsync(S3ObjectStore store)
    {
        // The store's own prefix constant, never a literal: if the prefix ever changes, a hardcoded
        // "j1/" here would empty the wrong namespace and the count assertions would go wrong again.
        var stale = await store.ListAsync(Fleet.Conversations.Journal.JournalObjectKeys.Prefix);
        foreach (var obj in stale) await store.DeleteAsync(obj.Key);
    }

    private string ProbeUrl => $"{Endpoint.TrimEnd('/')}/{Bucket}?list-type=2";

    private static async Task<int?> HeadAsync(string url)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            return (int)(await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url))).StatusCode;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// A store over the fixture's bucket, built through the same constructor Comms uses.
    /// </summary>
    /// <remarks>
    /// The pair comes from the environment because a real deployment can point the fixture at a
    /// bucket it manages, and it is what the client signs with. On CI it is the fixture identity
    /// named in <c>tests/fixtures/seaweedfs-primary-identity.json</c>; the fields are required by
    /// <see cref="JournalMediaOptions.Validate"/> — the same guard that protects the real
    /// deployment, which is also under test here.
    /// </remarks>
    public S3ObjectStore Store(string? bucket = null) => new(
        new JournalMediaOptions
        {
            Endpoint = Endpoint,
            Bucket = bucket ?? Bucket,
            AccessKey = AccessKey,
            SecretKey = SecretKey,
            Region = "us-east-1",
        },
        NullLogger.Instance,
        Client());

    /// <summary>
    /// Raw client, for the assertions that must not go through the store's own abstraction.
    /// ⚠️ <b>Signed with the fixture identity.</b> This used to send anonymous requests, which
    /// worked only against a server with no identity configured — and such a server cannot create a
    /// bucket at all, because SeaweedFS auto-creates one only for an <c>Admin</c> identity. The
    /// primary container therefore names an identity too, and this client signs with it.
    /// </summary>
    /// <remarks>
    /// A deployment that wants the old behaviour can point <c>FLEET_COMMS_S3_ACCESS_KEY</c> at an
    /// identity the server names; the fixture never invents credentials of its own.
    /// </remarks>
    public AmazonS3Client Client() => new(
        new Amazon.Runtime.BasicAWSCredentials(AccessKey, SecretKey),
        new AmazonS3Config
        {
            ServiceURL = Endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            RequestChecksumCalculation = Amazon.Runtime.RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = Amazon.Runtime.ResponseChecksumValidation.WHEN_REQUIRED,
        });
}

/// <summary>
/// The two classes that put bytes through the real bucket, serialised.
/// </summary>
/// <remarks>
/// ⚠️ <c>DisableParallelization</c> is not a performance concession; without it the suite cannot
/// make exact count assertions. Both classes write into the SAME named bucket, and several assert
/// "exactly one object under <c>j1/</c>" or "one committed row". xUnit runs different classes in a
/// collection concurrently unless this is set, so each class' bucket would contain the other's
/// objects and the counts would be wrong for reasons that have nothing to do with the code.
///
/// The fixture empties the journal prefix at start-up, which handles a dirty runner. It cannot
/// handle a CONCURRENT neighbour, and it must not try to: deleting another class' live objects
/// mid-assertion is worse than the failure it would fix.
/// </remarks>
[CollectionDefinition("s3", DisableParallelization = true)]
public sealed class S3Collection : ICollectionFixture<S3Fixture>;
