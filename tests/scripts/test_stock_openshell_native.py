"""Real owned .NET preparation fixtures; no provider CLI or product daemon is executed."""
import json
import uuid
from datetime import datetime, timedelta, timezone
import os
import shutil
from unittest.mock import patch
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]


class StockNativeStartupTests(unittest.TestCase):
    def run_profile(self, uid="10001", missing="", query=False, system_path="/usr/bin:/bin", api=False, revoked=False):
        dotnet = shutil.which("dotnet")
        self.assertIsNotNone(dotnet, "real .NET is a required fixture prerequisite")
        assembly = ROOT / "src/Fleet.Agent/bin/Release/net10.0/Fleet.Agent.dll"
        self.assertTrue(assembly.is_file(), "build Release Fleet.Agent before native startup fixtures")
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            home, work, app, bin_dir = [root / part for part in ["home", "work", "app", "bin"]]
            for path in [home, work, app, bin_dir]:
                path.mkdir()
            if not api:
                (home / ".codex-host").mkdir()
                (home / ".codex-host/auth.json").write_text('{"synthetic":true}')
            agent = {"Name": "agent1", "ShortName": "agent1", "Role": "synthetic", "Model": "sol",
                     "Provider": "codex", "WorkDir": str(work), "AllowedTools": ["mcp__memory__memory_get"]}
            if api:
                agent.update({"NativeAuthMode": "api_key", "ProviderApiKeyEnvRef": "mixedCaseRef"})
            authority = app / "authority.json"
            password = app / "password"
            password.write_text("synthetic-password")
            authority.write_text(json.dumps({
                "State": "revoked" if revoked else "active", "Agent": "agent1", "SandboxId": "synthetic",
                "Epoch": str(uuid.uuid4()), "ExpiresAt": (datetime.now(timezone.utc) + timedelta(minutes=5)).isoformat(),
                "GatewayIssuer": "synthetic", "GatewayAudience": "synthetic", "GatewayJwksPath": str(app / "jwks"),
                "AssertionPrivateKeyPath": str(app / "absent-private"), "AssertionPublicKeyPath": str(app / "public"),
                "MemoryHost": "memory.example.test", "BrokerHost": "broker.example.test",
                "PolicySha256": "b" * 64, "StockCommit": "bdb956f2cd65a635f1294fccf1d138d263bf650f"}))
            (app / "appsettings.json").write_text(json.dumps({"Agent": agent, "StockOpenShell": {
                "Enabled": True, "AuthorityFile": str(authority), "EnrollmentFile": str(authority),
                "BrokerPasswordFile": str(password)}, "RabbitMq": {"Host": "broker.example.test"}}))
            # Same public cloud-root fixture asserted by HarnessDefaultsTests, not an alternate parser.
            (app / "codex-root.toml").write_text('check_for_update_on_startup = false\n\n[analytics]\nenabled = false\n\n[history]\npersistence = "none"\n')
            (work / ".mcp.json").write_text(json.dumps({"mcpServers": {"memory": {"url": "https://memory.example.test/mcp" + ("?agent=spoof" if query else "")}}}))
            (bin_dir / "id").write_text('#!/bin/sh\nprintf "%s" "$SYNTHETIC_UID"\n')
            # Execute the real preparation process; stub ONLY the later long-lived daemon.
            # A successful stub cannot substitute for a failed preparation invocation.
            (bin_dir / "dotnet").write_text(
                '#!/usr/bin/python3\nimport os, sys\nfrom pathlib import Path\n'
                'if "--prepare-stock-native" in sys.argv:\n'
                f'    os.execve({str(Path(dotnet).resolve())!r}, [{str(Path(dotnet).resolve())!r}, {str(assembly)!r}] + sys.argv[2:], os.environ)\n'
                'Path(os.environ["MARKER"]).write_text("\\n".join(sys.argv[1:]))\n')
            for path in bin_dir.iterdir():
                path.chmod(0o700)
            if missing:
                {"credential": home / ".codex-host/auth.json", "root": app / "codex-root.toml"}[missing].unlink()
            # Local root runs drop to an actual non-root UID for the real validator, not a mocked geteuid.
            identity = {}
            if os.geteuid() == 0:
                for path in [root, *root.rglob("*")]:
                    os.chown(path, 10001, 10001)
                identity = {"user": 10001, "group": 10001, "extra_groups": []}
            script = (ROOT / "stock-entrypoint.sh").read_text().replace("/app/", str(app) + "/").replace("--contentRoot /app", "--contentRoot " + str(app)).replace("APP_ROOT=/app", "APP_ROOT=" + str(app))
            env = {"PATH": str(bin_dir) + (":" + system_path if system_path else ""), "HOME": str(home),
                   "DOTNET_CLI_HOME": str(home), "SYNTHETIC_UID": uid, "MARKER": str(root / "executed")}
            if api:
                env["OPENAI_API_KEY"] = "openshell:resolve:env:v7_OPENAI_API_KEY"
            result = subprocess.run(["/bin/bash", "--noprofile", "--norc", "-c", script], cwd=app,
                                    env=env, capture_output=True, text=True, timeout=10, **identity)
            config = home / ".codex/config.toml"
            auth = home / ".codex/auth.json"
            self.assertFalse(list(home.rglob("*.tmp")))
            if result.returncode:
                self.assertFalse(config.exists(), "refusal must precede native profile writes")
                self.assertFalse(auth.exists(), "refusal must not copy credentials")
            else:
                self.assertEqual(0o700, (home / ".codex").stat().st_mode & 0o777)
                self.assertEqual(0o600, auth.stat().st_mode & 0o777)
                if api:
                    self.assertEqual({"OPENAI_API_KEY": env["OPENAI_API_KEY"]}, json.loads(auth.read_text()))
            return result, config.read_text() if config.exists() else "", (root / "executed").read_text() if (root / "executed").exists() else ""

    def test_real_dotnet_outside_system_path_is_retained(self):
        dotnet = shutil.which("dotnet")
        self.assertIsNotNone(dotnet)
        with tempfile.TemporaryDirectory() as directory:
            (Path(directory) / "dotnet").symlink_to(Path(dotnet).resolve())
            with patch.dict(os.environ, {"PATH": directory}):
                result, config, executed = self.run_profile(system_path="")
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn('[mcp_servers.memory]', config)
            self.assertIn("Fleet.Agent.dll", executed)

    def test_nonroot_profile_prepares_native_mcp_then_executes_existing_daemon(self):
        for api in [False, True]:
            with self.subTest(api=api):
                result, config, executed = self.run_profile(api=api)
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertIn('[mcp_servers.memory]', config)
                self.assertIn('enabled_tools = ["memory_get"]', config)
                self.assertNotIn("agent=", config)
                self.assertIn("Fleet.Agent.dll", executed)
                self.assertIn("--contentRoot", executed)
                self.assertEqual(api, 'forced_login_method = "api"' in config)

    def test_root_and_missing_prerequisites_never_execute(self):
        for arguments in [{"uid": "0"}, {"missing": "credential"}, {"missing": "root"}, {"query": True}, {"revoked": True}]:
            with self.subTest(arguments=arguments):
                result, _, executed = self.run_profile(**arguments)
                self.assertNotEqual(0, result.returncode)
                self.assertEqual("", executed)
                self.assertNotIn("synthetic-password", result.stderr)

    def test_managed_mode_selects_stock_profile_before_legacy_setup(self):
        prefix = (ROOT / "entrypoint.sh").read_text().split("# Read provider", 1)[0]
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            dotnet = root / "dotnet"
            dotnet.write_text('#!/bin/sh\nprintf stock\n')
            profile = root / "profile"
            profile.write_text('#!/bin/sh\nprintf selected-stock\n')
            dotnet.chmod(0o700)
            profile.chmod(0o700)
            result = subprocess.run(["/bin/bash", "--noprofile", "--norc", "-c",
                                     prefix.replace("/app/stock-entrypoint.sh", str(profile)) + '\nprintf legacy-side-effect'],
                                    env={"PATH": directory}, capture_output=True, text=True, timeout=5)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual("selected-stock", result.stdout)

    def test_unknown_managed_mode_never_continues(self):
        prefix = (ROOT / "entrypoint.sh").read_text().split("# Read provider", 1)[0]
        with tempfile.TemporaryDirectory() as directory:
            dotnet = Path(directory) / "dotnet"
            dotnet.write_text('#!/bin/sh\nprintf unknown\n')
            dotnet.chmod(0o700)
            result = subprocess.run(["/bin/bash", "--noprofile", "--norc", "-c", prefix + '\nprintf unexpected'],
                                    env={"PATH": directory}, capture_output=True, text=True, timeout=5)
            self.assertEqual(1, result.returncode)
            self.assertEqual("", result.stdout)

    def test_optional_image_target_preserves_default_legacy_user(self):
        docker = (ROOT / "Dockerfile").read_text()
        stock = docker.split("FROM agent-runtime AS stock-agent", 1)[1]
        self.assertIn("USER 10001:10001", stock.split("FROM agent-runtime AS default", 1)[0])
        self.assertEqual("", stock.split("FROM agent-runtime AS default", 1)[1].strip())


if __name__ == "__main__":
    unittest.main()
