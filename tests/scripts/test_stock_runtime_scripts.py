"""#492 rollout guard and gateway backup scripts, with stubbed curl/docker (no network, no Docker)."""
import os
import re
import shutil
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
                f'[[ " $* " != *" -K - "* ]] || sed "s/^/stdin: /" >> "{self.calls}"\n'
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


class DefaultOffComposeTests(unittest.TestCase):
    """The dormant openshell profile must never break default installs (#492 review N1/F7)."""

    def test_example_has_no_required_interpolation(self):
        text = (ROOT / "docker-compose.example.yml").read_text()
        self.assertNotIn(":?", text)

    def test_gateway_is_reachable_on_fleet_net_and_loopback_only(self):
        """#492 review F3/G1: CLI over fleet-net, host-network supervisor over a loopback-only binding."""
        text = (ROOT / "docker-compose.example.yml").read_text()
        start = text.index("\n  openshell-gateway:\n")
        end = text.index("\nvolumes:\n", start)
        service = text[start:end]
        self.assertIn('profiles: ["openshell"]', service)
        self.assertIn("      - fleet-net\n", service)
        ports = re.findall(r'^\s+- "([^"]*:8443)"$', service, re.M)
        self.assertEqual(ports, ["127.0.0.1:8443:8443"])
        self.assertNotIn("0.0.0.0", service)
        self.assertNotIn("network_mode", service)

    def test_default_off_compose_config_is_valid(self):
        probe = subprocess.run(["docker", "compose", "version"], capture_output=True, text=True) if shutil.which("docker") else None
        if probe is None or probe.returncode != 0:
            if os.environ.get("CI"):
                self.fail("docker compose is required for this check in CI")
            self.skipTest("docker compose unavailable locally; CI runs this check")
        # Isolated synthetic project: its own directory, .env and project name, never the checkout's .env.
        with tempfile.TemporaryDirectory() as project:
            env_file = Path(project, ".env")
            env_file.write_text(f"FLEET_BASE_DIR={project}\nFLEET_STOCK_MANAGED_ENABLED=false\n")
            env = {k: v for k, v in os.environ.items() if not k.startswith("FLEET_")}
            result = subprocess.run(["docker", "compose", "-p", "phleet-default-off-check", "--project-directory", project,
                                     "--env-file", str(env_file), "-f", str(ROOT / "docker-compose.example.yml"), "config", "--quiet"],
                                    env=env, capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)


class GatewayBackupTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.state = Path(self.tmp.name, "state")
        (self.state / "tls").mkdir(parents=True)
        with sqlite3.connect(self.state / "gateway.db") as db:
            db.execute("create table sandboxes(id text)")
            db.execute("insert into sandboxes values ('synthetic')")
        (self.state / "tls" / "jwt").mkdir()
        for name in ("gateway.pem", "gateway-key.pem", "client-ca.pem", "jwt/signing.pem", "jwt/public.pem", "jwt/kid"):
            (self.state / "tls" / name).write_text("synthetic\n")
        self.config = Path(self.tmp.name, "gateway.toml")
        self.config.write_text("[openshell]\nversion = 2\n")
        self.backups = Path(self.tmp.name, "backups")

    def tearDown(self):
        self.tmp.cleanup()

    def run_backup(self, state=None):
        return subprocess.run(["bash", str(BACKUP), str(self.backups), str(self.config), str(state or self.state)], capture_output=True, text=True)

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
        sums = (snapshot / "SHA256SUMS").read_text()
        self.assertIn("state/tls/client-ca.pem", sums)
        self.assertIn("config/gateway.toml", sums)

    def test_incomplete_trust_or_config_coverage_is_invalid(self):
        for missing in (self.state / "tls" / "jwt" / "kid", self.config):
            with self.subTest(missing=missing.name):
                content = missing.read_text()
                missing.unlink()
                result = self.run_backup()
                missing.write_text(content)
                self.assertEqual(result.returncode, 1)
                self.assertFalse((self.backups / "last-success").exists())
                self.assertEqual(list(self.backups.glob("gateway-*")), [])  # partial key material deleted
                self.assertTrue((self.backups / "last-failure").exists())

    def test_linked_trust_ancestor_or_state_dir_is_invalid(self):
        real = Path(self.tmp.name, "real-jwt")
        shutil.move(str(self.state / "tls" / "jwt"), real)
        (self.state / "tls" / "jwt").symlink_to(real, target_is_directory=True)
        result = self.run_backup()
        self.assertEqual(result.returncode, 1)
        self.assertFalse((self.backups / "last-success").exists())

    def test_keeps_seven_complete_snapshots_and_deletes_invalid_ones(self):
        self.backups.mkdir(mode=0o700)
        for day in range(1, 10):
            old = self.backups / f"gateway-2026010{day}T000000Z"
            old.mkdir()
            (old / ("COMPLETE" if day != 5 else "INVALID")).write_text("")
        self.assertEqual(self.run_backup().returncode, 0)
        names = sorted(p.name for p in self.backups.iterdir() if p.name.startswith("gateway-"))
        complete = [n for n in names if (self.backups / n / "COMPLETE").exists()]
        self.assertEqual(len(complete), 7)
        self.assertNotIn("gateway-20260105T000000Z", names)  # invalid snapshots hold keys: deleted

    def test_sigterm_mid_run_deletes_the_snapshot_and_never_stamps_success(self):
        """#492 review F17r: a job-runner SIGTERM must not leave a key-bearing snapshot or last-success."""
        import signal
        import time
        stub_bin = Path(self.tmp.name, "slow-bin")
        stub_bin.mkdir()
        marker = Path(self.tmp.name, "copying")
        stub = stub_bin / "python3"
        # Stands in for the copy step: writes key material into the snapshot, then is still running.
        stub.write_text("#!/usr/bin/env bash\ncat > /dev/null\n"
                        f'echo synthetic-key > "$4/gateway-key.pem"\n: > "{marker}"\nexec sleep 30\n')
        stub.chmod(0o755)
        env = {**os.environ, "PATH": f"{stub_bin}:{os.environ['PATH']}"}
        proc = subprocess.Popen(["bash", str(BACKUP), str(self.backups), str(self.config), str(self.state)],
                                env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, start_new_session=True)
        try:
            deadline = time.monotonic() + 10
            while not marker.exists() and time.monotonic() < deadline:
                time.sleep(0.05)
            self.assertTrue(marker.exists(), "backup never reached the copy step")
            self.assertEqual(len(list(self.backups.glob("gateway-*"))), 1)
            os.killpg(proc.pid, signal.SIGTERM)  # what a job runner or `timeout` sends to the group
            _, stderr = proc.communicate(timeout=10)
        finally:
            if proc.poll() is None:
                os.killpg(proc.pid, signal.SIGKILL)
                proc.wait()
        self.assertEqual(proc.returncode, 143, stderr)
        self.assertIn("backup_interrupted", stderr)
        self.assertEqual(list(self.backups.glob("gateway-*")), [])  # the key-bearing partial is gone
        self.assertFalse((self.backups / "last-success").exists())
        self.assertFalse((self.backups / "last-success.tmp").exists())
        self.assertTrue((self.backups / "last-failure").exists())

    def test_missing_database_fails_without_a_success_marker(self):
        result = self.run_backup(Path(self.tmp.name, "absent"))
        self.assertEqual(result.returncode, 1)
        self.assertFalse((self.backups / "last-success").exists())


if __name__ == "__main__":
    unittest.main()
