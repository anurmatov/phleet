"""#507 operator regression logic: identity checks and cleanup against an in-memory gateway; no CLI, network or fixture."""
import importlib.util
import json
import os
import shutil
import signal
import tempfile
import time
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
_spec = importlib.util.spec_from_file_location("stock_workspace_transfer", ROOT / "scripts" / "stock_workspace_transfer.py")
transfer = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(transfer)

OLD = {"sandboxName": "fl-0000000000000001", "sandboxId": "11111111-1111-4111-8111-111111111111", "image": "r/a@sha256:" + "a" * 64}
NEW = {"sandboxName": "fl-0000000000000002", "sandboxId": "22222222-2222-4222-8222-222222222222", "image": "r/a@sha256:" + "b" * 64,
       "workloadWorkspacePath": "/home/agent/workspace"}
REFUSAL = b"sandbox source path '/workspace' is outside the sandbox workspace (/home/agent)"
BASELINE_ID, REPLACEMENT_ID, OTHER_ID = ("sha256:" + c * 64 for c in "abc")


class FakeRegression(transfer.Regression):
    """The pinned CLI and orchestrator as an in-memory gateway; every call is recorded."""

    def __init__(self, work):
        fixture = {"orchestratorUrl": "http://127.0.0.1:9", "tokenEnv": "T", "cli": "/x", "cliConfigDir": work + "/config", "workloadHome": "/home/agent",
                   "freshAgent": "fresh", "freshDockerWorkspace": work + "/docker", "legacyAgent": "legacy",
                   "baselineImage": OLD["image"], "replacementImage": NEW["image"], "receiptDir": work}
        super().__init__(fixture, str(ROOT), "synthetic", work)
        self.gateway = {OLD["sandboxName"]: {"id": OLD["sandboxId"], "phase": "Stopped"}}
        self.calls, self.api_calls = [], []
        self.manifest_code = 0
        self.stop_code = 0
        self.download_code = {}
        self.on_exec = None
        self.legacy = {"state": "revoked", "applied": dict(OLD)}
        self.now, self.delay, self.timeouts = 0.0, 0.0, []
        self.stop_keeps_phase = False
        # Local images by reference (tag or digest) and the image each sandbox UUID's workload container was created from.
        self.images = {OLD["image"]: BASELINE_ID, NEW["image"]: REPLACEMENT_ID}
        self.workloads = {OLD["sandboxId"]: BASELINE_ID, NEW["sandboxId"]: REPLACEMENT_ID}
        self.docker_calls = []
        # Docker agent containers by exact name: full ID, running, StartedAt.
        self.containers = {}
        self.fresh_applied = "docker"
        self.api_delay = 0.0

    def docker(self, args, timeout=60):
        self.docker_calls.append(list(args))
        self.timeouts.append((self.now, timeout))
        if self.delay > timeout:
            self.now += timeout
            return 124, b""
        self.now += self.delay
        if args[:2] == ["image", "inspect"]:
            return (0, self.images[args[-1]].encode() + b"\n") if args[-1] in self.images else (1, b"")
        if args[0] == "ps" and any(a.startswith("name=") for a in args):
            name = next(a for a in args if a.startswith("name="))[len("name=^/"):-1]
            return 0, (self.containers[name]["id"].encode() + b"\n") if name in self.containers else b""
        if args[0] == "ps":
            sandbox = next(a.removeprefix("label=openshell.ai/sandbox-id=") for a in args if a.startswith("label=openshell.ai/sandbox-id="))
            return 0, (b"container-" + sandbox.encode() + b"\n") if sandbox in self.workloads else b""
        container = next((c for c in self.containers.values() if c["id"] == args[-1]), None)
        if args[0] == "stop":
            if container:
                container["running"] = False
            return (0, b"") if container else (1, b"")
        if args[0] == "inspect" and container:
            field = {"{{.State.Running}}": "true" if container["running"] else "false", "{{.State.StartedAt}}": container["started"],
                     "{{json .Mounts}}\t{{.Created}}": json.dumps(container.get("mounts", [])) + "\t" + container.get("created", "")}
            return 0, field[args[2]].encode() + b"\n"
        if args[0] == "inspect":
            sandbox = args[-1].removeprefix("container-")
            return (0, self.workloads[sandbox].encode()) if sandbox in self.workloads else (1, b"")
        return 1, b""

    def clock(self):
        return self.now

    def sleep(self, seconds):
        self.now += seconds

    def cli(self, args, timeout=300, scope=None):
        self.calls.append(list(args))
        self.timeouts.append((self.now, timeout))
        if self.delay > timeout:  # a call slower than its bound returns as timed out at the bound
            self.now += timeout
            return 124, b"", b"timeout"
        self.now += self.delay
        verb, name = args[1], args[2] if len(args) > 2 else ""
        sandbox = self.gateway.get(name)
        if verb == "get":
            return (0, json.dumps({"id": sandbox["id"], "name": name, "phase": sandbox["phase"]}).encode(), b"") if sandbox else (1, b"", b"")
        if verb == "start":
            sandbox["phase"] = "Ready"
            return 0, b"", b""
        if verb == "stop":
            if self.stop_code == 0 and not self.stop_keeps_phase:
                sandbox["phase"] = "Stopped"
            return self.stop_code, b"", b""
        if verb == "exec":
            if self.on_exec:
                self.on_exec(args)
            if "python3" in args:
                return self.manifest_code, json.dumps({p: {"file": ["f", "0" * 64, False]} for p in args[args.index("-c") + 2:]}).encode(), b""
            return 0, b"", b""
        if verb == "download":
            source = args[3]
            return (1, b"", REFUSAL) if source == "/workspace" else (self.download_code.get(source, 0), b"", b"")
        return 1, b"", b""

    def api(self, method, path, body=None, timeout=900):
        self.api_calls.append((method, path))
        self.timeouts.append((self.now, timeout))
        if self.api_delay > timeout:
            self.now += timeout
            return 0, None
        self.now += self.api_delay
        if method == "GET" and path == "/api/agents/legacy/config":
            return 200, {"appliedRuntimeKind": "openshell", "appliedRuntimeBinding": json.dumps(self.legacy)}
        if method == "GET" and path == "/api/agents/fresh/config":
            return 200, {"runtimeKind": "openshell", "appliedRuntimeKind": self.fresh_applied, "appliedRuntimeBinding": None}
        if method == "GET" and path == "/api/agents/fresh":
            return 200, {"name": "fresh", "containerName": "fleet-fresh"}
        return 200, {"message": "ok"}

    def mutations(self):
        return [c for c in self.calls if c[1] in ("start", "stop", "exec", "download")]


