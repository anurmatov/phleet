using System.Net;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services.Push;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Orchestrator.Tests.Auth;

public class PushRevocationTests
{
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task AppPushRows_AreBoundAndRemovedByRemoteDeviceRevocation()
    {
        await using var host = await SignInHost.StartAsync(durable: true, push: true);
        var (cookie, deviceId, _, access) = await host.EnrolledAppAsync();
        var code = await host.IssueEnrollmentCodeAsync(cookie);
        var registered = await SignInHost.JsonAsync(await host.RegisterAsync(code));
        var minted = await SignInHost.JsonAsync(await host.TokenAsync(registered.GetProperty("deviceId").GetString(), registered.GetProperty("deviceSecret").GetString()));
        var other = minted.GetProperty("accessToken").GetString();
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Put, "/api/push/devices/install-one", bearer: access, body: new { token = Token })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendAsync(HttpMethod.Put, "/api/push/devices/install-one", bearer: other, body: new { token = Token })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendAsync(HttpMethod.Delete, "/api/push/devices/install-one", bearer: other)).StatusCode);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            Assert.Equal(deviceId, (await db.PushDevices.SingleAsync()).AuthDeviceId);
        }
        Assert.Equal(HttpStatusCode.NoContent, (await host.SendAsync(HttpMethod.Post, $"/api/auth/devices/{deviceId}/revoke", cookie: cookie, requestHeader: true)).StatusCode);
        await using (var scope = host.Services.CreateAsyncScope())
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().PushDevices.ToListAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(HttpMethod.Put, "/api/push/devices/install-one", bearer: access, body: new { token = Token })).StatusCode);
    }
    [Fact]
    public async Task Delivery_DropsRevokedOwnerRetries_EvenIfAConcurrentPutLeftARow()
    {
        await using var host = await SignInHost.StartAsync(durable: true, push: true);
        var (cookie, device, _, access) = await host.EnrolledAppAsync();
        (await host.SendAsync(HttpMethod.Put, "/api/push/devices/install-one", bearer: access,
            body: new { token = Token })).EnsureSuccessStatusCode();
        var apns = new RecordingApns();
        var delivery = new PushDelivery(host.Services.GetRequiredService<IServiceScopeFactory>(), apns, NullLogger.Instance);
        var message = PushMessages.ChatReply("agent1", "Agent", "01JCONVERSATION00000000001", 1);
        await delivery.DeliverAsync([message], _ => true, CancellationToken.None);
        Assert.Equal(1, delivery.Owed);
        (await host.SendAsync(HttpMethod.Post, $"/api/auth/devices/{device}/revoke", cookie: cookie, requestHeader: true)).EnsureSuccessStatusCode();
        // Simulate a PUT that read the live owner before revoke but committed its row afterwards.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            db.PushDevices.Add(new PushDevice { DeviceId = "install-one", AuthDeviceId = device,
                Token = Token, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        await delivery.DeliverAsync([message], _ => true, CancellationToken.None);
        Assert.Equal(1, apns.Calls);
        Assert.Equal(0, delivery.Owed);
    }

    private sealed class RecordingApns : IApnsClient
    {
        public int Calls;
        public Task<ApnsResult> SendAsync(string deviceToken, PushMessage message, CancellationToken ct)
        { Calls++; return Task.FromResult(new ApnsResult(ApnsOutcome.Failed)); }
    }

}
