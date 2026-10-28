"""Review b829 regressions (F15, F17): token off curl argv, no kept key material in failed backups,
and macOS-style linked system ancestors."""
import os
import sqlite3
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
GUARD = ROOT / "scripts" / "check-stock-runtime-rollout.sh"
BACKUP = ROOT / "scripts" / "backup-stock-gateway.sh"


class RolloutTokenTests(unittest.TestCase):
    def test_token_reaches_curl_on_stdin_never_argv(self):
        with tempfile.TemporaryDirectory() as tmp:
            bin_dir, argv, stdin = Path(tmp, "bin"), Path(tmp, "argv"), Path(tmp, "stdin")
            bin_dir.mkdir()
            curl = bin_dir / "curl"
            curl.write_text(
                "#!/usr/bin/env bash\n"
                f'printf "%s\\n" "$@" > "{argv}"\n'
                f'cat > "{stdin}"\n'
                'printf \'{"desiredOpenShell":0,"appliedOpenShell":0,"unresolvedBindings":0}\'\n')
            curl.chmod(0o755)
            dockerfile = Path(tmp, "Dockerfile")
            dockerfile.write_text("FROM x\n")
            token = 'secret-"tok\\en'
            result = subprocess.run(
                ["bash", str(GUARD), "--dockerfile", str(dockerfile)], capture_output=True, text=True,
                env={"PATH": f"{bin_dir}:{os.environ['PATH']}", "HOME": tmp, "ORCHESTRATOR_AUTH_TOKEN": token})
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertNotIn("secret", argv.read_text())
            self.assertIn("-K", argv.read_text().split())
            self.assertEqual(stdin.read_text(), 'header = "Authorization: Bearer secret-\\"tok\\\\en"\n')


class BackupTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.config = Path(self.tmp.name, "gateway.toml")
        self.config.write_text("[openshell]\nversion = 2\n")
        self.backups = Path(self.tmp.name, "backups")

    def tearDown(self):
        self.tmp.cleanup()

    def make_state(self, state):
        (state / "tls" / "jwt").mkdir(parents=True)
        with sqlite3.connect(state / "gateway.db") as db:
            db.execute("create table sandboxes(id text)")
        for name in ("gateway.pem", "gateway-key.pem", "client-ca.pem", "jwt/signing.pem", "jwt/public.pem", "jwt/kid"):
            (state / "tls" / name).write_text("synthetic\n")
        return state

    def run_backup(self, *args, **env):
        return subprocess.run(["bash", str(BACKUP), *map(str, args)], capture_output=True, text=True,
                              env={**os.environ, **env})

    def test_failed_run_deletes_the_partial_snapshot(self):
        state = self.make_state(Path(self.tmp.name, "state"))
        (state / "tls" / "gateway-key.pem").unlink()
        result = self.run_backup(self.backups, self.config, state)
        self.assertEqual(result.returncode, 1)
        self.assertIn("partial snapshot deleted", result.stderr)
        self.assertEqual(list(self.backups.glob("gateway-*")), [])
        self.assertTrue((self.backups / "last-failure").exists())
        self.assertFalse((self.backups / "last-success").exists())

    def test_linked_system_ancestor_like_macos_var_is_accepted(self):
        # macOS: /var -> /private/var, so the default /var/lib/openshell has a linked ancestor.
        self.make_state(Path(self.tmp.name, "private", "var", "lib", "openshell"))
        Path(self.tmp.name, "var").symlink_to(Path(self.tmp.name, "private", "var"), target_is_directory=True)
        result = self.run_backup(self.backups, self.config, Path(self.tmp.name, "var", "lib", "openshell"))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue((self.backups / "last-success").exists())

    def test_linked_state_dir_leaf_is_still_refused(self):
        real = self.make_state(Path(self.tmp.name, "real"))
        Path(self.tmp.name, "state").symlink_to(real, target_is_directory=True)
        result = self.run_backup(self.backups, self.config, Path(self.tmp.name, "state"))
        self.assertEqual(result.returncode, 1)
        self.assertEqual(list(self.backups.glob("gateway-*")), [])


if __name__ == "__main__":
    unittest.main()
