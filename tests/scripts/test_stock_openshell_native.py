"""Owned native startup fixtures; no provider CLI or product daemon is executed."""
import json
import os
import shutil
from unittest.mock import patch
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]


class StockNativeStartupTests(unittest.TestCase):
    def run_profile(self, uid="10001", missing="", query=False, system_path="/usr/bin:/bin"):
        node = shutil.which("node")
        self.assertIsNotNone(node, "real Node is a required fixture prerequisite")
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            home = root / "home"
            work = root / "work"
            app = root / "app"
            bin_dir = root / "bin"
            for path in [home / ".codex-host", work, app, bin_dir]:
                path.mkdir(parents=True)
            (home / ".codex-host/auth.json").write_text('{"synthetic":true}')
            (app / "appsettings.json").write_text(json.dumps({"Agent": {"Provider": "codex", "WorkDir": str(work), "AllowedTools": ["mcp__memory__memory_get"]}}))
            (app / "codex-root.toml").write_text('model = "synthetic-model"\n')
            (work / ".mcp.json").write_text(json.dumps({"mcpServers": {"memory": {"url": "https://memory.example.test/mcp" + ("?agent=spoof" if query else "")}}}))
            (bin_dir / "id").write_text('#!/bin/sh\nprintf "%s" "$SYNTHETIC_UID"\n')
            (bin_dir / "dotnet").write_text('#!/bin/sh\nprintf "%s\\n" "$@" > "$MARKER"\n')
            for path in bin_dir.iterdir():
                path.chmod(0o700)
            # Retain exactly the discovered real executable, including setup-node tool-cache installs.
            # Do not inherit the ambient PATH or substitute a Node stub.
            (bin_dir / "node").symlink_to(Path(node).resolve())
            if missing:
                {"credential": home / ".codex-host/auth.json", "root": app / "codex-root.toml"}[missing].unlink()
            script = (ROOT / "stock-entrypoint.sh").read_text().replace("/app/", str(app) + "/").replace("--contentRoot /app", "--contentRoot " + str(app))
            result = subprocess.run(["/bin/bash", "--noprofile", "--norc", "-c", script], cwd=app,
                                    env={"PATH": str(bin_dir) + ":" + system_path, "HOME": str(home), "SYNTHETIC_UID": uid,
                                         "MARKER": str(root / "executed")}, capture_output=True, text=True, timeout=5)
            config = (home / ".codex/config.toml")
            return result, config.read_text() if config.exists() else "", (root / "executed").read_text() if (root / "executed").exists() else ""

    def test_real_node_outside_system_path_is_retained(self):
        node = shutil.which("node")
        self.assertIsNotNone(node, "real Node is a required fixture prerequisite")
        with tempfile.TemporaryDirectory() as directory:
            # Model setup-node's tool-cache location using the real executable, not a mock.
            (Path(directory) / "node").symlink_to(Path(node).resolve())
            with patch.dict(os.environ, {"PATH": directory}):
                result, config, executed = self.run_profile(system_path="")
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn('[mcp_servers.memory]', config)
            self.assertIn("Fleet.Agent.dll", executed)

    def test_nonroot_profile_prepares_native_mcp_then_executes_existing_daemon(self):
        result, config, executed = self.run_profile()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn('[mcp_servers.memory]', config)
        self.assertIn('enabled_tools = ["memory_get"]', config)
        self.assertNotIn("agent=", config)
        self.assertIn("Fleet.Agent.dll", executed)
        self.assertIn("--contentRoot", executed)

    def test_root_and_missing_prerequisites_never_execute(self):
        for arguments in [{"uid": "0"}, {"missing": "credential"}, {"missing": "root"}, {"query": True}]:
            with self.subTest(arguments=arguments):
                result, _, executed = self.run_profile(**arguments)
                self.assertNotEqual(0, result.returncode)
                self.assertEqual("", executed)
                self.assertNotIn("synthetic-model", result.stderr)

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
