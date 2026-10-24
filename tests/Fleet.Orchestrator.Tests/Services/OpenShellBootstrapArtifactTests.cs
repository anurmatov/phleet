extern alias FleetAgent;
using System.Formats.Tar;
using System.Net;
using System.Text;
using System.Text.Json;
using Fleet.Orchestrator.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using Guard = FleetAgent::Fleet.Agent.Services.OpenShellStartupGuard;

namespace Fleet.Orchestrator.Tests.Services;

public sealed class OpenShellBootstrapArtifactTests
{
    [Fact]
    public async Task FreshArchiveToActualFileToActualGuard_UsesOneLaunchOnlyAndNoStartedHelper()
    {
        using var fixture = new OwnedEngine();
        var plan = OpenShellPolicyCompilerTests.Plan(); var launch = Guid.NewGuid(); var epoch = Guid.NewGuid();
        using var engine = new HttpClient(fixture) { BaseAddress = new("http://owned-engine.invalid") };
        var artifacts = new OpenShellBootstrapArtifacts(engine, fixture.Custody);
        await artifacts.DeliverAsync(plan, launch, epoch, CancellationToken.None);
        var capability = artifacts.ReadCapability(plan.Hash, launch, epoch);
        Assert.Equal(64, capability.Length);
        Assert.DoesNotContain(capability, JsonSerializer.Serialize(plan));
        var spec = artifacts.Materialize(plan, launch, epoch, DateTime.UtcNow.AddSeconds(50));
        Assert.Contains(OpenShellBootstrapArtifacts.VolumeName(launch, epoch), spec.Template.DriverConfig.ToString());
        Assert.DoesNotContain(capability, spec.ToString());
        var configuration = new ConfigurationBuilder().AddCommandLine(spec.Command.Skip(2).ToArray()).Build();
        var guard = new Guard(configuration, fixture.MountRoot);
        var request = new DefaultHttpContext(); request.Request.Headers.Authorization = "Bearer " + capability;
        using var ingress = new HttpClient(new AcceptedIngress());
        await guard.ProbeAsync(request, ingress);
        Assert.Equal(204, request.Response.StatusCode); Assert.Equal(0, fixture.Starts); Assert.Equal(1, fixture.Archives);
        Assert.Equal(1, fixture.HelperDeletes);
        Assert.ThrowsAny<Exception>(() => artifacts.ReadCapability(plan.Hash, launch, Guid.NewGuid()));
        Assert.ThrowsAny<Exception>(() => artifacts.ReadCapability(new string('f', 64), launch, epoch));
        File.Delete(Path.Combine(fixture.MountRoot, "app/generated/bootstrap.capability"));
        Assert.Throws<FileNotFoundException>(() => new Guard(configuration, fixture.MountRoot));
        await Assert.ThrowsAnyAsync<Exception>(() => artifacts.DeliverAsync(plan, launch, epoch, CancellationToken.None));
        Assert.Equal(1, fixture.Archives);
    }

    [Theory]
    [InlineData("volume_exists")]
    [InlineData("volume_error")]
    [InlineData("archive_error")]
    [InlineData("delete_error")]
    [InlineData("wrong_helper")]
    [InlineData("helper_started")]
    [InlineData("helper_image")]
    [InlineData("helper_user")]
    [InlineData("helper_network")]
    [InlineData("helper_mount")]
    public async Task DeliveryFailure_DoesNotQualifyAnArtifactOrStartAnyProcess(string fault)
    {
        using var fixture = new OwnedEngine(fault);
        using var engine = new HttpClient(fixture) { BaseAddress = new("http://owned-engine.invalid") };
        var artifacts = new OpenShellBootstrapArtifacts(engine, fixture.Custody);
        var launch = Guid.NewGuid(); var epoch = Guid.NewGuid(); var plan = OpenShellPolicyCompilerTests.Plan();
        await Assert.ThrowsAnyAsync<Exception>(() => artifacts.DeliverAsync(plan, launch, epoch, CancellationToken.None));
        Assert.ThrowsAny<Exception>(() => artifacts.ReadCapability(plan.Hash, launch, epoch));
        Assert.Equal(0, fixture.Starts);
        if (fault.StartsWith("volume_")) Assert.Equal(0, fixture.HelperCreates);
        else Assert.Equal(1, fixture.HelperCreates);
        if (fault == "delete_error") Assert.Equal(1, fixture.Archives);
    }

