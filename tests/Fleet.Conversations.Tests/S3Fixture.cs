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
    public const string SignedFilerVariable = "FLEET_COMMS_S3_SIGNED_FILER";
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
        var filer = Environment.GetEnvironmentVariable(SignedFilerVariable);
        if (!string.IsNullOrWhiteSpace(filer)) SignedFiler = filer;
        var bucket = Environment.GetEnvironmentVariable(SignedBucketVariable);
        if (!string.IsNullOrWhiteSpace(bucket)) SignedBucket = bucket;
    }

    /// <summary>Filer of the signed bucket, used only to pre-create it.</summary>
    public string SignedFiler { get; private set; } = "http://127.0.0.1:8888";

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
    /// The SeaweedFS filer, used ONLY to create the bucket. Configurable so a deployment that puts
    /// the two behind different addresses can still run this suite.
    /// </summary>
    public string FilerEndpoint { get; private set; } = "http://127.0.0.1:8888";

    /// <summary>
    /// ⚠️ <b>Unsigned requests, by construction.</b> The credentials come from the environment but
    /// are never used: the client is built with no credentials, which is how the AWS SDK sends an
    /// anonymous request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>signingKey</c> in the fixture's IAM config turns a wrong secret into HTTP <c>400
    /// InvalidRequest</c> ("Signed request requires setting up SeaweedFS S3 authentication") rather
    /// than the <c>403 InvalidAccessKeyId</c> the real deployment gets. A suite run against that
    /// fixture would therefore prove the store classifies a rejected credential as
    /// <c>credentials_rejected</c> when it does not — the exact false green this file exists to
    /// avoid.
    /// </para>
    /// <para>
    /// So the bucket is created over the filer (multipart POST, which no S3 signature is involved
    /// in) and the fixture never signs. What that buys: the bucket's anonymous LIST answers 200,
    /// which is the state Comms' own start-up probe refuses to boot under. That is why the probe
    /// assertions below expect success against a PUBLIC bucket — and why this fixture proves the
    /// data plane, never the credential classes. See the class remarks.
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
                + "  CI supplies a SeaweedFS service container. Locally:\n"
                + "    weed server -s3 -dir=/tmp/sw\n"
                + $"    export {EndpointVariable}='http://127.0.0.1:8333'\n"
                + "    # the fixture creates the bucket itself, over the filer\n"
                + $"    export {AccessVariable}='placeholder' {SecretVariable}='placeholder'");

        Endpoint = endpoint;
        AccessKey = access;
        SecretKey = secret;
        Bucket = Environment.GetEnvironmentVariable("FLEET_COMMS_S3_BUCKET") ?? "journal-ci";
        FilerEndpoint = Environment.GetEnvironmentVariable("FLEET_COMMS_S3_FILER") ?? FilerEndpoint;

        // Fail at fixture time, not inside the first assertion: an unreachable bucket is a missing
        // prerequisite, not a failing test.
        var reachable = await HeadAsync(ProbeUrl);
        if (reachable is not (200 or 403 or 404))
            throw new InvalidOperationException(
                $"the object store at {Endpoint} answered {reachable} for bucket {Bucket}. "
                + "Start one and point the fixture at it (see the class remarks).");

        if (reachable == 404)
        {
            // SeaweedFS creates a bucket when a file is written under it, over the FILER rather
            // than the S3 API — a signed CreateBucket would need a working signature, which this
            // fixture deliberately does not have.
            await EnsureBucketOverFilerAsync();
            if (await HeadAsync(ProbeUrl) is not (200 or 403))
                throw new InvalidOperationException(
                    $"could not create bucket {Bucket} over the filer at {FilerEndpoint}.");
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

    /// <summary>
    /// Creates the signed bucket, or fails the run explaining why it could not.
    /// </summary>
    /// <remarks>
    /// ⚠️ Not over the filer. The anonymous fixture creates its bucket that way because a signed
    /// CreateBucket needs a working signature; on the SIGNED fixture the signature works, and the
    /// bucket must be created by an authenticated S3 write — SeaweedFS auto-creates on upload for an
    /// <c>Admin</c> identity. A top-level filer directory is NOT visible to the S3 layer here:
    /// measured against the pinned image, <c>POST /&lt;bucket&gt;/</c> returned 201 while the S3
    /// <c>HeadBucket</c> for the same name still answered 404.
    /// </remarks>
    private async Task EnsureSignedBucketAsync()
    {
        using var store = SignedStore();

        // ⚠️ The PUT must come FIRST, and that is not incidental: the bucket does not exist until
        //    something creates it, and `HeadBucket` on a missing bucket answers 404 — which the
        //    store classifies as a startup failure. Probing before creating inverts the dependency
        //    and fails the fixture for a reason that has nothing to do with the code.
        //
        // The key is a real ULID under the journal prefix because SeaweedFS's auto-create only
        // fires for an Admin identity on an ordinary PUT; it is deleted immediately, so the orphan
        // sweep never sees a byte the suite did not intend to leave.
        var keep = JournalObjectKeys.For(Fleet.Protocol.Ulid.NewUlid());
        var marker = new byte[16];
        Random.Shared.NextBytes(marker);

        await using (var body = new MemoryStream(marker))
        {
            var written = await store.PutAsync(keep, body, marker.LongLength, "application/octet-stream");
            if (!written.Succeeded)
                throw new InvalidOperationException(
                    $"could not create bucket {SignedBucket} on the signed fixture at "
                    + $"{SignedEndpoint}: the store refused an authenticated PUT. Check that "
                    + "-s3.iam.config names an identity with Admin, and that its access key is at "
                    + "and that its access key matches the identity named in -s3.iam.config "
                    + "exactly. See SignedAccessKey.");
        }

        await store.DeleteAsync(keep);

        if (!await store.ProbeAsync())
            throw new InvalidOperationException(
                $"bucket {SignedBucket} exists on the signed fixture but HeadBucket refused it. The "
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

    private async Task EnsureBucketOverFilerAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        // A multipart POST to /<bucket>/ is how the filer creates a directory, and SeaweedFS
        // treats a top-level directory as a bucket. The file is a keep-marker; the fixture deletes
        // it so the orphan sweep sees nothing it did not put there.
        using var form = new MultipartFormDataContent();
        var keep = new ByteArrayContent([]);
        keep.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(keep, "file", ".fixture-keep");

        var created = await http.PostAsync($"{FilerEndpoint.TrimEnd('/')}/{Bucket}/", form);
        if (!created.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"the filer refused to create {Bucket}: HTTP {(int)created.StatusCode}.");

        var deleted = await http.DeleteAsync($"{FilerEndpoint.TrimEnd('/')}/{Bucket}/.fixture-keep");
        if (!deleted.IsSuccessStatusCode && deleted.StatusCode != System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException(
                $"the filer refused to remove the keep marker: HTTP {(int)deleted.StatusCode}.");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// A store over the fixture's bucket, built through the same constructor Comms uses.
    /// </summary>
    /// <remarks>
    /// The access/secret pair here is a PLACEHOLDER, not a working credential: SeaweedFS without a
    /// signing key authenticates nobody, and the store's own client is built from
    /// <see cref="Client"/> so nothing it sends is signed. The fields are filled because
    /// <see cref="JournalMediaOptions.Validate"/> requires them — the same guard that protects the
    /// real deployment, which is also under test here.
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
    /// ⚠️ <b>Anonymous.</b> The credentials above are read so a real deployment can point the
    /// fixture at a bucket it manages, but the SDK is given none — passing them would make a
    /// signed request, which SeaweedFS answers 400 rather than 403 without a signing key.
    /// </summary>
    public AmazonS3Client Client() => new(
        // `AnonymousAWSCredentials` is the SDK's declared "send this unsigned". Handing it nothing
        // is different: the default chain then searches the environment and FAILS the call when it
        // finds nothing, which is not the same as not needing credentials.
        new Amazon.Runtime.AnonymousAWSCredentials(),
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
