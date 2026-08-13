using System.Net;
using System.Text.Json;
using Fleet.Orchestrator.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Orchestrator.Tests.Services;

public class DockerServiceCreateContainerTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Captures the last request body sent to the fake Docker API and returns a
    /// successful container-create response.
    /// </summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);

            // Minimal Docker API create-container success response
            var json = """{"Id":"abc123def456"}""";
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    private static (DockerService svc, CapturingHandler handler) BuildService()
    {
        var capturing = new CapturingHandler();
        var httpClient = new HttpClient(capturing) { BaseAddress = new Uri("http://localhost") };
        var svc = new DockerService(NullLogger<DockerService>.Instance, httpClient);
        return (svc, capturing);
    }

    private static JsonElement GetLogConfig(string requestBody)
    {
        using var doc = JsonDocument.Parse(requestBody);
        return doc.RootElement.GetProperty("HostConfig").GetProperty("LogConfig").Clone();
    }

    // ── LogConfig present in both branches ────────────────────────────────────

    [Fact]
    public async Task CreateContainerAsync_WithoutHostPort_IncludesLogConfig()
    {
        var (svc, handler) = BuildService();

        await svc.CreateContainerAsync(
            "test-container", "test-image", 512L * 1024 * 1024,
            new[] { "ENV=val" }, new[] { "/a:/b" }, "test-net");

        Assert.NotNull(handler.LastRequestBody);
        var logConfig = GetLogConfig(handler.LastRequestBody!);
        Assert.Equal("json-file", logConfig.GetProperty("Type").GetString());
        var cfg = logConfig.GetProperty("Config");
        Assert.True(cfg.TryGetProperty("max-size", out _), "max-size key missing");
        Assert.True(cfg.TryGetProperty("max-file", out _), "max-file key missing");
    }

    [Fact]
    public async Task CreateContainerAsync_WithHostPort_IncludesLogConfig()
    {
        var (svc, handler) = BuildService();

        await svc.CreateContainerAsync(
            "test-container", "test-image", 512L * 1024 * 1024,
            new[] { "ENV=val" }, new[] { "/a:/b" }, "test-net",
            hostPort: 9999);

        Assert.NotNull(handler.LastRequestBody);
        var logConfig = GetLogConfig(handler.LastRequestBody!);
        Assert.Equal("json-file", logConfig.GetProperty("Type").GetString());
        var cfg = logConfig.GetProperty("Config");
        Assert.True(cfg.TryGetProperty("max-size", out _), "max-size key missing");
        Assert.True(cfg.TryGetProperty("max-file", out _), "max-file key missing");
    }

    // ── Default values ────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateContainerAsync_NoLogParams_UsesDefaults()
    {
        var (svc, handler) = BuildService();

        await svc.CreateContainerAsync(
            "test-container", "test-image", 512L * 1024 * 1024,
            new[] { "ENV=val" }, new[] { "/a:/b" }, "test-net");

        var logConfig = GetLogConfig(handler.LastRequestBody!);
        var cfg = logConfig.GetProperty("Config");
        Assert.Equal(DockerService.DefaultLogMaxSize, cfg.GetProperty("max-size").GetString());
        Assert.Equal(DockerService.DefaultLogMaxFile, cfg.GetProperty("max-file").GetString());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", "  ")]
    public async Task CreateContainerAsync_EmptyOrWhitespaceLogParams_FallsBackToDefaults(
        string? logMaxSize, string? logMaxFile)
    {
        var (svc, handler) = BuildService();

        await svc.CreateContainerAsync(
            "test-container", "test-image", 512L * 1024 * 1024,
            new[] { "ENV=val" }, new[] { "/a:/b" }, "test-net",
            logMaxSize: logMaxSize, logMaxFile: logMaxFile);

        var logConfig = GetLogConfig(handler.LastRequestBody!);
        var cfg = logConfig.GetProperty("Config");
        Assert.Equal(DockerService.DefaultLogMaxSize, cfg.GetProperty("max-size").GetString());
        Assert.Equal(DockerService.DefaultLogMaxFile, cfg.GetProperty("max-file").GetString());
    }

    // ── Custom values are passed through ─────────────────────────────────────

    [Fact]
    public async Task CreateContainerAsync_CustomLogParams_ArePassedThrough()
    {
        var (svc, handler) = BuildService();

        await svc.CreateContainerAsync(
            "test-container", "test-image", 512L * 1024 * 1024,
            new[] { "ENV=val" }, new[] { "/a:/b" }, "test-net",
            logMaxSize: "50m", logMaxFile: "5");

        var logConfig = GetLogConfig(handler.LastRequestBody!);
        var cfg = logConfig.GetProperty("Config");
        Assert.Equal("50m", cfg.GetProperty("max-size").GetString());
        Assert.Equal("5", cfg.GetProperty("max-file").GetString());
    }
}
