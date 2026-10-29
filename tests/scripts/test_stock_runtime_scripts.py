"""#492 rollout guard and gateway backup scripts, with stubbed curl/docker (no network, no Docker)."""
import os
import re
import shutil
import sqlite3
import stat
import subprocess
import tempfile
import tomllib
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

    def test_gateway_example_sets_the_guest_tls_ca_at_gateway_scope(self):
        """#492: the pinned stock gateway refuses Docker-driver guest TLS bootstrap without guest_tls_ca."""
        config = tomllib.loads((ROOT / "deploy" / "openshell" / "gateway.toml.example").read_text())
        gateway = config["openshell"]["gateway"]
        self.assertEqual(gateway["compute_driver"], "docker")
        self.assertIs(gateway["disable_tls"], False)
        self.assertTrue(Path(gateway["guest_tls_ca"]).is_absolute())
        # The CA that issued the server cert, not the client-certificate CA.
        self.assertNotEqual(gateway["guest_tls_ca"], gateway["tls"]["client_ca_path"])
        # Upstream rejects guest TLS fields inside driver tables.
        for name, driver in config["openshell"].get("drivers", {}).items():
            self.assertFalse({"guest_tls_ca", "guest_tls_cert", "guest_tls_key"} & driver.keys(), name)

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


class BackupBindSourceTests(unittest.TestCase):
    """#492 review W5: setup/upgrade pre-create the unconditional backup bind source 0700, only when absent."""

    MOUNT = "- ${FLEET_STOCK_BACKUP_DIR:-${FLEET_BASE_DIR}/openshell-backup}:/var/lib/fleet-stock-backup:ro"

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.base = Path(self.tmp.name, "fleet")
        self.base.mkdir()
        self.env_file = self.base / ".env"
        self.env_file.write_text("FLEET_STOCK_MANAGED_ENABLED=false\n")

    def tearDown(self):
        self.tmp.cleanup()

    def run_block(self, script, dry_run=False, shell_dir=None):
        """Runs the script's own read_env_var and STOCK_BACKUP_SRC lines, nothing else of the script."""
        text = (ROOT / script).read_text()
        reader = re.search(r"^read_env_var\(\) \{\n.*?^\}\n", text, re.S | re.M).group(0)
        lines = [line for line in text.splitlines() if "STOCK_BACKUP_SRC" in line]
        self.assertEqual(len(lines), 2, lines)
        env = {k: v for k, v in os.environ.items() if not k.startswith("FLEET_")}
        env.update(FLEET_BASE_DIR=str(self.base), ENV_FILE=str(self.env_file), DRY_RUN="true" if dry_run else "false")
        if shell_dir is not None:
            env["FLEET_STOCK_BACKUP_DIR"] = shell_dir
        body = "set -euo pipefail\n" + reader + "\n".join(lines) + '\nprintf "%s" "$STOCK_BACKUP_SRC"\n'
        result = subprocess.run(["bash", "-c", body], env=env, capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        return result.stdout

    def test_scripts_use_the_compose_default(self):
        self.assertIn(self.MOUNT, (ROOT / "docker-compose.example.yml").read_text())
        for script in ("setup.sh", "upgrade.sh"):
            with self.subTest(script=script):
                self.assertIn("${STOCK_BACKUP_SRC:-$FLEET_BASE_DIR/openshell-backup}", (ROOT / script).read_text())

    def test_absent_default_dir_is_created_0700(self):
        for script in ("setup.sh", "upgrade.sh"):
            with self.subTest(script=script):
                target = self.base / "openshell-backup"
                shutil.rmtree(target, ignore_errors=True)
                self.assertEqual(self.run_block(script), str(target))
                self.assertTrue(target.is_dir())
                self.assertEqual(stat.S_IMODE(target.stat().st_mode), 0o700)
                self.assertEqual(target.stat().st_uid, os.getuid())

    def test_absent_configured_dir_is_created_0700(self):
        for script in ("setup.sh", "upgrade.sh"):
            with self.subTest(script=script, source=".env"):
                target = Path(self.tmp.name, script, "env", "backups")
                self.env_file.write_text(f"FLEET_STOCK_BACKUP_DIR={target}\n")
                self.assertEqual(self.run_block(script), str(target))
                self.assertEqual(stat.S_IMODE(target.stat().st_mode), 0o700)
                self.assertFalse((self.base / "openshell-backup").exists())
            with self.subTest(script=script, source="shell"):
                # Compose lets a shell export win over --env-file, so the export is the bind source.
                target = Path(self.tmp.name, script, "shell", "backups")
                self.assertEqual(self.run_block(script, shell_dir=str(target)), str(target))
                self.assertEqual(stat.S_IMODE(target.stat().st_mode), 0o700)

    def test_existing_dir_is_left_untouched(self):
        for script in ("setup.sh", "upgrade.sh"):
            with self.subTest(script=script):
                target = self.base / "openshell-backup"
                shutil.rmtree(target, ignore_errors=True)
                target.mkdir()
                target.chmod(0o750)
                (target / "last-success").write_text("stamp\n")
                before = target.stat()
                self.run_block(script)
                after = target.stat()
                self.assertEqual(stat.S_IMODE(after.st_mode), 0o750)
                self.assertEqual((after.st_uid, after.st_gid, after.st_mtime_ns), (before.st_uid, before.st_gid, before.st_mtime_ns))
                self.assertEqual((target / "last-success").read_text(), "stamp\n")

    def test_setup_dry_run_creates_nothing(self):
        target = Path(self.tmp.name, "configured", "backups")
        self.env_file.write_text(f"FLEET_STOCK_BACKUP_DIR={target}\n")
        self.run_block("setup.sh", dry_run=True)
        self.assertFalse(target.parent.exists())
        self.env_file.write_text("")
        self.run_block("setup.sh", dry_run=True)
        self.assertFalse((self.base / "openshell-backup").exists())
        self.assertRegex((ROOT / "setup.sh").read_text(), r'(?m)^\$DRY_RUN \|\| .*mkdir -p -m 700 "\$STOCK_BACKUP_SRC"$')


def time_stamp():
    import time
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()) + "\n"


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
            self.assertEqual(len(list(self.backups.glob(".partial-gateway-*"))), 1)
            os.killpg(proc.pid, signal.SIGTERM)  # what a job runner or `timeout` sends to the group
            _, stderr = proc.communicate(timeout=10)
        finally:
            if proc.poll() is None:
                os.killpg(proc.pid, signal.SIGKILL)
                proc.wait()
        self.assertEqual(proc.returncode, 143, stderr)
        self.assertIn("backup_interrupted", stderr)
        self.assertEqual(list(self.backups.glob("gateway-*")), [])  # the key-bearing partial is gone
        self.assertEqual(list(self.backups.glob(".partial-gateway-*")), [])
        self.assertFalse((self.backups / "last-success").exists())
        self.assertFalse((self.backups / "last-success.tmp").exists())
        self.assertTrue((self.backups / "last-failure").exists())

    def test_sigkill_mid_run_never_leaves_a_snapshot_that_counts_as_valid(self):
        """#492 review L4: SIGKILL runs no trap, so the key-bearing partial must never carry the gateway-* name."""
        import signal
        import time
        freshness = ROOT / "scripts" / "check-stock-gateway-backup.sh"
        stub_bin = Path(self.tmp.name, "slow-bin")
        stub_bin.mkdir()
        marker = Path(self.tmp.name, "copying")
        stub = stub_bin / "python3"
        stub.write_text("#!/usr/bin/env bash\ncat > /dev/null\n"
                        f'echo synthetic-key > "$4/gateway-key.pem"\n: > "$4/COMPLETE"\n: > "$4/SHA256SUMS"\n: > "{marker}"\nexec sleep 30\n')
        stub.chmod(0o755)
        env = {**os.environ, "PATH": f"{stub_bin}:{os.environ['PATH']}"}
        proc = subprocess.Popen(["bash", str(BACKUP), str(self.backups), str(self.config), str(self.state)],
                                env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, start_new_session=True)
        try:
            deadline = time.monotonic() + 10
            while not marker.exists() and time.monotonic() < deadline:
                time.sleep(0.05)
            self.assertTrue(marker.exists(), "backup never reached the copy step")
            os.killpg(proc.pid, signal.SIGKILL)  # no trap runs
            proc.communicate(timeout=10)
        finally:
            if proc.poll() is None:
                os.killpg(proc.pid, signal.SIGKILL)
                proc.wait()
        self.assertEqual(list(self.backups.glob("gateway-*")), [])
        (leftover,) = list(self.backups.glob(".partial-gateway-*"))
        # Even with a fresh success stamp (from an earlier run), the leftover never satisfies the freshness check.
        (self.backups / "last-success").write_text(time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()) + "\n")
        check = subprocess.run(["bash", str(freshness), str(self.backups)], capture_output=True, text=True)
        self.assertEqual(check.returncode, 1, check.stdout)
        self.assertIn("no complete gateway snapshot", check.stderr)
        # The next run publishes a complete snapshot and prunes the stale key-bearing leftover.
        old = time.time() - 2 * 3600
        os.utime(leftover, (old, old))
        result = self.run_backup()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse(leftover.exists())
        (snapshot,) = list(self.backups.glob("gateway-*"))
        self.assertTrue((snapshot / "COMPLETE").exists())
        self.assertEqual(list(self.backups.glob(".partial-*")), [])
        check = subprocess.run(["bash", str(freshness), str(self.backups)], capture_output=True, text=True)
        self.assertEqual(check.returncode, 0, check.stderr)

    def test_incomplete_gateway_snapshot_from_an_older_version_is_pruned_and_ignored(self):
        legacy = self.backups / "gateway-20260101T000000Z"
        legacy.mkdir(parents=True)
        (legacy / "gateway-key.pem").write_text("synthetic-key\n")
        (self.backups / "last-success").write_text(time_stamp())
        freshness = ROOT / "scripts" / "check-stock-gateway-backup.sh"
        check = subprocess.run(["bash", str(freshness), str(self.backups)], capture_output=True, text=True)
        self.assertIn("no complete gateway snapshot", check.stderr)
        self.assertEqual(self.run_backup().returncode, 0)
        self.assertFalse(legacy.exists())

    def test_missing_database_fails_without_a_success_marker(self):
        result = self.run_backup(Path(self.tmp.name, "absent"))
        self.assertEqual(result.returncode, 1)
        self.assertFalse((self.backups / "last-success").exists())


if __name__ == "__main__":
    unittest.main()
