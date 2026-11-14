using Fleet.Orchestrator.Auth;
using Fleet.Orchestrator.Services;
using System.Net.WebSockets;

namespace Fleet.Orchestrator.Endpoints;

/// <summary>
/// The two WebSockets the dashboard holds open: agent state updates, and one agent's container log.
/// </summary>
public static class LiveSocketEndpoints
{
    public static IEndpointRouteBuilder MapLiveSocketEndpoints(this IEndpointRouteBuilder app)
    {
        // WebSocket: real-time agent state updates
        app.Map("/ws", async (HttpContext context, AgentRegistry registry) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var authWatch = ControlSocketAccess.WatchAsync(context, socket, lifetime);
            try
            {
                registry.AddSocket(socket);
                await registry.BroadcastAllToSocket(socket);
                var buffer = new byte[256];
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(buffer, lifetime.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by client", lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (WebSocketException) { }
            finally
            {
                // Every reconnect of a dashboard would otherwise leave its old socket registered.
                registry.RemoveSocket(socket);
                await lifetime.CancelAsync();
                await authWatch;
            }
        });

        // WebSocket: real-time container log streaming per agent
        app.Map("/ws/logs/{agentName}", async (string agentName, HttpContext context, AgentRegistry registry, DockerService docker) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();

            var agent = registry.Get(agentName);
            var containerName = agent?.ContainerName ?? agentName;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);

            var authWatch = ControlSocketAccess.WatchAsync(context, socket, cts);
            // Receive loop — detect client-initiated close
            var receiver = Task.Run(async () =>
            {
                var buf = new byte[64];
                try
                {
                    while (socket.State == WebSocketState.Open)
                    {
                        var result = await socket.ReceiveAsync(buf, cts.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                            break;
                    }
                }
                catch { /* disconnect */ }
                finally { cts.Cancel(); }
            }, CancellationToken.None);

            try
            {
                await foreach (var line in docker.StreamContainerLogsAsync(containerName, 200, cts.Token))
                {
                    if (socket.State != WebSocketState.Open) break;
                    var bytes = System.Text.Encoding.UTF8.GetBytes(line);
                    await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cts.Token);
                }
            }
            catch (OperationCanceledException) { /* client disconnected */ }
            catch (Exception ex)
            {
                var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
                logger.LogDebug(ex, "Log stream for {Agent} ended", agentName);
            }

            await cts.CancelAsync();
            await Task.WhenAll(receiver, authWatch);
            if (socket.State == WebSocketState.Open)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stream ended", CancellationToken.None);
        });

        return app;
    }
}
