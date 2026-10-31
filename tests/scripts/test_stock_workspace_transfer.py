"""#507 operator regression logic: identity checks and cleanup against an in-memory gateway; no CLI, network or fixture."""
import importlib.util
import json
import tempfile
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


class FakeRegression(transfer.Regression):
    """The pinned CLI and orchestrator as an in-memory gateway; every call is recorded."""

    def __init__(self, work):
        fixture = {"orchestratorUrl": "http://127.0.0.1:9", "tokenEnv": "T", "cli": "/x", "cliConfigDir": "/x", "workloadHome": "/home/agent",
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

    def cli(self, args, timeout=300, scope=None):
        self.calls.append(list(args))
        verb, name = args[1], args[2] if len(args) > 2 else ""
        sandbox = self.gateway.get(name)
        if verb == "get":
            return (0, json.dumps({"id": sandbox["id"], "name": name, "phase": sandbox["phase"]}).encode(), b"") if sandbox else (1, b"", b"")
        if verb == "start":
            sandbox["phase"] = "Ready"
            return 0, b"", b""
        if verb == "stop":
            if self.stop_code == 0:
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
        if method == "GET" and path == "/api/agents/legacy/config":
            return 200, {"appliedRuntimeKind": "openshell", "appliedRuntimeBinding": json.dumps(self.legacy)}
        if method == "GET" and path == "/api/agents/fresh/config":
            return 200, {"runtimeKind": "openshell", "appliedRuntimeKind": "docker", "appliedRuntimeBinding": None}
        return 200, {"message": "ok"}

    def wait(self, predicate, seconds=180):
        return any(predicate() for _ in range(3))

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

    def test_every_mutation_is_preceded_by_an_exact_identity_read(self):
        self.run_.check_fixture_state()
        self.run_.case("A1", self.run_.a1_legacy_control)
        for index, call in enumerate(self.run_.calls):
            if call[1] in ("start", "stop", "exec", "download"):
                self.assertEqual(["sandbox", "get", call[2] if call[1] != "exec" else call[3], "-o", "json"], self.run_.calls[index - 1])


if __name__ == "__main__":
    unittest.main()
