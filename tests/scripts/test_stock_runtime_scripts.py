"""#492 rollout guard and gateway backup scripts, with stubbed curl/docker (no network, no Docker)."""
import os
import sqlite3
import stat
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
GUARD = ROOT / "scripts" / "check-stock-runtime-rollout.sh"
BACKUP = ROOT / "scripts" / "backup-stock-gateway.sh"


class RolloutGuardTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.bin = Path(self.tmp.name, "bin")
        self.bin.mkdir()
        self.calls = Path(self.tmp.name, "calls")
        for tool in ("curl", "docker"):
            stub = self.bin / tool
            stub.write_text(
                "#!/usr/bin/env bash\n"
                f'echo "{tool} $*" >> "{self.calls}"\n'
                f'rc_var=STUB_{tool.upper()}_RC; out_var=STUB_{tool.upper()}_OUT\n'
                'printf "%s" "${!out_var:-}"\n'
                'exit "${!rc_var:-0}"\n')
            stub.chmod(0o755)

    def tearDown(self):
        self.tmp.cleanup()

    def run_guard(self, *args, **env):
        environment = {"PATH": f"{self.bin}:{os.environ['PATH']}", "HOME": self.tmp.name, **env}
        return subprocess.run(["bash", str(GUARD), *args], env=environment, capture_output=True, text=True)

    def dockerfile(self, labelled):
        path = Path(self.tmp.name, "Dockerfile")
        path.write_text("FROM x\n" + ("LABEL org.phleet.managed-stock-runtime=1\n" if labelled else ""))
        return str(path)

    def test_capable_source_passes_without_any_preflight_call(self):
        result = self.run_guard("--dockerfile", self.dockerfile(True))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse(self.calls.exists())

    def test_this_repository_orchestrator_declares_the_capability(self):
        result = self.run_guard("--dockerfile", str(ROOT / "src/Fleet.Orchestrator/Dockerfile"))
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_incapable_target_needs_zero_counts(self):
        zero = '{"desiredOpenShell":0,"appliedOpenShell":0,"unresolvedBindings":0}'
        ok = self.run_guard("--dockerfile", self.dockerfile(False), ORCHESTRATOR_AUTH_TOKEN="t", STUB_CURL_OUT=zero)
        self.assertEqual(ok.returncode, 0, ok.stderr)
        self.assertIn("Authorization: Bearer t", self.calls.read_text())

    def test_incapable_target_refuses_managed_unavailable_or_malformed(self):
        cases = [
            dict(ORCHESTRATOR_AUTH_TOKEN="t", STUB_CURL_OUT='{"desiredOpenShell":0,"appliedOpenShell":1,"unresolvedBindings":0}'),
            dict(ORCHESTRATOR_AUTH_TOKEN="t", STUB_CURL_RC="7"),
            dict(ORCHESTRATOR_AUTH_TOKEN="t", STUB_CURL_OUT='{"desiredOpenShell":0}'),
            dict(ORCHESTRATOR_AUTH_TOKEN="t", STUB_CURL_OUT="not json"),
            dict(),  # no operator token: never assume "none"
        ]
        for env in cases:
            with self.subTest(env=env):
                result = self.run_guard("--dockerfile", self.dockerfile(False), **env)
                self.assertEqual(result.returncode, 1)
                self.assertIn("REFUSED", result.stderr)

    def test_image_mode_reads_the_label_and_refuses_uninspectable_images(self):
        self.assertEqual(self.run_guard("--image", "x", STUB_DOCKER_OUT="1").returncode, 0)
        self.assertEqual(self.run_guard("--image", "x", STUB_DOCKER_RC="1").returncode, 1)


class GatewayBackupTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.state = Path(self.tmp.name, "state")
        (self.state / "tls").mkdir(parents=True)
        with sqlite3.connect(self.state / "gateway.db") as db:
            db.execute("create table sandboxes(id text)")
            db.execute("insert into sandboxes values ('synthetic')")
        (self.state / "tls" / "client-ca.pem").write_text("synthetic\n")
        self.backups = Path(self.tmp.name, "backups")

    def tearDown(self):
        self.tmp.cleanup()

    def run_backup(self, state=None):
        return subprocess.run(["bash", str(BACKUP), str(self.backups), str(state or self.state)], capture_output=True, text=True)

    def test_snapshot_is_complete_private_and_integrity_checked(self):
        result = self.run_backup()
        self.assertEqual(result.returncode, 0, result.stderr)
        (snapshot,) = [p for p in self.backups.iterdir() if p.name.startswith("gateway-")]
        self.assertTrue((snapshot / "COMPLETE").exists())
        self.assertTrue((self.backups / "last-success").exists())
        self.assertEqual(stat.S_IMODE(self.backups.stat().st_mode), 0o700)
        for path in snapshot.rglob("*"):
            if path.is_file():
                self.assertEqual(stat.S_IMODE(path.stat().st_mode), 0o600, path)
        with sqlite3.connect(snapshot / "gateway.db") as db:
            self.assertEqual(db.execute("select id from sandboxes").fetchone()[0], "synthetic")
        self.assertIn("state/tls/client-ca.pem", (snapshot / "SHA256SUMS").read_text())

    def test_keeps_seven_complete_snapshots_and_leaves_invalid_ones(self):
        self.backups.mkdir(mode=0o700)
        for day in range(1, 10):
            old = self.backups / f"gateway-2026010{day}T000000Z"
            old.mkdir()
            (old / ("COMPLETE" if day != 5 else "INVALID")).write_text("")
        self.assertEqual(self.run_backup().returncode, 0)
        names = sorted(p.name for p in self.backups.iterdir() if p.name.startswith("gateway-"))
        complete = [n for n in names if (self.backups / n / "COMPLETE").exists()]
        self.assertEqual(len(complete), 7)
        self.assertIn("gateway-20260105T000000Z", names)  # invalid kept for the operator

    def test_missing_database_fails_without_a_success_marker(self):
        result = self.run_backup(Path(self.tmp.name, "absent"))
        self.assertEqual(result.returncode, 1)
        self.assertFalse((self.backups / "last-success").exists())


if __name__ == "__main__":
    unittest.main()
