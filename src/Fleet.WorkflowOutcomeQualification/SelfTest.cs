using Fleet.Shared;
using Fleet.Agent.Services;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fleet.WorkflowOutcomeQualification;

public static class SelfTest
{
    private static int _total;
    private static int _failed;

    public static int Run()
    {
        Test("docker_foreign_context_refuses_before_any_process", DockerForeignContextRefusesBeforeIo);
        Test("git_local_fsmonitor_never_executes", GitLocalHookRefused);
        foreach (var fault in new[] { "filter", "include", "promisor" })
            Test("git_executable_configuration_refused_" + fault, () => GitExecutableConfigRefused(fault));
        foreach (var fault in new[] { "missing", "host", "context", "symlink", "duplicate", "current-context", "default-context" })
            Test("docker_context_binding_refuses_" + fault, () => DockerContextRefuses(fault));
        foreach (var fault in new[] { "exit", "deadline", "cancel", "overflow" })
            Test("parent_exit_descendant_cleanup_" + fault, () => ExitedParentCleanup(fault));
        foreach (var fault in new[] { "exit", "deadline", "cancel", "overflow" })
            foreach (var pipes in new[] { "inherited", "closed" })
                Test("different_process_group_cleanup_" + fault + "_" + pipes, () => SeparateGroupCleanup(fault, pipes == "closed"));
        Test("loopback_fixture_uses_actual_prompt_and_strict_production_parser", LoopbackUsesProductionContracts);
        foreach (var fault in new[] { "head-exit", "status-exit", "dirty", "engine", "image", "cli" })
            Test("target_validation_refuses_" + fault, () => TargetRefuses(fault));
        foreach (var fault in new[] { "nested-id", "nested-image", "nested-config", "nested-owner", "nested-host", "nested-env", "nested-socket", "nested-read", "nested-missing" })
            Test("nested_container_binding_refuses_" + fault, () => NestedTargetRefuses(fault));
        foreach (var fault in new[] { "namespace-lower", "namespace-colon", "broker-alias" })
            Test("nested_environment_alias_refuses_" + fault, () => NestedTargetRefuses(fault));
        foreach (var fault in new[] { "valid", "name", "404", "duplicate", "oversize", "secret", "reorder" })
            Test("nested_actual_docker_reader_" + fault, () => NestedHttpInspect(fault));
        foreach (var fault in new[] { "nested-host", "nested-env", "nested-socket" })
            Test("nested_self_consistent_spec_refuses_" + fault, () => NestedSelfConsistentRefuses(fault));
        foreach (var fault in new[] { "pinned", "unhashed", "symlink" })
            Test("nested_owned_bind_" + fault, () => NestedOwnedBind(fault));
        Test("missing_nested_registry_refuses_before_process", MissingNestedRegistryRefuses);
        foreach (var fault in new[] { "unknown", "missing-role", "duplicate-id", "nonce", "symlink", "hash" })
            Test("nested_registry_refuses_before_io_" + fault, () => NestedRegistryRefusesBeforeIo(fault));
        foreach (var fault in new[] { "valid", "acked", "batch", "generation", "nonce", "items", "delivered", "acked-item",
            "time", "hash", "duplicate", "oversize", "401", "403", "404", "409", "503", "redirect", "cancel",
            "malformed", "unknown", "missing", "transport", "ack-time", "bad-receipt-hash", "content-type" })
            Test("actual_api_receipt_collector_" + fault, () => ApiReceiptCollector(fault));
        foreach (var fault in new[] { "valid", "approval", "operation", "token-hash", "token-mode", "unlisted-token", "foreign-reader", "post-read-drift" })
            Test("connected_receipt_read_" + fault, () => ConnectedReceiptRead(fault));
        Test("changed_file_refuses_before_process_or_network", ChangedFileRefusesBeforeIo);
        Test("symlinked_custody_refuses_before_process_or_network", SymlinkRefusesBeforeIo);
        Test("manifest_accepts_only_the_explicit_target", ManifestParsesExplicitTarget);
        Test("campaign_refuses_uncollected_observations_before_any_io", CampaignRefusesUncollectedObservations);
        Test("mutating_commands_refuse_incomplete_ownership_registry", MutationsRefuseIncompleteRegistry);
        Test("scenario_rejects_foreign_batch_and_nonce", ScenarioRejectsForeignIdentity);
        Test("fixture_requires_shell_result_not_nonce_in_prompt", FixtureRequiresShellResult);
        Test("fixture_rejects_malformed_typed_fields", FixtureRejectsMalformedFields);
        Test("http_rejects_ambiguous_body_framing", HttpRejectsAmbiguousFraming);
        Test("http_header_read_is_cancelable", HeaderReadIsCancelable);
        Test("process_runner_executes_only_explicit_environment", ProcessExplicitEnvironment);
        Test("process_runner_deadline_reaps_owned_child", ProcessDeadlineReapsChild);
        Test("process_runner_cancellation_reaps_owned_child", ProcessCancellationReapsChild);
        Test("process_runner_large_output_does_not_deadlock", ProcessLargeOutputDoesNotDeadlock);
        Test("command_planner_binds_argv_and_minimal_environment", CommandPlannerBindsArgvAndEnvironment);
        Test("command_planner_refuses_missing_database_environment", CommandPlannerRefusesMissingDatabaseEnvironment);
        Test("driver_validates_linked_source_image_and_metadata", DriverValidatesLinkedTarget);
        Test("inference_fixture_returns_structured_turn", InferenceFixtureReturnsStructuredTurn);
        Test("inference_fixture_shell_probe_binds_nonce", InferenceFixtureShellProbeBindsNonce);
        Test("fixture_http_handler_serves_only_responses", FixtureHttpHandlerServesOnlyResponses);
        Test("temporal_client_uses_read_only_grpc_getsysteminfo", TemporalClientUsesReadOnlyGetSystemInfo);
        Test("docker_client_requires_explicit_engine_and_image", DockerClientRequiresExplicitEngineAndImage);
        Test("scenario_assertions_accept_exact_base_oracles", ScenarioAssertionsAcceptBaseOracles);
        Test("scenario_assertions_fail_second_turn", ScenarioAssertionsFailSecondTurn);
        Test("runtime_oracles_stay_unrun_without_authorization", RuntimeOraclesStayUnrun);

        var summary = new
        {
            kind = "workflow-outcome-process-self-test",
            total = _total,
            passed = _total - _failed,
            failed = _failed,
            skipped = 0
        };
        Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return _failed == 0 ? 0 : 1;
    }

