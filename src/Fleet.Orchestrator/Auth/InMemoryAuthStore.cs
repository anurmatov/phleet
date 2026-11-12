namespace Fleet.Orchestrator.Auth;

/// <summary>
/// Sign-in records held in memory, for an orchestrator started without a database and for tests.
/// </summary>
/// <remarks>
/// Nothing survives a restart: every browser signs in again and every app enrolls again. That is
/// the right behaviour for a process that has no database at all, and it is why the durable store
/// is used whenever one is configured.
/// </remarks>
public sealed class InMemoryAuthStore : IAuthStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, UserRecord> _users = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EnrollmentRecord> _enrollments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeviceRecord> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TokenRecord> _tokens = new(StringComparer.Ordinal);

    /// <summary>Test seam: every operation fails as an unavailable store would.</summary>
    public bool FailEveryOperation { get; set; }

    /// <summary>Test seam: the body runs and its writes are then lost, as a failed commit would lose them.</summary>
    public bool FailOnCommit { get; set; }

    public async Task<T> InTransactionAsync<T>(
        Func<IAuthStoreTransaction, CancellationToken, Task<T>> body, CancellationToken ct)
    {
        if (FailEveryOperation)
            throw new AuthStoreUnavailableException("The sign-in store is unavailable.");

        await _gate.WaitAsync(ct);
        try
        {
            // Work on a copy. Nothing the body writes is visible to anyone else until commit, so a
            // body that throws — or a commit that fails — leaves no partial state behind.
            var scratch = new Transaction(_users, _enrollments, _devices, _tokens);
            var result = await body(scratch, ct);

            if (FailOnCommit)
                throw new AuthStoreUnavailableException("The sign-in store failed to commit.");

            scratch.CommitInto(_users, _enrollments, _devices, _tokens);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal IReadOnlyCollection<EnrollmentRecord> Enrollments => _enrollments.Values;

    internal IReadOnlyCollection<DeviceRecord> Devices => _devices.Values;

    internal IReadOnlyCollection<TokenRecord> Tokens => _tokens.Values;

    private sealed class Transaction(
        Dictionary<string, UserRecord> users,
        Dictionary<string, EnrollmentRecord> enrollments,
        Dictionary<string, DeviceRecord> devices,
        Dictionary<string, TokenRecord> tokens) : IAuthStoreTransaction
    {
        private readonly Dictionary<string, UserRecord> _users = new(users, StringComparer.Ordinal);
        private readonly Dictionary<string, EnrollmentRecord> _enrollments = new(enrollments, StringComparer.Ordinal);
        private readonly Dictionary<string, DeviceRecord> _devices = new(devices, StringComparer.Ordinal);
        private readonly Dictionary<string, TokenRecord> _tokens = new(tokens, StringComparer.Ordinal);

        public Task<UserRecord?> FindUserAsync(string userId, CancellationToken ct) =>
            Task.FromResult(_users.GetValueOrDefault(userId));

        public Task SaveUserAsync(UserRecord record, CancellationToken ct)
        {
            _users[record.UserId] = record;
            return Task.CompletedTask;
        }

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

        public Task<IReadOnlyList<DeviceRecord>> ListDevicesAsync(string principalId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DeviceRecord>>(_devices.Values
                .Where(d => string.Equals(d.PrincipalId, principalId, StringComparison.Ordinal))
                .OrderBy(d => d.RegisteredAt)
                .ThenBy(d => d.DeviceId, StringComparer.Ordinal)
                .ToList());

        public Task<TokenRecord?> FindTokenAsync(string tokenId, CancellationToken ct) =>
            Task.FromResult(_tokens.GetValueOrDefault(tokenId));

        public Task SaveTokenAsync(TokenRecord record, CancellationToken ct)
        {
            _tokens[record.TokenId] = record;
            return Task.CompletedTask;
        }

        public Task<bool> HasLiveTokenAsync(string deviceId, DateTimeOffset now, CancellationToken ct) =>
            Task.FromResult(_tokens.Values.Any(t =>
                string.Equals(t.DeviceId, deviceId, StringComparison.Ordinal)
                && t.RevokedAt is null
                && t.ExpiresAt > now));

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

        public Task DeleteSpentAsync(DateTimeOffset before, CancellationToken ct)
        {
            foreach (var token in _tokens.Values.Where(t => t.ExpiresAt < before).ToList())
                _tokens.Remove(token.TokenId);

            foreach (var enrollment in _enrollments.Values
                         .Where(e => e.ExpiresAt < before
                                     && (e.ConsumedAt is null || e.ConsumedAt < before)
                                     && (e.RevokedAt is null || e.RevokedAt < before))
                         .ToList())
            {
                _enrollments.Remove(enrollment.EnrollmentId);
            }

            return Task.CompletedTask;
        }

        public void CommitInto(
            Dictionary<string, UserRecord> userTarget,
            Dictionary<string, EnrollmentRecord> enrollmentTarget,
            Dictionary<string, DeviceRecord> deviceTarget,
            Dictionary<string, TokenRecord> tokenTarget)
        {
            Replace(userTarget, _users);
            Replace(enrollmentTarget, _enrollments);
            Replace(deviceTarget, _devices);
            Replace(tokenTarget, _tokens);
        }

        private static void Replace<T>(Dictionary<string, T> target, Dictionary<string, T> source)
        {
            target.Clear();
            foreach (var (key, value) in source) target[key] = value;
        }
    }
}