class StockWorkspaceTransferTests(unittest.TestCase):
    def setUp(self):
        self.work = tempfile.mkdtemp(prefix="transfer-test-")
        self.addCleanup(lambda: __import__("shutil").rmtree(self.work, ignore_errors=True))
        self.run_ = FakeRegression(self.work)

    def test_a_fixture_that_disagrees_with_the_supplied_gateway_is_blocked_before_anything_starts(self):
        for gateway in [{OLD["sandboxName"]: {"id": "99999999-9999-4999-8999-999999999999", "phase": "Stopped"}}, {}]:
            with self.subTest(gateway=gateway):
                self.run_.gateway = gateway
                with self.assertRaises(transfer.Blocked):
                    self.run_.check_fixture_state()
                self.assertEqual([], self.run_.mutations())

    def test_a_running_legacy_sandbox_is_blocked_and_never_touched(self):
        self.run_.gateway[OLD["sandboxName"]]["phase"] = "Ready"
        with self.assertRaises(transfer.Blocked):
            self.run_.check_fixture_state()
        self.assertEqual([], self.run_.mutations())

    def test_a1_passes_and_always_stops_the_sandbox_it_started(self):
        self.run_.check_fixture_state()
        self.run_.case("A1", self.run_.a1_legacy_control)
        self.assertEqual("PASS", self.run_.receipt["cases"]["A1"]["result"])
        self.assertEqual("Stopped", self.run_.gateway[OLD["sandboxName"]]["phase"])
        self.assertEqual([{"action": "stop", "sandboxName": OLD["sandboxName"], "sandboxId": OLD["sandboxId"], "result": "stopped"}],
                         self.run_.receipt["cleanup"])
        self.assertEqual(0, self.run_.exit_code())

    def test_a_failed_manifest_still_stops_the_started_sandbox(self):
        self.run_.check_fixture_state()
        self.run_.manifest_code = 1
        self.run_.case("A1", self.run_.a1_legacy_control)
        self.assertEqual("FAIL", self.run_.receipt["cases"]["A1"]["result"])
        self.assertEqual("Stopped", self.run_.gateway[OLD["sandboxName"]]["phase"])
        self.assertEqual("stopped", self.run_.receipt["cleanup"][-1]["result"])
        self.assertEqual(1, self.run_.exit_code())

    def test_a_failed_stop_is_kept_in_the_receipt_and_fails_the_run(self):
        self.run_.check_fixture_state()
        self.run_.stop_code = 1
        self.run_.case("A1", self.run_.a1_legacy_control)
        self.assertEqual("PASS", self.run_.receipt["cases"]["A1"]["result"])
        self.assertEqual("error", self.run_.receipt["cleanup"][-1]["result"])
        self.assertEqual(1, self.run_.exit_code())

    def test_a_swapped_sandbox_is_never_stopped_or_used(self):
        self.run_.check_fixture_state()
        name = OLD["sandboxName"]

        def swap(args):  # a same-named foreign sandbox replaces the persisted one while staged
            self.run_.gateway[name] = {"id": "33333333-3333-4333-8333-333333333333", "phase": "Ready"}
        self.run_.on_exec = swap
        self.run_.case("A1", self.run_.a1_legacy_control)
        self.assertEqual("FAIL", self.run_.receipt["cases"]["A1"]["result"])
        self.assertNotIn(["sandbox", "stop", name], self.run_.calls)
        self.assertNotIn("python3", [a for c in self.run_.calls for a in c])
        self.assertEqual("error", self.run_.receipt["cleanup"][-1]["result"])
        self.assertEqual(1, self.run_.exit_code())

    def test_a_failed_download_revokes_the_agent_and_keeps_the_result(self):
        self.run_.check_fixture_state()
        self.run_.state["legacy"] = {"/workspace": {}}
        self.run_.gateway[NEW["sandboxName"]] = {"id": NEW["sandboxId"], "phase": "Ready"}
        self.run_.download_code[NEW["workloadWorkspacePath"]] = 1
        applied = {"state": "applied", "applied": NEW, "superseded": [dict(OLD)]}
        self.run_.api = lambda method, path, body=None, timeout=900: (
            self.run_.api_calls.append((method, path)) or
            ((200, {"appliedRuntimeKind": "openshell", "appliedRuntimeBinding": json.dumps(applied)}) if method == "GET" else (200, {})))
        self.run_.case("A3", self.run_.a3_legacy_image_change, agent="legacy")
        self.assertEqual("FAIL", self.run_.receipt["cases"]["A3"]["result"])
        self.assertEqual(("POST", "/api/agents/legacy/revoke"), self.run_.api_calls[-1])
        self.assertEqual({"action": "revoke", "agent": "legacy", "result": "revoked"}, self.run_.receipt["cleanup"][-1])
        self.assertEqual("Stopped", self.run_.gateway[OLD["sandboxName"]]["phase"])  # the old sandbox was never started
        self.assertEqual(1, self.run_.exit_code())

    # ── image identity: recorded references stay as recorded; the pinned image is compared by resolved ID ──

    def test_a_tag_recorded_legacy_binding_on_the_pinned_image_passes_and_is_never_rewritten(self):
        tag = "registry.example.test/stock-agent:legacy"
        self.run_.legacy["applied"]["image"] = tag
        self.run_.images[tag] = BASELINE_ID
        self.run_.check_fixture_state()
        self.assertEqual(tag, self.run_.state["old"]["image"])
        identity = self.run_.receipt["imageIdentity"][-1]
        self.assertEqual((tag, OLD["image"], BASELINE_ID, BASELINE_ID), (identity["recorded"], identity["pinned"], identity["recordedId"], identity["workloadId"]))
        self.assertEqual([], self.run_.mutations())

    def test_a_digest_recorded_legacy_binding_passes(self):
        self.run_.check_fixture_state()
        self.assertEqual(OLD["image"], self.run_.state["old"]["image"])

    def test_legacy_image_identity_faults_are_blocked_before_anything_starts(self):
        tag = "registry.example.test/stock-agent:moved"
        cases = {
            "moved_tag": lambda: (self.run_.legacy["applied"].update(image=tag), self.run_.images.update({tag: OTHER_ID})),
            "unresolvable_tag": lambda: self.run_.legacy["applied"].update(image=tag),
            "other_workload_image": lambda: self.run_.workloads.update({OLD["sandboxId"]: OTHER_ID}),
            "no_workload_container": lambda: self.run_.workloads.pop(OLD["sandboxId"]),
            "unresolvable_pin": lambda: self.run_.images.pop(OLD["image"]),
            "same_images": lambda: self.run_.images.update({NEW["image"]: BASELINE_ID}),
        }
        for name, arrange in cases.items():
            with self.subTest(fault=name):
                self.run_ = FakeRegression(self.work)
                arrange()
                with self.assertRaises(transfer.Blocked):
                    self.run_.check_fixture_state()
                self.assertEqual([], self.run_.mutations())

    def a3_until_identity(self, new):
        """A3 with the transfer itself stubbed: only the applied binding's identity and custody decide the result."""
        self.run_.check_fixture_state()
        self.run_.state["legacy"] = {"/workspace": {}}
        self.run_.gateway[new["sandboxName"]] = {"id": new["sandboxId"], "phase": "Ready"}
        applied = {"state": "applied", "applied": new, "superseded": [dict(self.run_.state["old"])]}
        self.run_.api = lambda method, path, body=None, timeout=900: (
            (200, {"appliedRuntimeKind": "openshell", "appliedRuntimeBinding": json.dumps(applied)}) if method == "GET" else (200, {}))
        empty = tempfile.mkdtemp(dir=self.work)
        self.run_.download = lambda sandbox, source, label: (0, empty, b"")
        self.run_.remote_manifest = lambda sandbox, *paths: self.run_.state["legacy"]
        self.run_.case("A3", self.run_.a3_legacy_image_change)
        return self.run_.receipt["cases"]["A3"]

    def test_a3_accepts_a_replacement_recorded_by_tag_or_digest_and_checks_its_workload(self):
        tag = "registry.example.test/stock-agent:replacement"
        for recorded in [NEW["image"], tag]:
            with self.subTest(recorded=recorded):
                self.run_ = FakeRegression(self.work)
                self.run_.images[tag] = REPLACEMENT_ID
                self.assertEqual("PASS", self.a3_until_identity(dict(NEW, image=recorded))["result"])
        self.run_ = FakeRegression(self.work)
        self.run_.workloads[NEW["sandboxId"]] = BASELINE_ID  # recorded right, but the new workload runs the old image
        result = self.a3_until_identity(dict(NEW))
        self.assertEqual("FAIL", result["result"])
        self.assertIn("replacement image", result["reason"])

    def test_a3_requires_the_old_custody_exactly_as_recorded(self):
        self.run_.check_fixture_state()
        new = dict(NEW)
        self.run_.state["legacy"] = {"/workspace": {}}
        self.run_.gateway[new["sandboxName"]] = {"id": new["sandboxId"], "phase": "Ready"}
        rewritten = dict(OLD, image="registry.example.test/stock-agent:rewritten")
        applied = {"state": "applied", "applied": new, "superseded": [rewritten]}
        self.run_.api = lambda method, path, body=None, timeout=900: (
            (200, {"appliedRuntimeKind": "openshell", "appliedRuntimeBinding": json.dumps(applied)}) if method == "GET" else (200, {}))
        self.run_.case("A3", self.run_.a3_legacy_image_change)
        self.assertEqual("FAIL", self.run_.receipt["cases"]["A3"]["result"])
        self.assertIn("custody", self.run_.receipt["cases"]["A3"]["reason"])

    # ── A5 post-Docker-commit cleanup: the committed Docker workload, never a managed revoke (409) ──

    PRIOR, COMMITTED = "c" * 64, "d" * 64

    def owned_container(self, container_id, created="2999-01-01T00:00:00Z", source=None):
        return {"id": container_id, "running": True, "started": created, "created": created,
                "mounts": [{"Type": "bind", "Source": source or self.run_.f["freshDockerWorkspace"], "Destination": "/workspace"}]}

    def pending_docker(self, committed=True, applied="docker"):
        self.run_.containers["fleet-fresh"] = self.owned_container(self.COMMITTED)
        self.run_.state["dockerApply"] = {"agent": "fresh", "container": "fleet-fresh", "before": self.PRIOR, "issuedAt": time.time(),
                                          **({"committed": self.COMMITTED} if committed else {})}
        self.run_.fresh_applied = applied

    def test_a_failure_after_the_docker_commit_stops_the_pinned_workload_instead_of_revoking(self):
        self.pending_docker()
        self.run_.case("A5", self.fail_case, agent="fresh")
        self.assertEqual({"action": "docker-stop", "agent": "fresh", "container": "fleet-fresh", "containerId": self.COMMITTED, "result": "stopped"},
                         self.run_.receipt["cleanup"][-1])
        self.assertFalse(self.run_.containers["fleet-fresh"]["running"])
        self.assertNotIn(("POST", "/api/agents/fresh/revoke"), self.run_.api_calls)
        self.assertEqual(1, self.run_.exit_code())

    def test_an_uncommitted_docker_apply_still_revokes(self):
        self.pending_docker(committed=False, applied="openshell")
        self.run_.case("A5", self.fail_case, agent="fresh")
        self.assertEqual(("POST", "/api/agents/fresh/revoke"), self.run_.api_calls[-1])
        self.assertTrue(self.run_.containers["fleet-fresh"]["running"])
        self.assertNotIn("stop", [c[0] for c in self.run_.docker_calls])

    def test_unproven_ownership_never_stops_a_same_name_container(self):
        foreign_dir = tempfile.mkdtemp(dir=self.work)
        cases = {
            "the prior container": dict(committed=True, container=lambda: self.owned_container(self.PRIOR)),
            "not the pinned committed ID": dict(committed=True, container=lambda: self.owned_container("e" * 64)),
            "foreign mounts, nothing pinned": dict(committed=False, container=lambda: self.owned_container("e" * 64, source=foreign_dir)),
            "created before the apply, nothing pinned": dict(committed=False, container=lambda: self.owned_container("e" * 64, created="2001-01-01T00:00:00Z")),
            "unreadable mounts, nothing pinned": dict(committed=False, container=lambda: dict(self.owned_container("e" * 64), mounts="not-a-list")),
        }
        for name, case in cases.items():
            with self.subTest(case=name):
                self.run_ = FakeRegression(self.work)
                self.pending_docker(committed=case["committed"])
                self.run_.containers["fleet-fresh"] = case["container"]()
                self.run_.case("A5", self.fail_case, agent="fresh")
                self.assertEqual("error", self.run_.receipt["cleanup"][-1]["result"])
                self.assertIn("not stopped", self.run_.receipt["cleanup"][-1]["reason"])
                self.assertTrue(self.run_.containers["fleet-fresh"]["running"])
                self.assertNotIn("stop", [c[0] for c in self.run_.docker_calls])

    def test_one_deadline_from_dispatcher_entry_covers_runtime_lookup_docker_stop_and_revoke(self):
        for applied in ["docker", "openshell", None]:
            with self.subTest(applied=applied):
                self.run_ = FakeRegression(self.work)
                self.pending_docker(committed=False, applied=applied)
                self.run_.delay, self.run_.api_delay = 59.0, 70.0
                start = self.run_.now
                self.run_.agent_cleanup("fresh")
                self.assert_one_deadline(start)
                self.assertTrue(any(e["result"] == "error" for e in self.run_.receipt["cleanup"]))

    # ── A5 excluded-state proof: sentinels plus the adapter's stage record ──

    def a5_with(self, during=None, before_stage=None, after=None, record=True):
        """A5 with the sandbox side stubbed. The fake Docker apply writes the adapter's stage record around its mirror;
        `before_stage`, `during` and `after` mutate the Docker workspace before, during and after the transfer stage."""
        ws = self.run_.f["freshDockerWorkspace"]
        os.makedirs(os.path.join(ws, "codex"))
        with open(os.path.join(ws, "codex", "auth.json"), "w") as handle:
            handle.write("synthetic")
        with open(os.path.join(ws, "old.txt"), "w") as handle:
            handle.write("old")
        self.run_.state["sentinels"] = self.run_.seed_sentinels(ws)
        epoch = "0f0f0f0f-0000-4000-8000-000000000001"
        self.run_.state["fresh"] = dict(NEW, epoch=epoch)
        self.run_.gateway[NEW["sandboxName"]] = {"id": NEW["sandboxId"], "phase": "Ready"}
        tree = tempfile.mkdtemp(dir=self.work)
        for name, content in {"kept.txt": "edited", ".mcp.json": "{}"}.items():
            with open(os.path.join(tree, name), "w") as handle:
                handle.write(content)
        self.run_.exec_ok = lambda sandbox, script, *args, timeout=60: True
        self.run_.download = lambda sandbox, source, label: (0, tree, b"")
        self.run_.fresh_applied = "openshell"
        records = os.path.join(self.run_.f["cliConfigDir"], "managed", "fresh", epoch.replace("-", ""), "docker-before")
        real_api = self.run_.api

        def api(method, path, body=None, timeout=900):
            if method == "POST" and path.endswith("/runtime/apply"):
                if before_stage:
                    before_stage(ws)
                stage = {"version": 1, "beforeMirror": transfer.excluded_state(ws, self.run_.excluded)}
                for entry in os.listdir(ws):
                    if entry not in self.run_.excluded:
                        target = os.path.join(ws, entry)
                        shutil.rmtree(target) if os.path.isdir(target) else os.remove(target)
                shutil.copy(os.path.join(tree, "kept.txt"), ws)
                if during:
                    during(ws)
                stage["afterMirror"] = transfer.excluded_state(ws, self.run_.excluded)
                if record:
                    os.makedirs(records, exist_ok=True)
                    with open(os.path.join(records, "op1.excluded.json"), "w") as handle:
                        json.dump(stage, handle)
                self.run_.containers["fleet-fresh"] = self.owned_container(self.COMMITTED)
                self.run_.fresh_applied = "docker"
                if after:
                    after(ws)  # provisioning and the started workload, after the transfer stage
            return real_api(method, path, body, timeout)
        self.run_.api = api
        self.run_.containers["fleet-fresh"] = dict(self.owned_container(self.PRIOR), running=False)
        self.run_.case("A5", self.run_.a5_current_to_docker, agent="fresh")
        return self.run_.receipt["cases"]["A5"], self.run_.receipt.get("excludedStateProof")

    @staticmethod
    def write(ws, rel, content):
        os.makedirs(os.path.dirname(os.path.join(ws, rel)), exist_ok=True)
        with open(os.path.join(ws, rel), "w") as handle:
            handle.write(content)

    def test_a5_passes_when_the_transfer_stage_is_proved_unchanged_and_records_later_changes(self):
        def provisioning_and_workload(ws):
            self.write(ws, ".generated/appsettings.json", "regenerated")
            self.write(ws, "codex/session.json", "written by the running workload")
        result, proof = self.a5_with(after=provisioning_and_workload)
        self.assertEqual("PASS", result["result"], result.get("reason"))
        self.assertEqual(([], []), (proof["acrossMirror"], proof["scriptBeforeVsRecordBefore"]))
        self.assertEqual([".generated/appsettings.json", "codex/session.json"], [c["path"] for c in proof["afterTransferStage"]])
        self.assertEqual([], self.run_.receipt["cleanup"])

    def test_an_unauthorized_auth_write_during_or_before_the_transfer_stage_fails_and_stops_the_workload(self):
        auth = lambda ws: self.write(ws, "codex/auth.json", "unauthorized")
        for stage, kwargs, reason in [("during", {"during": auth}, "during the transfer stage"),
                                      ("before", {"before_stage": auth}, "before the transfer stage")]:
            with self.subTest(stage=stage):
                self.run_ = FakeRegression(tempfile.mkdtemp(dir=self.work))
                result, proof = self.a5_with(**kwargs)
                self.assertEqual("FAIL", result["result"])
                self.assertIn(reason, result["reason"])
                changed = proof["acrossMirror"] if stage == "during" else proof["scriptBeforeVsRecordBefore"]
                self.assertEqual(["codex/auth.json"], [c["path"] for c in changed])  # retained before the assertion
                self.assertEqual(("docker-stop", "stopped", self.COMMITTED),
                                 tuple(self.run_.receipt["cleanup"][-1][k] for k in ("action", "result", "containerId")))
                self.assertNotIn(("POST", "/api/agents/fresh/revoke"), self.run_.api_calls)

    def test_a5_fails_without_the_stage_record_or_with_a_changed_sentinel(self):
        result, proof = self.a5_with(record=False)
        self.assertEqual("FAIL", result["result"])
        self.assertIn("stage record", result["reason"])
        self.run_ = FakeRegression(tempfile.mkdtemp(dir=self.work))
        result, proof = self.a5_with(after=lambda ws: self.write(ws, ".ssh/" + transfer.SENTINEL, "overwritten later"))
        self.assertEqual("FAIL", result["result"])
        self.assertEqual([".ssh/" + transfer.SENTINEL], proof["sentinelsChanged"])

    def test_an_interruption_right_after_the_docker_apply_stops_only_the_proven_new_workload(self):
        def interrupt_next_read(ws):
            def config(agent):
                raise KeyboardInterrupt
            self.run_.config = config
        with self.assertRaises(KeyboardInterrupt):
            self.a5_with(after=interrupt_next_read)
        self.assertEqual("INTERRUPTED", self.run_.receipt["cases"]["A5"]["result"])
        self.assertEqual(("docker-stop", "stopped", self.COMMITTED),
                         tuple(self.run_.receipt["cleanup"][-1][k] for k in ("action", "result", "containerId")))
        self.assertEqual(130, self.run_.exit_code())

    def test_a_foreign_same_name_container_after_an_interruption_is_left_running(self):
        foreign = tempfile.mkdtemp(dir=self.work)

        def foreign_replacement_then_interrupt(ws):
            self.run_.containers["fleet-fresh"] = self.owned_container("f" * 64, source=foreign)

            def config(agent):
                raise KeyboardInterrupt
            self.run_.config = config
        with self.assertRaises(KeyboardInterrupt):
            self.a5_with(after=foreign_replacement_then_interrupt)
        entry = self.run_.receipt["cleanup"][-1]
        self.assertEqual(("docker-stop", "error", "f" * 64), (entry["action"], entry["result"], entry["containerId"]))
        self.assertIn("ownership unproven", entry["reason"])
        self.assertTrue(self.run_.containers["fleet-fresh"]["running"])
        self.assertNotIn("stop", [c[0] for c in self.run_.docker_calls])

    # ── F4: interruption unwinds through the same cleanup ──

    def applied_legacy(self, after_apply):
        """A3 up to a successful Apply; `after_apply` runs right after Apply returned 200."""
        self.run_.check_fixture_state()
        self.run_.state["legacy"] = {"/workspace": {}}
        calls = self.run_.api_calls

        def api(method, path, body=None, timeout=900):
            calls.append((method, path))
            if method == "GET":
                return 200, {"appliedRuntimeKind": "openshell", "appliedRuntimeBinding": json.dumps(self.run_.legacy)}
            if path.endswith("/runtime/apply"):
                after_apply()
            return 200, {}
        self.run_.api = api

    def test_an_interruption_right_after_a_successful_apply_still_revokes_and_records_it(self):
        def interrupt():
            raise KeyboardInterrupt
        self.applied_legacy(lambda: None)
        original = self.run_.config
        self.run_.config = lambda agent: interrupt() if ("POST", "/api/agents/legacy/runtime/apply") in self.run_.api_calls else original(agent)
        with self.assertRaises(KeyboardInterrupt):
            self.run_.case("A3", self.run_.a3_legacy_image_change, agent="legacy")
        self.assertEqual("INTERRUPTED", self.run_.receipt["cases"]["A3"]["result"])
        self.assertEqual(("POST", "/api/agents/legacy/revoke"), self.run_.api_calls[-1])
        self.assertEqual({"action": "revoke", "agent": "legacy", "result": "revoked"}, self.run_.receipt["cleanup"][-1])
        self.assertEqual(130, self.run_.exit_code())

    def test_sigterm_after_apply_unwinds_through_revoke_and_a_second_signal_waits_for_cleanup(self):
        previous = self.run_.install_signal_handlers()
        self.addCleanup(lambda: [signal.signal(sig, handler) for sig, handler in previous.items()])
        revoke = self.run_.revoke_cleanup

        def revoke_with_a_late_signal(agent, end=None):
            os.kill(os.getpid(), signal.SIGINT)  # arrives mid-cleanup: recorded and deferred, never cuts it short
            revoke(agent, end)
        self.run_.revoke_cleanup = revoke_with_a_late_signal
        self.applied_legacy(lambda: os.kill(os.getpid(), signal.SIGTERM))
        with self.assertRaises(transfer.Interrupted):
            self.run_.case("A3", self.run_.a3_legacy_image_change, agent="legacy")
        self.assertEqual(("SIGTERM", ["SIGTERM", "SIGINT"]), (self.run_.interrupted, self.run_.receipt["signals"]))
        self.assertEqual("INTERRUPTED", self.run_.receipt["cases"]["A3"]["result"])
        self.assertEqual("revoked", self.run_.receipt["cleanup"][-1]["result"])
        self.assertEqual(130, self.run_.exit_code())

    def test_a_signal_during_failure_cleanup_is_raised_only_after_cleanup_completes(self):
        previous = self.run_.install_signal_handlers()
        self.addCleanup(lambda: [signal.signal(sig, handler) for sig, handler in previous.items()])
        self.run_.check_fixture_state()
        revoke = self.run_.revoke_cleanup

        def revoke_with_a_signal(agent, end=None):
            os.kill(os.getpid(), signal.SIGTERM)
            revoke(agent, end)
        self.run_.revoke_cleanup = revoke_with_a_signal
        self.run_.api = lambda method, path, body=None, timeout=900: (self.run_.api_calls.append((method, path)) or (500, None))
        with self.assertRaises(transfer.Interrupted):
            self.run_.case("A3", lambda: self.fail_case(), agent="legacy")
        self.assertEqual("FAIL", self.run_.receipt["cases"]["A3"]["result"])
        self.assertEqual(("POST", "/api/agents/legacy/revoke"), self.run_.api_calls[-1])
        self.assertEqual("error", self.run_.receipt["cleanup"][-1]["result"])  # the revoke outcome is kept, not lost
        self.assertEqual(130, self.run_.exit_code())

    @staticmethod
    def fail_case():
        raise AssertionError("synthetic failure")

    def test_an_interrupted_staged_sandbox_is_still_stopped(self):
        self.run_.check_fixture_state()
        real = self.run_.cli

        def cli(args, timeout=300, scope=None):
            if "python3" in args:
                raise KeyboardInterrupt
            return real(args, timeout, scope)
        self.run_.cli = cli
        with self.assertRaises(KeyboardInterrupt):
            self.run_.case("A1", self.run_.a1_legacy_control)
        self.assertEqual("Stopped", self.run_.gateway[OLD["sandboxName"]]["phase"])
        self.assertEqual(["stopped"], [e["result"] for e in self.run_.receipt["cleanup"]])
        self.assertEqual("INTERRUPTED", self.run_.receipt["cases"]["A1"]["result"])

    # ── F7: one 120-second deadline across identity probes, stop and polling ──

    def assert_one_deadline(self, start):
        for began, bound in self.run_.timeouts:
            self.assertLessEqual(began + bound, start + transfer.CLEANUP_SECONDS + 1e-9)
        self.assertLessEqual(self.run_.now - start, transfer.CLEANUP_SECONDS)

    def test_delayed_cleanup_calls_share_one_deadline(self):
        self.run_.gateway[OLD["sandboxName"]]["phase"] = "Ready"
        self.run_.delay = 59.0  # three 59 s calls used to reach 177 s
        start = self.run_.now
        self.run_.stop_cleanup(OLD)
        self.assertEqual("error", self.run_.receipt["cleanup"][-1]["result"])
        self.assert_one_deadline(start)
        self.assertGreaterEqual(len(self.run_.timeouts), 3)

    def test_polling_a_sandbox_that_never_stops_ends_at_the_deadline(self):
        self.run_.gateway[OLD["sandboxName"]]["phase"] = "Ready"
        self.run_.stop_keeps_phase = True
        self.run_.delay = 7.0
        start = self.run_.now
        self.run_.stop_cleanup(OLD)
        entry = self.run_.receipt["cleanup"][-1]
        self.assertEqual("error", entry["result"])
        self.assertIn("deadline", entry["reason"])
        self.assert_one_deadline(start)

    def test_every_mutation_is_preceded_by_an_exact_identity_read(self):
        self.run_.check_fixture_state()
        self.run_.case("A1", self.run_.a1_legacy_control)
        for index, call in enumerate(self.run_.calls):
            if call[1] in ("start", "stop", "exec", "download"):
                self.assertEqual(["sandbox", "get", call[2] if call[1] != "exec" else call[3], "-o", "json"], self.run_.calls[index - 1])


if __name__ == "__main__":
    unittest.main()
