using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
namespace Fleet.Agent.Services.MessageCopy;

/// <summary>Own loopback-only Kestrel app, started before any task intake or CLI warmup.</summary>
public sealed class MessageCopyListener(MessageCopyTools tools, ILoggerFactory logs) : IHostedService, IAsyncDisposable
{
    public const string ServerName = "fleet-telegram-copy";
    public const string Path = "/telegram-copy/v1/mcp";
    public bool Ready { get; private set; }
    private WebApplication? _app;
    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            // Empty configuration prevents inherited Kestrel endpoints from exposing this server.
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
            builder.Services.AddSingleton(logs);
            builder.Services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
            builder.Services.AddRouting();
            // Target-instance overload: every call runs on the one parent-owned instance, so its
            // serialization, deadline and counters hold. The parameterless generic overload
            // would construct a new instance per call against this child container.
            var server = builder.Services.AddMcpServer(options => options.ServerInfo = new Implementation { Name = ServerName, Version = "1" })
                .WithHttpTransport(options => options.Stateless = true);
            server.WithTools([tools.CreateTool()]);
            builder.WebHost.UseKestrelCore().ConfigureKestrel(options =>
            { options.AddServerHeader = false; options.Listen(IPAddress.Loopback, 8092); });
            _app = builder.Build();
            _app.Use(async (context, next) =>
            {
                if (context.Connection.LocalPort != 8092 || context.Request.Path.Value != Path)
                { context.Response.StatusCode = 404; return; }
                await next(context);
            });
            _app.MapMcp(Path); await _app.StartAsync(ct); Ready = true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logs.CreateLogger<MessageCopyListener>().LogError("Message copy listener failed to bind: {type}", ex.GetType().Name);
            if (_app is not null) await _app.DisposeAsync(); _app = null;
            // Main agent continues; the CLI reports the emitted MCP server as failed.
        }
    }
    public async Task StopAsync(CancellationToken ct) { Ready = false; if (_app is not null) await _app.StopAsync(ct); }
    public async ValueTask DisposeAsync() { Ready = false; if (_app is not null) await _app.DisposeAsync(); _app = null; }
}
