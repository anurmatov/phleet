namespace Fleet.Comms.Auth;

/// <summary>
/// The Slice-1 <see cref="IAuthStore"/> implementation: in-process, serialized, and durable only
/// for the lifetime of the process.
///
/// <para><b>This is a deliberate scope boundary, and it is a real limitation.</b> A restart loses
/// device registrations, so the owner must re-enroll. It is here rather than a MySQL layer for one
/// reason: the deployable that owns a database credential is settled by #277 D-4 and the schema
/// that goes with it is not in this slice, so a persistence layer written now could not be
/// exercised against a real engine in this repository's CI. Untested persistence that looks
/// finished is worse than an honest in-process store with the port drawn correctly — the port is
/// the part that has to be right, because it is what a durable implementation will have to
/// satisfy.</para>
///
/// <para>The single semaphore is what makes <see cref="InTransactionAsync{T}"/> atomic. It is not
/// a performance design and does not need to be: this boundary serves one owner with one device,
/// and correctness of the consume-and-register step is the only property that matters here.</para>
/// </summary>
public sealed class InMemoryAuthStore : IAuthStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, EnrollmentRecord> _enrollments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeviceRecord> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TokenRecord> _tokens = new(StringComparer.Ordinal);

    /// <summary>
    /// When set, every operation throws <see cref="AuthStoreUnavailableException"/>. This is the
    /// real failure path rather than a mocked interface, so the fail-closed tests exercise the same
    /// code the route layer runs in production.
    /// </summary>
    public bool FailEveryOperation { get; set; }

    /// <summary>
    /// When set, the transaction body runs and is then discarded, as a store that accepted writes
    /// and lost the commit would behave. Used to prove no half-spent enrollment state survives.
    /// </summary>
    public bool FailOnCommit { get; set; }

    public async Task<T> InTransactionAsync<T>(
        Func<IAuthStoreTransaction, CancellationToken, Task<T>> body, CancellationToken ct)
    {
        if (FailEveryOperation)
            throw new AuthStoreUnavailableException("The auth store is unavailable.");

        await _gate.WaitAsync(ct);
        try
        {
            // Work on a copy. Nothing the body writes is visible to anyone else until commit, so a
            // body that throws — or a commit that fails — leaves no partial state behind.
            var scratch = new Transaction(_enrollments, _devices, _tokens);
            var result = await body(scratch, ct);

            if (FailOnCommit)
                throw new AuthStoreUnavailableException("The auth store failed to commit.");

            scratch.CommitInto(_enrollments, _devices, _tokens);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Test and operator visibility. Never used by the request path.</summary>
    internal IReadOnlyCollection<EnrollmentRecord> Enrollments => _enrollments.Values;

    internal IReadOnlyCollection<DeviceRecord> Devices => _devices.Values;

    internal IReadOnlyCollection<TokenRecord> Tokens => _tokens.Values;

    private sealed class Transaction(
        Dictionary<string, EnrollmentRecord> enrollments,
        Dictionary<string, DeviceRecord> devices,
        Dictionary<string, TokenRecord> tokens) : IAuthStoreTransaction
    {
        private readonly Dictionary<string, EnrollmentRecord> _enrollments =
            new(enrollments, StringComparer.Ordinal);
        private readonly Dictionary<string, DeviceRecord> _devices =
            new(devices, StringComparer.Ordinal);
        private readonly Dictionary<string, TokenRecord> _tokens =
            new(tokens, StringComparer.Ordinal);

        public Task<EnrollmentRecord?> FindEnrollmentAsync(string enrollmentId, CancellationToken ct) =>
            Task.FromResult(_enrollments.GetValueOrDefault(enrollmentId));

        public Task SaveEnrollmentAsync(EnrollmentRecord record, CancellationToken ct)
        {
            _enrollments[record.EnrollmentId] = record;
            return Task.CompletedTask;
        }

        public Task<DeviceRecord?> FindDeviceAsync(string deviceId, CancellationToken ct) =>
            Task.FromResult(_devices.GetValueOrDefault(deviceId));

        public Task SaveDeviceAsync(DeviceRecord record, CancellationToken ct)
        {
            _devices[record.DeviceId] = record;
            return Task.CompletedTask;
        }

        public Task<int> CountActiveDevicesAsync(string principalId, CancellationToken ct) =>
            Task.FromResult(_devices.Values.Count(d =>
                d.IsActive && string.Equals(d.PrincipalId, principalId, StringComparison.Ordinal)));

        public Task<TokenRecord?> FindTokenAsync(string tokenId, CancellationToken ct) =>
            Task.FromResult(_tokens.GetValueOrDefault(tokenId));

        public Task SaveTokenAsync(TokenRecord record, CancellationToken ct)
        {
            _tokens[record.TokenId] = record;
            return Task.CompletedTask;
        }

        public Task RevokeTokensForDeviceAsync(string deviceId, DateTimeOffset at, CancellationToken ct)
        {
            foreach (var token in _tokens.Values
                         .Where(t => string.Equals(t.DeviceId, deviceId, StringComparison.Ordinal)
                                     && t.RevokedAt is null)
                         .ToList())
            {
                _tokens[token.TokenId] = token with { RevokedAt = at };
            }
            return Task.CompletedTask;
        }

        public void CommitInto(
            Dictionary<string, EnrollmentRecord> enrollmentTarget,
            Dictionary<string, DeviceRecord> deviceTarget,
            Dictionary<string, TokenRecord> tokenTarget)
        {
            enrollmentTarget.Clear();
            foreach (var (key, value) in _enrollments) enrollmentTarget[key] = value;

            deviceTarget.Clear();
            foreach (var (key, value) in _devices) deviceTarget[key] = value;

            tokenTarget.Clear();
            foreach (var (key, value) in _tokens) tokenTarget[key] = value;
        }
    }
}
