using System.Data.Common;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Fleet.Orchestrator.Auth;

/// <summary>
/// Sign-in records in the orchestrator database.
/// </summary>
/// <remarks>
/// <para>
/// One unit of work at a time, for the whole process: a gate around a database transaction. The
/// orchestrator is a single instance and these operations are short, so serialising them costs
/// nothing a caller can notice and removes every question about two registrations racing for the
/// last device slot or one code being consumed twice.
/// </para>
/// <para>
/// The database's execution strategy may run the body again after a transient fault, each time on
/// a clean change tracker. That is why <see cref="IAuthStore"/> requires a body to derive what it
/// writes from what it reads.
/// </para>
/// <para>
/// Record ids are case-sensitive base64url and the database's default collation is not, so every
/// lookup is confirmed with an ordinal comparison before a row is believed.
/// </para>
/// </remarks>
public sealed class EfAuthStore(IServiceScopeFactory scopes) : IAuthStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<T> InTransactionAsync<T>(
        Func<IAuthStoreTransaction, CancellationToken, Task<T>> body, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

            if (!db.Database.IsRelational())
            {
                // The in-memory provider has no transactions; the gate is the whole critical
                // section there. Nothing but tests runs on it.
                return await body(new Transaction(db), ct);
            }

            var strategy = db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async token =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(token);
                var result = await body(new Transaction(db), token);
                await transaction.CommitAsync(token);
                return result;
            }, ct);
        }
        catch (Exception ex) when (ex is DbException or DbUpdateException or RetryLimitExceededException or TimeoutException)
        {
            // The type is kept and the message is not: a provider message can carry a statement.
            throw new AuthStoreUnavailableException($"The sign-in store did not answer ({ex.GetType().Name}).", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class Transaction(OrchestratorDbContext db) : IAuthStoreTransaction
    {
        public async Task<UserRecord?> FindUserAsync(string userId, CancellationToken ct)
        {
            var row = await db.AuthUsers.FindAsync([userId], ct);
            return row is null || !Same(row.UserId, userId) ? null : new UserRecord
            {
                UserId = row.UserId,
                DisplayName = row.DisplayName,
                TelegramUserId = row.TelegramUserId,
                CreatedAt = Utc(row.CreatedAt),
            };
        }

        public async Task SaveUserAsync(UserRecord record, CancellationToken ct)
        {
            var row = await db.AuthUsers.FindAsync([record.UserId], ct);
            if (row is null)
            {
                row = new AuthUser { UserId = record.UserId };
                db.AuthUsers.Add(row);
            }

            row.DisplayName = record.DisplayName;
            row.TelegramUserId = record.TelegramUserId;
            row.CreatedAt = record.CreatedAt.UtcDateTime;
            await db.SaveChangesAsync(ct);
        }

        public async Task<EnrollmentRecord?> FindEnrollmentAsync(string enrollmentId, CancellationToken ct)
        {
            var row = await db.AuthEnrollments.FindAsync([enrollmentId], ct);
            return row is null || !Same(row.EnrollmentId, enrollmentId) ? null : new EnrollmentRecord
            {
                EnrollmentId = row.EnrollmentId,
                CodeHash = row.CodeHash,
                PrincipalId = row.UserId,
                DeviceName = row.DeviceName,
                ExpiresAt = Utc(row.ExpiresAt),
                ConsumedAt = Utc(row.ConsumedAt),
                DeviceId = row.DeviceId,
                RevokedAt = Utc(row.RevokedAt),
            };
        }

        public async Task SaveEnrollmentAsync(EnrollmentRecord record, CancellationToken ct)
        {
            var row = await db.AuthEnrollments.FindAsync([record.EnrollmentId], ct);
            if (row is null)
            {
                row = new AuthEnrollment
                {
                    EnrollmentId = record.EnrollmentId,
                    CodeHash = record.CodeHash,
                    UserId = record.PrincipalId,
                };
                db.AuthEnrollments.Add(row);
            }

            row.CodeHash = record.CodeHash;
            row.UserId = record.PrincipalId;
            row.DeviceName = record.DeviceName;
            row.ExpiresAt = record.ExpiresAt.UtcDateTime;
            row.ConsumedAt = record.ConsumedAt?.UtcDateTime;
            row.DeviceId = record.DeviceId;
            row.RevokedAt = record.RevokedAt?.UtcDateTime;
            await db.SaveChangesAsync(ct);
        }

        public async Task<DeviceRecord?> FindDeviceAsync(string deviceId, CancellationToken ct)
        {
            var row = await db.AuthDevices.FindAsync([deviceId], ct);
            return row is null || !Same(row.DeviceId, deviceId) ? null : ToRecord(row);
        }

        public async Task SaveDeviceAsync(DeviceRecord record, CancellationToken ct)
        {
            var row = await db.AuthDevices.FindAsync([record.DeviceId], ct);
            if (row is null)
            {
                row = new AuthDevice
                {
                    DeviceId = record.DeviceId,
                    UserId = record.PrincipalId,
                    Kind = KindName(record.Kind),
                };
                db.AuthDevices.Add(row);
            }

            row.UserId = record.PrincipalId;
            row.Kind = KindName(record.Kind);
            row.Name = record.Name;
            row.SecretHash = record.SecretHash;
            row.EnrollmentId = record.EnrollmentId;
            row.RegisteredAt = record.RegisteredAt.UtcDateTime;
            row.FirstTokenMintedAt = record.FirstTokenMintedAt?.UtcDateTime;
            row.LastSeenAt = record.LastSeenAt?.UtcDateTime;
            row.RevokedAt = record.RevokedAt?.UtcDateTime;
            await db.SaveChangesAsync(ct);
        }

        public Task<int> CountActiveDevicesAsync(string principalId, CancellationToken ct) =>
            db.AuthDevices.CountAsync(d => d.UserId == principalId && d.RevokedAt == null, ct);

        public async Task<IReadOnlyList<DeviceRecord>> ListDevicesAsync(string principalId, CancellationToken ct)
        {
            var rows = await db.AuthDevices
                .Where(d => d.UserId == principalId)
                .OrderBy(d => d.RegisteredAt)
                .ThenBy(d => d.DeviceId)
                .ToListAsync(ct);
            return rows.Where(d => Same(d.UserId, principalId)).Select(ToRecord).ToList();
        }

        public async Task<TokenRecord?> FindTokenAsync(string tokenId, CancellationToken ct)
        {
            var row = await db.AuthTokens.FindAsync([tokenId], ct);
            return row is null || !Same(row.TokenId, tokenId) ? null : new TokenRecord
            {
                TokenId = row.TokenId,
                TokenHash = row.TokenHash,
                DeviceId = row.DeviceId,
                Kind = row.Kind == SessionKind ? TokenKind.Session : TokenKind.Access,
                ExpiresAt = Utc(row.ExpiresAt),
                RevokedAt = Utc(row.RevokedAt),
            };
        }

        public async Task SaveTokenAsync(TokenRecord record, CancellationToken ct)
        {
            var row = await db.AuthTokens.FindAsync([record.TokenId], ct);
            if (row is null)
            {
                row = new AuthToken
                {
                    TokenId = record.TokenId,
                    TokenHash = record.TokenHash,
                    DeviceId = record.DeviceId,
                    Kind = record.Kind == TokenKind.Session ? SessionKind : AccessKind,
                };
                db.AuthTokens.Add(row);
            }

            row.TokenHash = record.TokenHash;
            row.DeviceId = record.DeviceId;
            row.Kind = record.Kind == TokenKind.Session ? SessionKind : AccessKind;
            row.ExpiresAt = record.ExpiresAt.UtcDateTime;
            row.RevokedAt = record.RevokedAt?.UtcDateTime;
            await db.SaveChangesAsync(ct);
        }

        public Task<bool> HasLiveTokenAsync(string deviceId, DateTimeOffset now, CancellationToken ct)
        {
            var moment = now.UtcDateTime;
            return db.AuthTokens.AnyAsync(
                t => t.DeviceId == deviceId && t.RevokedAt == null && t.ExpiresAt > moment, ct);
        }

        public async Task RevokeTokensForDeviceAsync(string deviceId, DateTimeOffset at, CancellationToken ct)
        {
            var rows = await db.AuthTokens
                .Where(t => t.DeviceId == deviceId && t.RevokedAt == null)
                .ToListAsync(ct);
            foreach (var row in rows.Where(t => Same(t.DeviceId, deviceId)))
                row.RevokedAt = at.UtcDateTime;
            await db.SaveChangesAsync(ct);
        }

        public async Task DeleteSpentAsync(DateTimeOffset before, CancellationToken ct)
        {
            var moment = before.UtcDateTime;

            // Loaded and removed rather than deleted in one statement: the sets are small, and this
            // way the same code runs on every provider the tests use.
            db.AuthTokens.RemoveRange(await db.AuthTokens
                .Where(t => t.ExpiresAt < moment)
                .ToListAsync(ct));

            db.AuthEnrollments.RemoveRange(await db.AuthEnrollments
                .Where(e => e.ExpiresAt < moment
                            && (e.ConsumedAt == null || e.ConsumedAt < moment)
                            && (e.RevokedAt == null || e.RevokedAt < moment))
                .ToListAsync(ct));

            await db.SaveChangesAsync(ct);
        }

        private const string AccessKind = "access";
        private const string SessionKind = "session";
        private const string AppKind = "app";
        private const string BrowserKind = "browser";

        private static string KindName(DeviceKind kind) => kind == DeviceKind.Browser ? BrowserKind : AppKind;

        private static DeviceRecord ToRecord(AuthDevice row) => new()
        {
            DeviceId = row.DeviceId,
            PrincipalId = row.UserId,
            Kind = row.Kind == BrowserKind ? DeviceKind.Browser : DeviceKind.App,
            Name = row.Name,
            SecretHash = row.SecretHash,
            EnrollmentId = row.EnrollmentId,
            RegisteredAt = Utc(row.RegisteredAt),
            FirstTokenMintedAt = Utc(row.FirstTokenMintedAt),
            LastSeenAt = Utc(row.LastSeenAt),
            RevokedAt = Utc(row.RevokedAt),
        };

        private static bool Same(string stored, string asked) =>
            string.Equals(stored, asked, StringComparison.Ordinal);

        // Stored as UTC without an offset; the provider hands it back with an unspecified kind.
        private static DateTimeOffset Utc(DateTime value) =>
            new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

        private static DateTimeOffset? Utc(DateTime? value) =>
            value is null ? null : Utc(value.Value);
    }
}