    private static void GitLocalHookRefused()
    {
        if (!OperatingSystem.IsLinux()) throw new QualificationBlockedException("Linux source fixture required");
        using var directory = new TemporaryDirectory();
        var path = WriteManifest(directory.Path);
        var manifest = QualificationManifestParser.Parse(File.ReadAllText(path));
        var git = Path.Combine(directory.Path, "tools", "git");
        File.Copy("/usr/bin/git", git, overwrite: true);
        File.SetUnixFileMode(git, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        manifest = manifest with { Executables = new Dictionary<string, HashedFile>(manifest.Executables)
            { ["git"] = new HashedFile("tools/git", Sha256(git)) } };
        var planner = new CommandPlanner(directory.Path);
        var planned = planner.Build(manifest, "git-status");
        var runner = new ProcessCommandRunner();
        ProcessCommandResult Git(params string[] argv) => runner.RunAsync(planned with { Arguments = argv })
            .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        if (Git("init", "-q").ExitCode != 0) throw new InvalidOperationException("owned Git init failed");
        var hook = Path.Combine(directory.Path, "hook.sh");
        var marker = Path.Combine(directory.Path, "hook-ran");
        File.WriteAllText(hook, "#!/bin/sh\nprintf unsafe > '" + marker + "'\n");
        File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (Git("config", "core.fsmonitor", hook).ExitCode != 0) throw new InvalidOperationException("owned Git config failed");
        var result = runner.RunAsync(planned).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        if (result.ExitCode != 0 || File.Exists(marker)) throw new InvalidOperationException("repository-local hook executed");
        if (planned.Environment["GIT_NO_LAZY_FETCH"] != "1"
            || !planned.Arguments.Contains("--ignore-submodules=all"))
            throw new InvalidOperationException("Git executable configuration guards missing");
    }

    private static void GitExecutableConfigRefused(string fault)
    {
        using var directory = new TemporaryDirectory();
        var path = WriteManifest(directory.Path);
        var manifest = QualificationManifestParser.Parse(File.ReadAllText(path));
        var config = Path.Combine(directory.Path, "source", ".git", "config");
        File.AppendAllText(config, fault switch
        {
            "filter" => "[filter \"synthetic\"]\nclean = unallowlisted-command\n",
            "include" => "[include]\npath = unowned-config\n",
            _ => "[remote \"origin\"]\npromisor = true\n"
        });
        try { new CommandPlanner(directory.Path).Build(manifest, "git-status"); }
        catch (QualificationBlockedException) { return; }
        throw new InvalidOperationException("executable Git configuration was accepted");
    }

    private static void DockerForeignContextRefusesBeforeIo()
    {
        using var directory = new TemporaryDirectory();
        var path = WriteManifest(directory.Path);
        var config = Path.Combine(directory.Path, "config.json");
        File.WriteAllText(config, "{\"currentContext\":\"foreign-context\"}");
        var doc = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        foreach (var pin in doc["configFiles"]!.AsArray())
            if (pin!["path"]!.GetValue<string>() == "config.json") pin["sha256"] = Sha256(config);
        File.WriteAllText(path, doc.ToJsonString());
        var driver = new ProcessQualificationDriver(path, new RefuseRunner(), new RefuseDocker(), new RefuseTemporal());
        try { driver.ValidateAsync().GetAwaiter().GetResult(); }
        catch (QualificationException) { return; }
        throw new InvalidOperationException("foreign Docker context reached an unowned process");
    }

    private static void DockerContextRefuses(string fault)
    {
        using var directory = new TemporaryDirectory();
        var path = WriteManifest(directory.Path);
        var manifest = QualificationManifestParser.Parse(File.ReadAllText(path));
        var relative = manifest.ConfigFiles.Single(f => f.Path.EndsWith("meta.json", StringComparison.Ordinal)).Path;
        var metadata = Path.Combine(directory.Path, relative);
        if (fault == "missing") manifest = manifest with { ConfigFiles = manifest.ConfigFiles.Where(f => f.Path != relative).ToArray() };
        if (fault is "host" or "context" or "duplicate")
        {
            var text = File.ReadAllText(metadata);
            text = fault switch
            {
                "host" => text.Replace("unix:///run/example/docker.sock", "unix:///run/foreign/docker.sock", StringComparison.Ordinal),
                "context" => text.Replace("qualification-context", "foreign-context", StringComparison.Ordinal),
                _ => text.Replace("\"Name\":", "\"Name\":\"ambiguous\",\"Name\":", StringComparison.Ordinal)
            };
            File.WriteAllText(metadata, text);
            manifest = manifest with { ConfigFiles = manifest.ConfigFiles.Select(f => f.Path == relative ? f with { Sha256 = Sha256(metadata) } : f).ToArray() };
        }
        if (fault == "default-context") manifest = manifest with { Docker = manifest.Docker with { Context = "default" } };
        if (fault == "current-context")
        {
            var config = Path.Combine(directory.Path, "config.json");
            File.WriteAllText(config, "{\"currentContext\":\"foreign-context\"}");
            manifest = manifest with { ConfigFiles = manifest.ConfigFiles.Select(f => f.Path == "config.json" ? f with { Sha256 = Sha256(config) } : f).ToArray() };
        }
        if (fault == "symlink")
        {
            var copy = Path.Combine(directory.Path, "other-meta.json");
            File.Move(metadata, copy);
            File.CreateSymbolicLink(metadata, copy);
        }
        try { DockerConfigurationBinding.Verify(manifest, directory.Path); }
        catch (Exception e) when (e is QualificationException or QualificationBlockedException) { return; }
        throw new InvalidOperationException("unsafe Docker binding accepted");
    }

    private static void ExitedParentCleanup(string fault)
    {
        using var directory = new TemporaryDirectory();
        using var cancel = new CancellationTokenSource();
        if (fault == "cancel") cancel.CancelAfter(TimeSpan.FromMilliseconds(400));
        var script = "sleep 10 & echo $! > descendant.pid; ";
        script += fault switch
        {
            "exit" => "exit 0",
            "overflow" => "head -c 2097152 /dev/zero; exit 0",
            _ => "wait"
        };
        var command = new PlannedCommand("synthetic-descendant", "/bin/sh", ["-c", script],
            new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin", ["HOME"] = directory.Path, ["TMPDIR"] = directory.Path },
            directory.Path, TimeSpan.FromMilliseconds(600));
        try
        {
            var result = new ProcessCommandRunner().RunAsync(command, cancel.Token)
                .WaitAsync(TimeSpan.FromSeconds(4)).GetAwaiter().GetResult();
            if (fault is "deadline" or "cancel" && !result.TimedOut)
                throw new InvalidOperationException("deadline/cancellation was not retained");
            if (fault == "overflow") throw new InvalidOperationException("overflow was accepted");
        }
        catch (QualificationException e) when (fault == "overflow" && e.Message.Contains("output exceeded", StringComparison.Ordinal)) { }
        finally
        {
            var pidFile = Path.Combine(directory.Path, "descendant.pid");
            if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid))
            {
                var stat = File.Exists("/proc/" + pid + "/stat") ? File.ReadAllText("/proc/" + pid + "/stat") : "";
                if (stat.Length != 0 && !stat[(stat.LastIndexOf(')') + 2)..].StartsWith("Z ", StringComparison.Ordinal))
                {
                    try { using var child = System.Diagnostics.Process.GetProcessById(pid); child.Kill(); }
                    catch (ArgumentException) { }
                    throw new InvalidOperationException("owned descendant survived session cleanup");
                }
            }
            else throw new InvalidOperationException("owned descendant fixture never started");
        }
    }

    private static void SeparateGroupCleanup(string fault, bool closePipes)
    {
        using var directory = new TemporaryDirectory();
        using var cancel = new CancellationTokenSource();
        if (fault == "cancel") cancel.CancelAfter(TimeSpan.FromMilliseconds(700));
        // The child deliberately changes process group but retains the reserved session.
        // Parent exit and pipe ownership are independent of the child's lifetime.
        const string script = """
            import os, signal, sys, time
            child = os.fork()
            if child == 0:
                os.setpgrp()
                signal.signal(signal.SIGTERM, signal.SIG_IGN)
                with open("separate.pid", "w") as f:
                    f.write(str(os.getpid()) + " " + str(os.getsid(0)) + " " + str(os.getpgrp()))
                if sys.argv[2] == "closed":
                    for fd in (0, 1, 2):
                        os.close(fd)
                time.sleep(10)
                os._exit(0)
            until = time.monotonic() + 1
            while not os.path.exists("separate.pid") and time.monotonic() < until:
                time.sleep(0.005)
            if sys.argv[1] == "overflow":
                os.write(1, b"x" * 2097152)
            if sys.argv[1] in ("exit", "overflow"):
                os._exit(0)
            time.sleep(10)
            """;
        var command = new PlannedCommand("synthetic-separate-group", "/usr/bin/python3",
            ["-c", script, fault, closePipes ? "closed" : "inherited"],
            new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin", ["HOME"] = directory.Path,
                ["TMPDIR"] = directory.Path, ["PYTHONPYCACHEPREFIX"] = directory.Path },
            directory.Path, TimeSpan.FromMilliseconds(900));
        try
        {
            var result = new ProcessCommandRunner().RunAsync(command, cancel.Token)
                .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (fault is "deadline" or "cancel" && !result.TimedOut)
                throw new InvalidOperationException("cancellation/deadline was not retained");
            if (fault == "exit" && (result.ExitCode != 0 || result.TimedOut))
                throw new InvalidOperationException("normal target exit was not retained");
            if (fault == "overflow") throw new InvalidOperationException("overflow was accepted");
        }
        catch (QualificationException e) when (fault == "overflow" && e.Message.Contains("output exceeded", StringComparison.Ordinal)) { }
        finally
        {
            var file = Path.Combine(directory.Path, "separate.pid");
            if (!File.Exists(file)) throw new InvalidOperationException("separate-group fixture never started");
            var ids = File.ReadAllText(file).Split(' ').Select(int.Parse).ToArray();
            if (ids.Length != 3 || ids[0] != ids[2] || ids[0] == ids[1])
                throw new InvalidOperationException("fixture did not create a different process group in its session");
            var path = "/proc/" + ids[0] + "/stat";
            string state;
            try { state = File.ReadAllText(path); }
            catch (FileNotFoundException) { state = ""; }
            catch (DirectoryNotFoundException) { state = ""; }
            if (state.Length != 0 && !state[(state.LastIndexOf(')') + 2)..].StartsWith("Z ", StringComparison.Ordinal))
            {
                // Reap only this synthetic fixture's exact recorded child on red.
                using var child = System.Diagnostics.Process.GetProcessById(ids[0]);
                child.Kill();
                child.WaitForExit(1000);
                throw new InvalidOperationException("owned different-process-group child survived cleanup");
            }
        }
    }

    private static void LoopbackUsesProductionContracts()
    {
        var facts = Facts();
        var item = facts.Items.Single();
        var batch = new WorkflowOutcomeInboxBatch(facts.BatchId, facts.Generation,
            "qualificationsyntheticnonce", "operator", DateTimeOffset.UtcNow, "synthetic-hash",
            [new WorkflowOutcomeInboxItem(item.Id, item.Version, item.Namespace, item.WorkflowId, item.RunId,
                "synthetic", WorkflowOutcomeClassifications.ConfirmedFailure, "workflow_failed", "WorkflowExecutionFailed",
                2, DateTimeOffset.UtcNow, "synthetic", false, "operator", null, [])]);
        var prompt = WorkflowOutcomePrompt.Build(batch);
        var server = new InferenceFixtureServer(FixtureTarget(), InferenceFixtureMode.Turn,
            "qualificationsyntheticnonce", 0);
        try
        {
            server.StartAsync().GetAwaiter().GetResult();
            using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
            using var response = http.PostAsync("http://127.0.0.1:" + server.Port + "/v1/responses",
                new StringContent(Request(prompt), Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            if (response.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException("fixture response was not 200");
            var text = Completion(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())
                .GetProperty("response").GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString();
            if (!WorkflowOutcomeTurnOutputParser.TryParse(text, batch.BatchId, batch.Generation,
                    [new WorkflowOutcomeItemRef(item.Id, item.Version)], DateTimeOffset.UtcNow, out var output, out var error)
                || output!.Ack.Count != 1 || output.Decisions.Single().Operation != "hold")
                throw new InvalidOperationException("actual turn parser refused the inference output: " + error);
            if (WorkflowOutcomeTurnOutputParser.TryParse(text, Guid.NewGuid(), batch.Generation,
                    [new WorkflowOutcomeItemRef(item.Id, item.Version)], DateTimeOffset.UtcNow, out _, out _))
                throw new InvalidOperationException("foreign batch output was accepted");
        }
        finally { server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); }
    }

    private static void TargetRefuses(string fault)
    {
        using var directory = new TemporaryDirectory();
        var path = WriteManifest(directory.Path);
        var manifest = QualificationManifestParser.Parse(File.ReadAllText(path));
        var runner = new FakeRunner(manifest) { Fault = fault };
        var driver = new ProcessQualificationDriver(path, runner, new FakeDocker(manifest) { Fault = fault }, new FakeTemporal());
        try { driver.ValidateAsync().GetAwaiter().GetResult(); }
        catch (QualificationException)
        {
            if ((fault is "head-exit" or "status-exit" or "dirty") && runner.Operations.Contains("codex-version"))
                throw new InvalidOperationException("failed source read did not stop validation");
            return;
        }
        throw new InvalidOperationException("target mismatch accepted: " + fault);
    }

    private static void ConnectedReceiptRead(string fault)
    {
        using var directory = new TemporaryDirectory();
        var path = WriteManifest(directory.Path);
        var tokenPath = Path.Combine(directory.Path,"receipt-token");
        File.WriteAllText(tokenPath,"synthetic-token-sentinel");
        File.SetUnixFileMode(tokenPath,UnixFileMode.UserRead|UnixFileMode.UserWrite);
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        root["configFiles"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject {
            ["path"]="receipt-token", ["sha256"]=Sha256(tokenPath)
        });
        if (fault!="operation") root["allowedOperations"]!.AsArray().Add("receipt-read");
        File.WriteAllText(path,root.ToJsonString());
        var manifest = QualificationManifestParser.Parse(File.ReadAllText(path));
        var item = new WorkflowOutcomeItemRef(manifest.Runtime.ScenarioItemId,manifest.Runtime.ScenarioVersion);
        var receipt = new WorkflowOutcomeReceipt(manifest.Runtime.ScenarioBatchId,manifest.Runtime.ScenarioGeneration,
            manifest.Runtime.ScenarioNonce,WorkflowOutcomeHashes.ItemVersionsHash([item]),[item],[item],[],
            new string('a',64),DateTimeOffset.Parse("2026-01-01T00:00:00Z",CultureInfo.InvariantCulture),null);
        var calls = 0;
        using var http = new HttpClient(new StaticHandler(request => {
            calls++;
            if (request.Method != HttpMethod.Get) throw new InvalidOperationException("collector mutated API");
            if (fault == "post-read-drift") File.AppendAllText(Path.Combine(directory.Path,"agent.json"),"changed");
            return new HttpResponseMessage(HttpStatusCode.OK) {
                Content=new StringContent(JsonSerializer.Serialize(receipt,new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    Encoding.UTF8,"application/json")
            };
        }));
        var factoryCalls = 0;
        var runner = new FakeRunner(manifest);
        var driver = new ProcessQualificationDriver(path,runner,new FakeDocker(manifest),new FakeTemporal(),
            receiptFactory:(origin,token) => {
                factoryCalls++;
                return fault=="foreign-reader" ? new ForeignReceiptReader() : new ProcessReceiptClient(origin,token,http);
            });
        if (fault=="token-hash") File.AppendAllText(tokenPath,"changed");
        if (fault=="token-mode") File.SetUnixFileMode(tokenPath,UnixFileMode.UserRead|UnixFileMode.GroupRead);
        try
        {
            var readback = driver.ReadReceiptAsync(fault=="approval" ? "foreign" : manifest.Target.ApprovalRef,
                fault=="unlisted-token" ? "unlisted" : "receipt-token").GetAwaiter().GetResult();
            if (fault!="valid" || !readback.Receipt.Delivered || readback.Target.NestedContainerIds.Count!=3)
                throw new InvalidOperationException("unbound receipt observation returned");
        }
        catch (QualificationException) { if (fault=="valid") throw; }
        if (calls != (fault is "valid" or "post-read-drift" ? 1 : 0))
            throw new InvalidOperationException("receipt request escaped preflight or retried");
        if (fault is "approval" or "operation" or "token-hash" or "token-mode" or "unlisted-token" && factoryCalls != 0)
            throw new InvalidOperationException("token/client construction escaped guard");
    }

    private sealed class ForeignReceiptReader : IProcessReceiptReader
    {
        public string Origin => "https://foreign.example";
        public Task<ProcessReceiptObservation> ReadAsync(RuntimeTarget target,CancellationToken ct=default) =>
            throw new InvalidOperationException("foreign receipt reader invoked");
        public void Dispose() { }
    }

    private static void ApiReceiptCollector(string fault)
    {
        using var directory = new TemporaryDirectory();
        var path = WriteManifest(directory.Path);
        var manifest = QualificationManifestParser.Parse(File.ReadAllText(path));
        var runtime = manifest.Runtime;
        var item = new WorkflowOutcomeItemRef(runtime.ScenarioItemId, runtime.ScenarioVersion);
        var at = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture);
        var receipt = new WorkflowOutcomeReceipt(runtime.ScenarioBatchId, runtime.ScenarioGeneration,
            runtime.ScenarioNonce, WorkflowOutcomeHashes.ItemVersionsHash([item]), [item], [item],
            fault == "acked" ? [item] : [], new string('a',64), at, fault == "acked" ? at : null);
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(receipt,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
        switch (fault)
        {
            case "batch": node["batchId"] = Guid.NewGuid().ToString(); break;
            case "generation": node["generation"] = runtime.ScenarioGeneration + 1; break;
            case "nonce": node["nonce"] = "foreign"; break;
            case "items": node["items"]!.AsArray().Add(node["items"]![0]!.DeepClone()); break;
            case "delivered": node["deliveredVersions"] = new System.Text.Json.Nodes.JsonArray(); break;
            case "acked-item": node["ackedVersions"] = System.Text.Json.Nodes.JsonNode.Parse(
                "[{\"id\":\"11111111-1111-4111-8111-111111111111\",\"version\":1}]"); break;
            case "time": node["deliveredAt"] = null; break;
            case "hash": node["itemVersionsHash"] = new string('0',64); break;
            case "ack-time": node["acknowledgedAt"] = at; break;
            case "bad-receipt-hash": node["receiptHash"] = "invalid"; break;
            case "unknown": node["foreign"] = "synthetic"; break;
            case "missing": node.AsObject().Remove("deliveredAt"); break;
        }
        var calls = 0;
        const string token = "synthetic-token-sentinel";
        using var http = new HttpClient(new StaticHandler(request => {
            calls++;
            if (request.Method != HttpMethod.Get
                || request.RequestUri!.PathAndQuery != "/api/workflow-outcomes/batches/" + runtime.ScenarioBatchId.ToString("D")
                    + "/receipt?nonce=" + Uri.EscapeDataString(runtime.ScenarioNonce)
                || request.Headers.Authorization?.ToString() != "Bearer " + token)
                throw new InvalidOperationException("receipt method/scope/header changed");
            if (fault == "transport") throw new HttpRequestException("synthetic-token-sentinel");
            var body = fault == "malformed" ? "not JSON" : node.ToJsonString();
            if (fault == "duplicate") body = body.Insert(1, "\"batchId\":\""+runtime.ScenarioBatchId.ToString("D")+"\",");
            if (fault == "oversize") body = new string('x',131073);
            var status = int.TryParse(fault,out var code) ? code : fault == "redirect" ? 302 : 200;
            return new HttpResponseMessage((HttpStatusCode)status) {
                Content = new StringContent(body,Encoding.UTF8,fault=="content-type" ? "text/plain" : "application/json")
            };
        })) { BaseAddress = new Uri(runtime.OrchestratorBaseUrl) };
        using var client = new ProcessReceiptClient(runtime.OrchestratorBaseUrl, token, http);
        using var cancel = new CancellationTokenSource();
        if (fault == "cancel") cancel.Cancel();
        try
        {
            var observed = client.ReadAsync(runtime,cancel.Token).GetAwaiter().GetResult();
            if (fault is not ("valid" or "acked")) throw new InvalidOperationException("invalid receipt accepted");
            if (observed.BatchId != runtime.ScenarioBatchId || observed.Acknowledged != (fault == "acked"))
                throw new InvalidOperationException("readback identity/state lost");
            if (JsonSerializer.Serialize(observed).Contains(token,StringComparison.Ordinal))
                throw new InvalidOperationException("token leaked");
        }
        catch (Exception e) when (e is QualificationException or OperationCanceledException)
        {
            if (fault is "valid" or "acked") throw;
            if (e.Message.Contains(token,StringComparison.Ordinal)) throw new InvalidOperationException("token leaked in error");
        }
        if (calls != (fault=="cancel" ? 0 : 1)) throw new InvalidOperationException("receipt read retried or ignored cancellation");
    }

    private static void NestedTargetRefuses(string fault)
    {
        using var directory = new TemporaryDirectory();
        var path = WriteManifest(directory.Path);
        var manifest = QualificationManifestParser.Parse(File.ReadAllText(path));
        var runner = new FakeRunner(manifest);
        var driver = new ProcessQualificationDriver(path, runner, new FakeDocker(manifest) { Fault = fault }, new FakeTemporal());
        try { driver.ValidateAsync().GetAwaiter().GetResult(); }
        catch (QualificationException)
        {
            if (runner.Operations.Contains("codex-version"))
                throw new InvalidOperationException("nested mismatch reached native CLI");
            return;
        }
        throw new InvalidOperationException("foreign nested container configuration was accepted");
    }

    private static void NestedRegistryRefusesBeforeIo(string fault)
    {
        using var directory = new TemporaryDirectory();
        var path = WriteManifest(directory.Path);
        var file = Path.Combine(directory.Path,"runtime-bindings.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
        switch (fault)
        {
            case "unknown": node["OwnerNonce"] = "ambiguous-alias"; break;
            case "missing-role": node["containers"]!.AsObject().Remove("agent"); break;
            case "duplicate-id": node["containers"]!["agent"]!["id"] = RoleId("orchestrator"); break;
            case "nonce": node["ownerNonce"] = "foreign"; break;
            case "hash": node["socketTarget"] = "/run/changed.sock"; break;
        }
        File.WriteAllText(file,node.ToJsonString());
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        if (fault != "hash")
            foreach (var pin in root["configFiles"]!.AsArray())
                if (pin!["path"]!.GetValue<string>() == "runtime-bindings.json") pin["sha256"] = Sha256(file);
        File.WriteAllText(path,root.ToJsonString());
        if (fault == "symlink")
        {
            var copy = Path.Combine(directory.Path,"other-owned.json");
            File.Move(file,copy);
            File.CreateSymbolicLink(file,copy);
        }
        var driver = new ProcessQualificationDriver(path,new RefuseRunner(),new RefuseDocker(),new RefuseTemporal());
        try { driver.ValidateAsync().GetAwaiter().GetResult(); }
        catch (Exception e) when (e is QualificationException or JsonException) { return; }
        throw new InvalidOperationException("invalid nested registry reached a process or endpoint");
    }

    private static void NestedOwnedBind(string fault)
    {
        using var directory = new TemporaryDirectory();
        using var other = new TemporaryDirectory();
        var path = WriteManifest(directory.Path);
        var manifest = QualificationManifestParser.Parse(File.ReadAllText(path));
        var source = Path.Combine(directory.Path,"agent.json");
        if (fault == "unhashed")
        {
            source = Path.Combine(directory.Path,"unhashed.json");
            File.WriteAllText(source,"owned synthetic data");
        }
        if (fault == "symlink")
        {
            source = Path.Combine(directory.Path,"linked-directory");
            Directory.CreateSymbolicLink(source,other.Path);
        }
        var docker = new FakeDocker(manifest) { ExtraMountSource=source };
        var file = Path.Combine(directory.Path,"runtime-bindings.json");
        var bindings = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
        foreach (var role in QualificationManifestParser.RequiredImageRoles)
            bindings["containers"]![role]!["specSha256"] = docker.GetContainerAsync(RoleId(role)).GetAwaiter().GetResult().SpecSha256;
        File.WriteAllText(file,bindings.ToJsonString());
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        foreach (var pin in root["configFiles"]!.AsArray())
            if (pin!["path"]!.GetValue<string>() == "runtime-bindings.json") pin["sha256"] = Sha256(file);
        File.WriteAllText(path,root.ToJsonString());
        var driver = new ProcessQualificationDriver(path,new FakeRunner(manifest),docker,new FakeTemporal());
        try
        {
            driver.ValidateAsync().GetAwaiter().GetResult();
            if (fault != "pinned") throw new InvalidOperationException("unowned bind was accepted");
        }
        catch (QualificationException) when (fault != "pinned") { }
    }

    private static void MissingNestedRegistryRefuses()
    {
        using var directory = new TemporaryDirectory();
        var path = WriteManifest(directory.Path);
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        var config = node["configFiles"]!.AsArray();
        config.Remove(config.Single(f => f!["path"]!.GetValue<string>() == "runtime-bindings.json"));
        File.WriteAllText(path, node.ToJsonString());
        var driver = new ProcessQualificationDriver(path, new RefuseRunner(), new RefuseDocker(), new RefuseTemporal());
        try { driver.ValidateAsync().GetAwaiter().GetResult(); }
        catch (QualificationException) { return; }
        throw new InvalidOperationException("missing nested registry reached a target");
    }

    private static void NestedSelfConsistentRefuses(string fault)
    {
        using var directory = new TemporaryDirectory();
        var path = WriteManifest(directory.Path);
        var manifest = QualificationManifestParser.Parse(File.ReadAllText(path));
        var changed = new FakeDocker(manifest) { Fault = fault };
        var bindings = Path.Combine(directory.Path, "runtime-bindings.json");
        var data = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(bindings))!;
        foreach (var role in QualificationManifestParser.RequiredImageRoles)
            data["containers"]![role]!["specSha256"] = changed.GetContainerAsync(RoleId(role)).GetAwaiter().GetResult().SpecSha256;
        File.WriteAllText(bindings, data.ToJsonString());
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        foreach (var pin in root["configFiles"]!.AsArray())
            if (pin!["path"]!.GetValue<string>() == "runtime-bindings.json") pin["sha256"] = Sha256(bindings);
        File.WriteAllText(path, root.ToJsonString());
        var runner = new FakeRunner(manifest);
        var driver = new ProcessQualificationDriver(path, runner, changed, new FakeTemporal());
        try { driver.ValidateAsync().GetAwaiter().GetResult(); }
        catch (QualificationException)
        {
            if (runner.Operations.Contains("codex-version")) throw new InvalidOperationException("unsafe nested spec reached native CLI");
            return;
        }
        throw new InvalidOperationException("unsafe self-consistent nested configuration passed");
    }

    private static void NestedHttpInspect(string fault)
    {
        var manifest = Manifest();
        var doc = ContainerDocument(manifest, "agent");
        var text = doc.GetRawText();
        if (fault == "secret")
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(text)!;
            node["Config"]!["Env"]!.AsArray().Add("IGNORED_PASSWORD=owned-synthetic-secret-sentinel");
            text = node.ToJsonString();
        }
        if (fault == "duplicate") text = text.Replace("\"Id\":", "\"Id\":\"" + new string('4',64) + "\",\"Id\":", StringComparison.Ordinal);
        if (fault == "oversize") text = new string(' ',4 * 1024 * 1024 + 1);
        if (fault == "reorder")
        {
            var entries = doc.EnumerateObject().Reverse().Select(p => new KeyValuePair<string, JsonElement>(p.Name,p.Value));
            text = JsonSerializer.Serialize(entries.ToDictionary(p => p.Key,p => p.Value));
        }
        var reads = 0;
        using var handler = new StaticHandler(request =>
        {
            reads++;
            if (request.Method != HttpMethod.Get || request.RequestUri!.AbsolutePath != "/v1.43/containers/" + RoleId("agent") + "/json")
                throw new InvalidOperationException("inspect was not exact-ID/read-only");
            return new HttpResponseMessage(fault == "404" ? HttpStatusCode.NotFound : HttpStatusCode.OK)
                { Content = new StringContent(text) };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        using var client = new DockerEngineClient(http,"v1.43");
        try
        {
            var observed = client.GetContainerAsync(fault == "name" ? "agent1" : RoleId("agent")).GetAwaiter().GetResult();
            if (fault is "name" or "404" or "duplicate" or "oversize") throw new InvalidOperationException("invalid inspect was accepted");
            if (fault == "secret" && JsonSerializer.Serialize(observed).Contains("owned-synthetic-secret",StringComparison.Ordinal))
                throw new InvalidOperationException("protected environment escaped the observation");
            if (fault == "reorder" && observed.SpecSha256 != ContainerBindingObservation.Parse(doc).SpecSha256)
                throw new InvalidOperationException("JSON property order changed the immutable spec digest");
        }
        catch (QualificationException e) when (fault is "name" or "404" or "duplicate" or "oversize")
        {
            if (fault == "name" && reads != 0) throw new InvalidOperationException("name lookup reached Docker");
            if (fault == "oversize" && !e.Message.Contains("size limit",StringComparison.Ordinal))
                throw new InvalidOperationException("oversized inspect did not hit its byte bound");
            return;
        }
        if (reads != 1) throw new InvalidOperationException("inspect was retried");
    }

    private static void ChangedFileRefusesBeforeIo() => CustodyRefuses(false);
    private static void SymlinkRefusesBeforeIo() => CustodyRefuses(true);
    private static void CustodyRefuses(bool symlink)
    {
        using var directory = new TemporaryDirectory();
        var path = WriteManifest(directory.Path);
        var file = System.IO.Path.Combine(directory.Path, "agent.json");
        if (symlink)
        {
            File.Delete(file);
            File.CreateSymbolicLink(file, System.IO.Path.Combine(directory.Path, "orchestrator.json"));
        }
        else File.WriteAllText(file, "changed");
        var driver = new ProcessQualificationDriver(path, new RefuseRunner(), new RefuseDocker(), new RefuseTemporal());
        try { driver.ValidateAsync().GetAwaiter().GetResult(); }
        catch (QualificationException) { return; }
        throw new InvalidOperationException("changed/symlinked custody accepted");
    }

    private static void CampaignRefusesUncollectedObservations()
    {
        using var directory = new TemporaryDirectory();
        var path = WriteManifest(directory.Path);
        var manifest = QualificationManifestParser.Parse(File.ReadAllText(path));
        var observations = System.IO.Path.Combine(directory.Path, "forged.json");
        File.WriteAllText(observations, "{}");
        var driver = new ProcessQualificationDriver(path, new RefuseRunner(), new RefuseDocker(), new RefuseTemporal());
        try { driver.RunAsync(manifest.Target.ApprovalRef, observations).GetAwaiter().GetResult(); }
        catch (QualificationBlockedException) { return; }
        throw new InvalidOperationException("uncollected caller assertions became runtime qualification");
    }

    private static void MutationsRefuseIncompleteRegistry()
    {
        var manifest = Manifest();
        var planner = new CommandPlanner("/owned-fixture");
        foreach (var operation in new[] { "compose-up", "compose-down", "mysql-seed", "mysql-verify" })
        {
            try { planner.Build(manifest, operation); }
            catch (QualificationBlockedException) { continue; }
            throw new InvalidOperationException("unsafe mutation was planned: " + operation);
        }
    }

    private static void ScenarioRejectsForeignIdentity()
    {
        var manifest = Manifest();
        var observation = Observation("failure-native-turn") with
        { BatchId = Guid.NewGuid().ToString("D"), Nonce = "foreign" };
        var result = ScenarioAssertionRunner.Evaluate(manifest, [observation])
            .Single(item => item.Scenario == observation.Scenario);
        if (result.Status != ScenarioAssertionRunner.Fail || !result.Failures.Contains("batch_id_mismatch")
            || !result.Failures.Contains("nonce_mismatch"))
            throw new InvalidOperationException("foreign observation passed identity matching");
    }

    private static void FixtureRequiresShellResult()
    {
        var nonce = "qualificationsyntheticnonce";
        var request = Request(Prompt() + nonce);
        if (!WorkflowOutcomeInferenceFixture.TryCreateResponse(request, FixtureTarget(), InferenceFixtureMode.ShellProbe,
            nonce, out var sse, out var error) || !sse.Contains("local_shell_call", StringComparison.Ordinal))
            throw new InvalidOperationException("nonce in untrusted text bypassed shell request: " + error);
    }

    private static void FixtureRejectsMalformedFields()
    {
        foreach (var request in new[] { "{\"model\":1,\"stream\":true}", "{\"model\":\"qualification-fixture\",\"stream\":1}" })
            if (WorkflowOutcomeInferenceFixture.TryCreateResponse(request, FixtureTarget(), InferenceFixtureMode.Turn,
                    "qualificationsyntheticnonce", out _, out _))
                throw new InvalidOperationException("malformed typed field was accepted");
    }

    private static void HttpRejectsAmbiguousFraming()
    {
        foreach (var framing in new[] { "Content-Length: 0\r\nContent-Length: 1", "Content-Length: 0\r\nTransfer-Encoding: chunked" })
        {
            using var input = new MemoryStream(Encoding.ASCII.GetBytes(
                "POST /v1/responses HTTP/1.1\r\nContent-Type: application/json\r\n" + framing + "\r\n\r\n"));
            using var output = new MemoryStream();
            try { InferenceFixtureHttpHandler.HandleAsync(input, output, FixtureTarget(), InferenceFixtureMode.Turn,
                "qualificationsyntheticnonce", CancellationToken.None).GetAwaiter().GetResult(); }
            catch (IOException) { continue; }
            throw new InvalidOperationException("ambiguous HTTP framing accepted");
        }
    }

    private static void HeaderReadIsCancelable()
    {
        using var input = new NeverReadStream();
        using var output = new MemoryStream();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var task = InferenceFixtureHttpHandler.HandleAsync(input, output, FixtureTarget(), InferenceFixtureMode.Turn,
            "qualificationsyntheticnonce", cancellation.Token);
        try { task.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("pending header read was not cancelled");
    }

    private static void ProcessExplicitEnvironment()
    {
        using var directory = new TemporaryDirectory();
        const string key = "QUALIFICATION_SYNTHETIC_AMBIENT";
        var previous = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, "must-not-reach-child");
        try
        {
            var command = new PlannedCommand("synthetic-environment", "/bin/sh",
                ["-c", "test -z \"${QUALIFICATION_SYNTHETIC_AMBIENT+x}\" || exit 9; printf explicit; printf diagnostic >&2"],
                new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin", ["HOME"] = directory.Path, ["TMPDIR"] = directory.Path },
                directory.Path, TimeSpan.FromSeconds(2));
            var result = new ProcessCommandRunner().RunAsync(command).WaitAsync(TimeSpan.FromSeconds(4)).GetAwaiter().GetResult();
            if (result.ExitCode != 0 || result.TimedOut || result.Stdout != "explicit" || result.Stderr != "diagnostic"
                || result.StdoutSha256 != Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("explicit"))))
                throw new InvalidOperationException("explicit environment/process capture failed");
        }
        finally { Environment.SetEnvironmentVariable(key, previous); }
    }

    private static void ProcessDeadlineReapsChild() => RunOwnedChild(cancel: false);
    private static void ProcessCancellationReapsChild() => RunOwnedChild(cancel: true);
    private static void RunOwnedChild(bool cancel)
    {
        using var directory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        if (cancel) cancellation.CancelAfter(TimeSpan.FromMilliseconds(150));
        var command = new PlannedCommand("synthetic-child", "/bin/sh", ["-c", "echo $$ > child.pid; exec sleep 10"],
            new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin", ["HOME"] = directory.Path, ["TMPDIR"] = directory.Path },
            directory.Path, TimeSpan.FromMilliseconds(200));
        ProcessCommandResult result;
        try { result = new ProcessCommandRunner().RunAsync(command, cancellation.Token)
            .WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            try { using var observed = System.Diagnostics.Process.GetProcessById(result.ProcessId);
                if (!observed.HasExited) throw new InvalidOperationException("runner left its child alive"); }
            catch (ArgumentException) { }
        }
        finally
        {
            var pidFile = System.IO.Path.Combine(directory.Path, "child.pid");
            if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid))
                try { using var owned = System.Diagnostics.Process.GetProcessById(pid);
                    if (!owned.HasExited) { owned.Kill(entireProcessTree: true); owned.WaitForExit(1000); } }
                catch (ArgumentException) { }
        }
        if (!result.TimedOut || result.ExitCode != 124)
            throw new InvalidOperationException("pending child was not reported timed out/cancelled");
        try { using var child = System.Diagnostics.Process.GetProcessById(result.ProcessId);
            if (!child.HasExited) throw new InvalidOperationException("owned child survived cleanup"); }
        catch (ArgumentException) { }
    }

    private static void ProcessLargeOutputDoesNotDeadlock()
    {
        using var directory = new TemporaryDirectory();
        var command = new PlannedCommand("synthetic-output", "/bin/sh",
            ["-c", "head -c 2097152 /dev/zero; head -c 2097152 /dev/zero >&2"],
            new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin", ["HOME"] = directory.Path, ["TMPDIR"] = directory.Path },
            directory.Path, TimeSpan.FromSeconds(2));
        try { new ProcessCommandRunner().RunAsync(command).WaitAsync(TimeSpan.FromSeconds(4)).GetAwaiter().GetResult(); }
        catch (QualificationException exception) when (exception.Message.Contains("output exceeded", StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("oversize output was not refused");
    }

    private sealed class RefuseRunner : IProcessCommandRunner
    { public Task<ProcessCommandResult> RunAsync(PlannedCommand command, CancellationToken ct = default) => throw new InvalidOperationException("unexpected process"); }
    private sealed class RefuseDocker : IDockerEngineReader
    {
        public Task<DockerEngineInfo> GetEngineInfoAsync(CancellationToken ct = default) => throw new InvalidOperationException("unexpected engine read");
        public Task<DockerImageInfo> GetImageAsync(string reference, CancellationToken ct = default) => throw new InvalidOperationException("unexpected image read");
        public Task<ContainerBindingObservation> GetContainerAsync(string id, CancellationToken ct = default) => throw new InvalidOperationException("unexpected container read");
    }
    private sealed class RefuseTemporal : ITemporalMetadataReader
    { public Task<TemporalSystemInfo> GetSystemInfoAsync(CancellationToken ct = default) => throw new InvalidOperationException("unexpected server read"); }
    private sealed class NeverReadStream : MemoryStream
    {
        public override int ReadByte() => throw new InvalidOperationException("synchronous read is unbounded");
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return 0; }
    }

    private static void ManifestParsesExplicitTarget()
    {
        using var directory = new TemporaryDirectory();
        var manifest = WriteManifest(directory.Path);
        using var stream = File.OpenRead(manifest);
        var parsed = QualificationManifestParser.Parse(stream);
        if (!string.Equals(parsed.Temporal.Namespace, parsed.Temporal.TaskQueue, StringComparison.Ordinal))
            throw new InvalidOperationException("namespace and task queue were not bound equally");
        if (!parsed.AllowedOperations.Contains("compose-up"))
            throw new InvalidOperationException("required operation was not retained");
        if (parsed.AuthorizedRuntimeOracles.Count != 0)
            throw new InvalidOperationException("runtime oracle was authorized by a source fixture");
    }

    private static void CommandPlannerBindsArgvAndEnvironment()
    {
        using var directory = new TemporaryDirectory();
        var manifestPath = WriteManifest(directory.Path);
        using var stream = File.OpenRead(manifestPath);
        var manifest = QualificationManifestParser.Parse(stream);
        var planner = new CommandPlanner(directory.Path);
        var codex = planner.Build(manifest, "codex-version");
        if (!string.Equals(Path.GetFileName(codex.FileName), "codex", StringComparison.Ordinal)
            || codex.Arguments.Count != 1 || codex.Arguments[0] != "--version")
            throw new InvalidOperationException("codex argv was not exact");
        if (!string.Equals(codex.Environment["CODEX_OSS_BASE_URL"], manifest.Fixture.Endpoint, StringComparison.Ordinal)
            || codex.Environment.ContainsKey("QUALIFICATION_MYSQL_CONNECTION") || codex.Environment.ContainsKey("DOCKER_HOST"))
            throw new InvalidOperationException("codex child environment was not minimal and explicit");


    }

    private static void CommandPlannerRefusesMissingDatabaseEnvironment()
    {
        using var directory = new TemporaryDirectory();
        var manifestPath = WriteManifest(directory.Path);
        using var stream = File.OpenRead(manifestPath);
        var manifest = QualificationManifestParser.Parse(stream);
        var planner = new CommandPlanner(directory.Path);
        try
        {
            planner.Build(manifest, "mysql-seed");
        }
        catch (QualificationBlockedException)
        {
            return;
        }
        throw new InvalidOperationException("mysql operation did not refuse a missing explicit connection");
    }

    private static void DriverValidatesLinkedTarget()
    {
        using var directory = new TemporaryDirectory();
        var manifestPath = WriteManifest(directory.Path);
        using var stream = File.OpenRead(manifestPath);
        var manifest = QualificationManifestParser.Parse(stream);
        var driver = new ProcessQualificationDriver(
            manifestPath,
            new FakeRunner(manifest),
            new FakeDocker(manifest),
            new FakeTemporal());
        var plan = driver.ValidateAsync().GetAwaiter().GetResult();
        if (!string.Equals(plan.SourceCommit, manifest.Target.SourceCommit, StringComparison.Ordinal)
            || !string.Equals(plan.SourceTree, manifest.Target.SourceTree, StringComparison.Ordinal))
            throw new InvalidOperationException("source linkage failed");
        if (plan.ImageIds.Count != 3) throw new InvalidOperationException("image linkage failed");
        if (plan.NestedContainerIds.Count != 3) throw new InvalidOperationException("nested container linkage failed");
        if (string.IsNullOrWhiteSpace(plan.TemporalServerVersion))
            throw new InvalidOperationException("Temporal metadata linkage failed");
    }

    private static void InferenceFixtureReturnsStructuredTurn()
    {
        var target = FixtureTarget();
        var prompt = Prompt();
        var request = Request(prompt);
        if (!WorkflowOutcomeInferenceFixture.TryCreateResponse(
                request, target, InferenceFixtureMode.Turn, "qualificationsyntheticnonce", out var sse, out var error))
            throw new InvalidOperationException("fixture refused a valid turn: " + error);
        if (!sse.Contains("event: response.completed", StringComparison.Ordinal)
            || sse.Contains("local_shell_call", StringComparison.Ordinal))
            throw new InvalidOperationException("fixture did not return a text completion");
        var facts = Facts();
        if (Completion(sse).GetProperty("response").GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString()
            != WorkflowOutcomeInferenceFixture.BuildTurnOutput(facts))
            throw new InvalidOperationException("fixture did not return the linked structured output");
    }

    private static void InferenceFixtureShellProbeBindsNonce()
    {
        var target = FixtureTarget();
        var request = Request(Prompt());
        if (!WorkflowOutcomeInferenceFixture.TryCreateResponse(
                request, target, InferenceFixtureMode.ShellProbe, "qualificationsyntheticnonce", out var sse, out var error))
            throw new InvalidOperationException("fixture refused the shell probe: " + error);
        var action = Completion(sse).GetProperty("response").GetProperty("output")[0].GetProperty("action");
        if (!action.GetProperty("command").EnumerateArray().Select(item => item.GetString()).SequenceEqual(target.ShellArgv)
            || action.GetProperty("env").GetProperty("PROCESS_QUALIFICATION_NONCE").GetString() != "qualificationsyntheticnonce")
            throw new InvalidOperationException("fixture did not bind the nonce to exact shell argv");
    }

    private static void FixtureHttpHandlerServesOnlyResponses()
    {
        var body = Encoding.UTF8.GetBytes(Request(Prompt()));
        var request = Encoding.ASCII.GetBytes(
            "POST /v1/responses HTTP/1.1\r\nContent-Type: application/json\r\nContent-Length: "
            + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n").Concat(body).ToArray();
        using var input = new MemoryStream(request);
        using var output = new MemoryStream();
        InferenceFixtureHttpHandler.HandleAsync(
            input, output, FixtureTarget(), InferenceFixtureMode.Turn, "qualificationsyntheticnonce", CancellationToken.None).GetAwaiter().GetResult();
        var response = Encoding.UTF8.GetString(output.ToArray());
        if (!response.StartsWith("HTTP/1.1 200 OK\r\n", StringComparison.Ordinal)
            || !response.Contains("Content-Type: text/event-stream", StringComparison.Ordinal)
            || !response.Contains("event: response.completed", StringComparison.Ordinal))
            throw new InvalidOperationException("HTTP fixture handler did not serve the Responses route");
    }

    private static void TemporalClientUsesReadOnlyGetSystemInfo()
    {
        var payload = new byte[] { 0x0a, 5, (byte)'1', (byte)'.', (byte)'2', (byte)'.', (byte)'3' };
        var frame = new byte[] { 0, 0, 0, 0, (byte)payload.Length }.Concat(payload).ToArray();
        using var handler = new StaticHandler(request =>
        {
            if (request.Method != HttpMethod.Post
                || request.RequestUri?.AbsolutePath != "/temporal.api.workflowservice.v1.WorkflowService/GetSystemInfo"
                || request.Content?.Headers.ContentType?.MediaType != "application/grpc")
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(frame)
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://temporal.example:7233") };
        using var client = new TemporalMetadataClient(http);
        var info = client.GetSystemInfoAsync().GetAwaiter().GetResult();
        if (info.ServerVersion != "1.2.3") throw new InvalidOperationException("Temporal metadata was not parsed");
    }

    private static void DockerClientRequiresExplicitEngineAndImage()
    {
        using var handler = new StaticHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1.43/info")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"ID\":\"engine-qualification\",\"ServerVersion\":\"1.2\"}")
                };
            if (request.RequestUri?.AbsolutePath.StartsWith("/v1.43/images/", StringComparison.Ordinal) == true)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"Id\":\"sha256:" + new string('c', 64) + "\"}")
                };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        using var client = new DockerEngineClient(http, "v1.43");
        var engine = client.GetEngineInfoAsync().GetAwaiter().GetResult();
        var image = client.GetImageAsync("registry.example/agent@sha256:" + new string('c', 64)).GetAwaiter().GetResult();
        if (engine.EngineId != "engine-qualification" || image.ImageId != "sha256:" + new string('c', 64))
            throw new InvalidOperationException("Docker engine or image binding failed");
    }

    private static void ScenarioAssertionsAcceptBaseOracles()
    {
        var manifest = Manifest();
        var observations = QualificationManifestParser.BaseScenarios.Select(Observation).ToList();
        var results = ScenarioAssertionRunner.Evaluate(manifest, observations);
        if (results.Where(result => QualificationManifestParser.BaseScenarios.Contains(result.Scenario))
            .Any(result => result.Status != ScenarioAssertionRunner.Pass))
            throw new InvalidOperationException("a valid base observation did not pass");
    }

    private static void ScenarioAssertionsFailSecondTurn()
    {
        var manifest = Manifest();
        var observations = QualificationManifestParser.BaseScenarios
            .Select(Observation)
            .Select(item => item.Scenario == "failure-native-turn"
                ? item with { SecondTurn = true }
                : item)
            .ToList();
        var result = ScenarioAssertionRunner.Evaluate(manifest, observations)
            .Single(item => item.Scenario == "failure-native-turn");
        if (result.Status != ScenarioAssertionRunner.Fail || !result.Failures.Contains("second_turn_not_refused"))
            throw new InvalidOperationException("second-turn regression was not failed");
    }

    private static void RuntimeOraclesStayUnrun()
    {
        var manifest = Manifest();
        var observations = QualificationManifestParser.BaseScenarios.Select(Observation).ToList();
        var results = ScenarioAssertionRunner.Evaluate(manifest, observations);
        foreach (var oracle in QualificationManifestParser.RuntimeOracleScenarios)
        {
            var result = results.Single(item => item.Scenario == oracle);
            if (result.Status != ScenarioAssertionRunner.Unrun
                || result.Reason != "separate_runtime_authorization_required")
                throw new InvalidOperationException("runtime oracle was not retained as UNRUN");
        }
    }

    private static ProcessQualificationManifest Manifest()
    {
        using var directory = new TemporaryDirectory();
        var path = WriteManifest(directory.Path);
        using var stream = File.OpenRead(path);
        return QualificationManifestParser.Parse(stream);
    }

    private static ProcessScenarioObservation Observation(string scenario) => scenario switch
    {
        "success-zero-turn" => new ProcessScenarioObservation(
            scenario, 0, 0, 0, 0, 0, 0, 0, false, false, false, false, false, false, false,
            false, false, false, false, null, null, null, null, null, null, null),
        "denials" => new ProcessScenarioObservation(
            scenario, 0, 0, 0, 0, 0, 0, 0, true, true, true, false, false, false, false,
            false, false, false, false, null, null, null, null, null, null, null),
        _ => new ProcessScenarioObservation(
            scenario, 1, 1, 1, 1, 1, 0, scenario == "late-confirm-before-ack" ? 1 : 0,
            false, false, false, scenario == "crash-after-admission", false,
            scenario == "lost-post-reconciliation", scenario == "late-confirm-before-ack",
            false, false, false, false,
            "11111111-1111-4111-8111-111111111111", "22222222-2222-4222-8222-222222222222",
            1, 1, new string('d', 64), "qualificationsyntheticnonce", "process ancestry")
    };

    private static InferenceFixtureTarget FixtureTarget() => new(
        "0.159.2", "qualification-fixture", "openai-responses-v1", "http://localhost:49151/v1/",
        ["/bin/sh", "-c", "printf '%s' \"$PROCESS_QUALIFICATION_NONCE\""],
        ["PROCESS_QUALIFICATION_NONCE"], 5);

    private static WorkflowOutcomePromptFacts Facts() => new(
        Guid.Parse("11111111-1111-4111-8111-111111111111"),
        1,
        [new WorkflowOutcomeFixtureItem(
            Guid.Parse("22222222-2222-4222-8222-222222222222"), 1,
            "namespace-a", "workflow-a", "run-a")]);

    private static string Prompt()
    {
        var facts = JsonSerializer.Serialize(new
        {
            batchId = Facts().BatchId,
            generation = Facts().Generation,
            items = Facts().Items.Select(item => new
            {
                id = item.Id, version = item.Version, @namespace = item.Namespace,
                workflowId = item.WorkflowId, runId = item.RunId
            })
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return WorkflowOutcomeInferenceFixture.PromptPrefix + facts + WorkflowOutcomeInferenceFixture.PromptSuffix;
    }

    private static string Request(string prompt) => JsonSerializer.Serialize(new
    {
        model = "qualification-fixture",
        input = new[]
        {
            new
            {
                type = "message",
                role = "user",
                content = new[] { new { type = "input_text", text = prompt } }
            }
        },
        stream = true,
        prompt_cache_key = "019a0000-0000-7000-8000-000000000001"
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static string WriteManifest(string directory)
    {
        Directory.CreateDirectory(directory);
        var commit = new string('a', 40);
        var tree = new string('b', 40);
        var imageHash = new string('c', 64);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { "agent.json", "orchestrator.json", "temporal-bridge.json", "config.json" })
        {
            var configPath = Path.Combine(directory, name);
            File.WriteAllText(configPath, "{}\n");
            files[name] = Sha256(configPath);
        }
        File.WriteAllText(Path.Combine(directory, "config.json"), "{\"currentContext\":\"qualification-context\"}");
        files["config.json"] = Sha256(Path.Combine(directory, "config.json"));
        var contextHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("qualification-context")));
        var metaRelative = Path.Combine("contexts", "meta", contextHash, "meta.json");
        var metaPath = Path.Combine(directory, metaRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(metaPath)!);
        File.WriteAllText(metaPath, "{\"Name\":\"qualification-context\",\"Endpoints\":{\"docker\":{\"Host\":\"unix:///run/example/docker.sock\",\"SkipTLSVerify\":false}}}");
        files[metaRelative] = Sha256(metaPath);
        var policies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { "ownership.json", "classifiers.json", "recovery-approvals.json" })
        {
            var policyPath = Path.Combine(directory, name);
            File.WriteAllText(policyPath, "{}\n");
            policies[name] = Sha256(policyPath);
        }
        var seed = Path.Combine(directory, "seed.sql");
        File.WriteAllText(seed, "SELECT 1;\n");
        foreach (var name in new[] { "runtime", "source", "home", "cache", "tmp", "tools" })
            Directory.CreateDirectory(Path.Combine(directory, name));
        Directory.CreateDirectory(Path.Combine(directory, "source", ".git"));
        File.WriteAllText(Path.Combine(directory, "source", ".git", "config"), "[core]\nrepositoryformatversion = 0\nbare = false\n");
        foreach (var name in new[] { "git", "codex" }) File.WriteAllText(Path.Combine(directory, "tools", name), "owned synthetic executable");

        var manifest = new
        {
            schemaVersion = 1,
            kind = "workflow-outcomes-process-qualification",
            target = new
            {
                registry = "registry.example",
                approvalRef = "https://approval.example/records/qualification",
                sourceRef = "refs/heads/qualification",
                sourceCommit = commit,
                sourceTree = tree,
                images = new
                {
                    agent = new { reference = "registry.example/agent@sha256:" + imageHash, digest = "sha256:" + imageHash, buildCommit = commit },
                    orchestrator = new { reference = "registry.example/orchestrator@sha256:" + imageHash, digest = "sha256:" + imageHash, buildCommit = commit },
                    temporalBridge = new { reference = "registry.example/temporal-bridge@sha256:" + imageHash, digest = "sha256:" + imageHash, buildCommit = commit }
                }
            },
            executables = new Dictionary<string, object>
            {
                ["git"] = new { path = "tools/git", sha256 = Sha256(Path.Combine(directory, "tools/git")) },
                ["codex"] = new { path = "tools/codex", sha256 = Sha256(Path.Combine(directory, "tools/codex")) }
            },
            configFiles = files.Select(pair => new { path = pair.Key, sha256 = pair.Value }).ToArray(),
            policyFiles = policies.Select(pair => new { path = pair.Key, sha256 = pair.Value }).ToArray(),
            docker = new
            {
                endpoint = "unix:///run/example/docker.sock",
                configFile = "config.json",
                context = "qualification-context",
                engineId = "engine-qualification",
                apiVersion = "v1.43",
                serverVersion = "1.2"
            },
            temporal = new
            {
                endpoint = "https://temporal.example:7233",
                @namespace = "namespace-a",
                runId = "run-a",
                taskQueue = "namespace-a",
                tlsMode = "server-auth",
                serverName = "temporal.example",
                identitySha256 = new string('e', 64),
                serverVersion = "1.2.3"
            },
            runtime = new
            {
                sourceDirectory = "source",
                workingDirectory = "runtime",
                pathEnvironment = "/usr/bin:/bin",
                orchestratorBaseUrl = "https://orchestrator.example",
                brokerEndpoint = "amqps://broker.example",
                databaseServer = "qualification-db",
                databaseName = "qualification-db",
                connectionEnv = "QUALIFICATION_MYSQL_CONNECTION",
                seedSql = "seed.sql",
                seedSqlSha256 = Sha256(seed),
                scenarioBatchId = "11111111-1111-4111-8111-111111111111",
                scenarioNonce = "qualificationsyntheticnonce",
                scenarioItemId = "22222222-2222-4222-8222-222222222222",
                scenarioVersion = 1,
                scenarioGeneration = 1,
                scenarioAttempt = 1,
                expectedTurnOutputSha256 = new string('d', 64),
                terminalHash = new string('f', 64)
            },
            fixture = new
            {
                cliVersion = "0.159.2",
                model = "qualification-fixture",
                protocol = "openai-responses-v1",
                endpoint = "http://localhost:49151/v1/",
                shellArgv = new[] { "/bin/sh", "-c", "printf '%s' \"$PROCESS_QUALIFICATION_NONCE\"" },
                shellEnvironment = new[] { "PROCESS_QUALIFICATION_NONCE" },
                shellDeadlineSeconds = 5
            },
            allowedOperations = new[]
            {
                "docker-version", "docker-image-agent", "docker-image-orchestrator",
                "docker-image-temporal-bridge", "compose-up", "compose-down", "mysql-seed",
                "mysql-verify", "codex-version", "git-source", "git-status"
            },
            scenarios = QualificationManifestParser.BaseScenarios.ToArray(),
            authorizedRuntimeOracles = Array.Empty<string>(),
            cleanup = new
            {
                projectName = "qualification-run",
                owned = new[] { "qualification-run", "namespace-a", "qualification-db" },
                verify = true,
                commands = new[] { "compose-down" }
            },
            maxDurationSeconds = 600
        };
        var manifestPath = Path.Combine(directory, "manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));
        var parsed = QualificationManifestParser.Parse(File.ReadAllText(manifestPath));
        var pins = QualificationManifestParser.RequiredImageRoles.ToDictionary(role => role, role =>
            new ContainerBindingPin(RoleId(role), "sha256:" + new string('9',64),
                ContainerBindingObservation.Parse(ContainerDocument(parsed, role)).SpecSha256), StringComparer.Ordinal);
        var bindings = Path.Combine(directory, "runtime-bindings.json");
        File.WriteAllText(bindings, JsonSerializer.Serialize(new {
            schemaVersion=1, ownerNonce=parsed.Runtime.ScenarioNonce,
            socketTarget="/run/qualification/docker.sock", containers=pins
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var updated = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifestPath))!;
        updated["configFiles"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject
            { ["path"]="runtime-bindings.json", ["sha256"]=Sha256(bindings) });
        File.WriteAllText(manifestPath, updated.ToJsonString());
        return manifestPath;
    }

    private static string RoleId(string role) => new(role switch
        { "agent" => '1', "orchestrator" => '2', "temporalBridge" => '3', _ => throw new InvalidOperationException() },64);

    private static JsonElement ContainerDocument(ProcessQualificationManifest manifest, string role)
    {
        const string socket = "/run/qualification/docker.sock";
        var env = new List<string> { "RabbitMq__Host=" + manifest.Runtime.BrokerEndpoint };
        if (role == "agent") env.AddRange(["Agent__Provider=codex", "Agent__Model=ollama/" + manifest.Fixture.Model,
            "Agent__CodexOssBaseUrl=" + manifest.Fixture.Endpoint.TrimEnd('/')]);
        if (role == "orchestrator") env.Add("Docker__SocketPath=" + socket);
        if (role == "temporalBridge") env.AddRange([
            "TemporalBridge__TemporalAddress=" + new Uri(manifest.Temporal.Endpoint).Authority,
            "TemporalBridge__Namespaces__0=" + manifest.Temporal.Namespace,
            "TemporalBridge__OrchestratorUrl=" + manifest.Runtime.OrchestratorBaseUrl]);
        return JsonSerializer.SerializeToElement(new {
            Id=RoleId(role), Image="sha256:" + new string('9',64),
            Config=new { Env=env, Labels=new Dictionary<string,string> { ["org.phleet.qualification.owner"]=manifest.Runtime.ScenarioNonce } },
            HostConfig=new { Privileged=false, NetworkMode="qualification-network", PidMode="", IpcMode="private" },
            State=new { Running=true, Pid=42, StartedAt="2026-01-01T00:00:00Z" },
            Mounts=role=="orchestrator" ? new[]{new {Type="bind",Source=manifest.Docker.Endpoint["unix://".Length..],Destination=socket}} : [],
            NetworkSettings=new { Networks=new Dictionary<string,object>() }
        });
    }

    private static JsonElement Completion(string sse)
    {
        var block = sse.Split("\n\n").Single(item => item.StartsWith("event: response.completed\n", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(block[(block.IndexOf("data: ", StringComparison.Ordinal) + 6)..]);
        return document.RootElement.Clone();
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void Test(string name, Action action)
    {
        _total++;
        try
        {
            action();
            Console.WriteLine("PASS " + name);
        }
        catch (Exception exception)
        {
            _failed++;
            Console.Error.WriteLine("FAIL " + name + ": " + exception.Message);
        }
    }

    private sealed class FakeRunner(ProcessQualificationManifest manifest) : IProcessCommandRunner
    {
        public string? Fault { get; init; }
        public List<string> Operations { get; } = [];
        public Task<ProcessCommandResult> RunAsync(PlannedCommand command, CancellationToken ct = default)
        {
            Operations.Add(command.Operation);
            var output = command.Operation switch
            {
                "git-source" => manifest.Target.SourceCommit + " " + manifest.Target.SourceTree,
                "codex-version" => Fault == "cli" ? "foreign 0.159.2" : "codex-cli 0.159.2",
                "git-status" => Fault == "dirty" ? "?? foreign" : "",
                _ => throw new InvalidOperationException("unexpected operation " + command.Operation)
            };
            return Task.FromResult(new ProcessCommandResult(
                command.Operation, (Fault == "head-exit" && command.Operation == "git-source"
                    || Fault == "status-exit" && command.Operation == "git-status") ? 1 : 0, 4242, [], output, "",
                "stdout-hash", "stderr-hash", TimeSpan.FromSeconds(1), false));
        }
    }

    private sealed class FakeDocker(ProcessQualificationManifest manifest) : IDockerEngineReader
    {
        public string? ExtraMountSource { get; init; }
        public string? Fault { get; init; }
        public Task<DockerEngineInfo> GetEngineInfoAsync(CancellationToken ct = default) =>
            Task.FromResult(new DockerEngineInfo(Fault == "engine" ? "foreign" : manifest.Docker.EngineId, "1.2"));

        public Task<DockerImageInfo> GetImageAsync(string reference, CancellationToken ct = default) =>
            Task.FromResult(new DockerImageInfo(reference, "sha256:" + new string('9', 64), [reference], Fault == "image" ? "foreign" : manifest.Target.SourceCommit));

        public Task<ContainerBindingObservation> GetContainerAsync(string id, CancellationToken ct = default)
        {
            if (Fault is "nested-read" or "nested-missing") throw new QualificationException("owned synthetic inspect unavailable");
            var role = QualificationManifestParser.RequiredImageRoles.Single(r => RoleId(r) == id);
            var node = System.Text.Json.Nodes.JsonNode.Parse(ContainerDocument(manifest, role).GetRawText())!;
            switch (Fault)
            {
                case "nested-id": node["Id"] = new string('4',64); break;
                case "nested-image": node["Image"] = "sha256:" + new string('8',64); break;
                case "nested-config": node["Config"]!["Labels"]!["unexpected"] = "changed"; break;
                case "nested-owner": node["Config"]!["Labels"]!["org.phleet.qualification.owner"] = "foreign"; break;
                case "nested-host": node["HostConfig"]!["Privileged"] = true; break;
                case "nested-env": node["Config"]!["Env"]![0] = "RabbitMq__Host=amqps://foreign.example"; break;
                case "nested-socket": node["Mounts"] = System.Text.Json.Nodes.JsonNode.Parse("[{\"Type\":\"bind\",\"Source\":\"/foreign/docker.sock\",\"Destination\":\"/run/foreign.sock\"}]"); break;
            }
            if (Fault == "namespace-lower")
                node["Config"]!["Env"]!.AsArray().Add("temporalbridge__namespaces__1=foreign-synthetic");
            if (Fault == "namespace-colon")
                node["Config"]!["Env"]!.AsArray().Add("TEMPORALBRIDGE:NAMESPACES:1=foreign-synthetic");
            if (Fault == "broker-alias")
                node["Config"]!["Env"]!.AsArray().Add("rabbitmq:host=amqp://foreign.example/");
            if (ExtraMountSource is not null)
                node["Mounts"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject
                    { ["Type"]="bind", ["Source"]=ExtraMountSource, ["Destination"]="/run/qualification/extra.json" });
            using var doc = JsonDocument.Parse(node.ToJsonString());
            return Task.FromResult(ContainerBindingObservation.Parse(doc.RootElement));
        }
    }

    private sealed class FakeTemporal : ITemporalMetadataReader
    {
        public Task<TemporalSystemInfo> GetSystemInfoAsync(CancellationToken ct = default) =>
            Task.FromResult(new TemporalSystemInfo("1.2.3", new string('e', 64)));
    }

    private sealed class StaticHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = respond(request);
            if (response.Content is ByteArrayContent bytes && bytes.Headers.ContentType is null)
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/grpc");
            response.TrailingHeaders.TryAddWithoutValidation("grpc-status", "0");
            return Task.FromResult(response);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("process-qualification-").FullName;
        public string Path { get; }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
        }
    }
}