    [Fact]
    public async Task SeparateLaunchesReceiveDistinctRandomCapabilities()
    {
        using var a = new OwnedEngine(); using var b = new OwnedEngine();
        using var ea = new HttpClient(a) { BaseAddress = new("http://owned-engine.invalid") };
        using var eb = new HttpClient(b) { BaseAddress = new("http://owned-engine.invalid") };
        var x = new OpenShellBootstrapArtifacts(ea, a.Custody); var y = new OpenShellBootstrapArtifacts(eb, b.Custody);
        var plan = OpenShellPolicyCompilerTests.Plan(); var id = Guid.NewGuid(); var epoch = Guid.NewGuid();
        await x.DeliverAsync(plan, id, epoch, default); await y.DeliverAsync(plan, id, epoch, default);
        Assert.NotEqual(x.ReadCapability(plan.Hash, id, epoch), y.ReadCapability(plan.Hash, id, epoch));
    }

    private sealed class AcceptedIngress : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Assert.Equal("/identity/ready", request.RequestUri!.AbsolutePath); Assert.Null(request.Headers.Authorization); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); }
    }
    internal sealed class OwnedEngine(string fault = "") : HttpMessageHandler
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "bootstrap-test-" + Guid.NewGuid().ToString("N"));
        public string Custody => Path.Combine(Root, "custody");
        public string MountRoot => Path.Combine(Root, "mount");
        public int Starts, Archives, HelperCreates, HelperDeletes;
        private bool _deleted;
        private string _volume = "", _nonce = "", _image = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/start")) { Starts++; throw new Exception("never start fixture helper"); }
            if (path.Contains("/volumes/") && request.Method == HttpMethod.Get)
                return new(fault == "volume_exists" ? HttpStatusCode.OK : fault == "volume_error" ? HttpStatusCode.InternalServerError : HttpStatusCode.NotFound);
            if (path == "/v1.41/volumes/create")
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                _volume = json.RootElement.GetProperty("Name").GetString()!;
                _nonce = json.RootElement.GetProperty("Labels").GetProperty("bootstrap-owner").GetString()!;
                return Json(new { Name = _volume, Labels = new Dictionary<string, string> { ["bootstrap-owner"] = _nonce } });
            }
            if (path == "/v1.41/containers/create")
            {
                HelperCreates++;
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                _image = json.RootElement.GetProperty("Image").GetString()!;
                Assert.Equal("none", json.RootElement.GetProperty("HostConfig").GetProperty("NetworkMode").GetString());
                Assert.Equal("1000:1000", json.RootElement.GetProperty("User").GetString());
                return Json(new { Id = new string('a', 64) }, HttpStatusCode.Created);
            }
            if (path.EndsWith("/json"))
            {
                if (_deleted) return new(HttpStatusCode.NotFound);
                return Json(new { Id = new string(fault == "wrong_helper" ? 'b' : 'a', 64), State = new { Running = false, Status = fault == "helper_started" ? "exited" : "created" },
                    Config = new { Image = fault == "helper_image" ? "other" : _image, User = fault == "helper_user" ? "0" : "1000:1000", Labels = new Dictionary<string, string> { ["bootstrap-owner"] = _nonce } },
                    HostConfig = new { NetworkMode = fault == "helper_network" ? "host" : "none", ReadonlyRootfs = true },
                    Mounts = new[] { new { Type = "volume", Name = fault == "helper_mount" ? "foreign-volume" : _volume, Destination = "/app/generated", RW = true } } });
            }
            if (path.EndsWith("/archive"))
            {
                if (fault == "archive_error") return new(HttpStatusCode.InternalServerError);
                Assert.Contains("copyUIDGID=true", request.RequestUri.Query);
                Assert.Contains("noOverwriteDirNonDir=true", request.RequestUri.Query);
                Archives++;
                await using var stream = await request.Content!.ReadAsStreamAsync(ct); using var reader = new TarReader(stream);
                var entry = reader.GetNextEntry()!; Assert.Equal("bootstrap.capability", entry.Name);
                Assert.Equal(1000, entry.Uid); Assert.Equal(1000, entry.Gid); Assert.Equal(UnixFileMode.UserRead, entry.Mode);
                Directory.CreateDirectory(Path.Combine(MountRoot, "app/generated"));
                await using var file = File.Create(Path.Combine(MountRoot, "app/generated/bootstrap.capability"));
                await entry.DataStream!.CopyToAsync(file, ct); Assert.Null(reader.GetNextEntry());
                return new(HttpStatusCode.OK);
            }
            if (request.Method == HttpMethod.Delete)
            { HelperDeletes++; _deleted = true; return new(fault == "delete_error" ? HttpStatusCode.InternalServerError : HttpStatusCode.NoContent); }
            throw new Exception("unexpected fixture call " + request.Method + " " + path);
        }
        private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        protected override void Dispose(bool disposing) { if (Directory.Exists(Root)) Directory.Delete(Root, true); base.Dispose(disposing); }
    }
}
