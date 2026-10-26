"""Synthetic entrypoint-prefix tests; no product service or dependency is started."""
import os
from pathlib import Path
import subprocess
import shlex
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]


class StockStartupTests(unittest.TestCase):
    def test_enrollment_refusal_precedes_all_existing_side_effects(self):
        text = (ROOT / "entrypoint.sh").read_text()
        guard = text.split("# Read provider", 1)[0]
        self.assertIn("--validate-stock-enrollment", guard)
        with tempfile.TemporaryDirectory() as directory:
            dotnet = Path(directory) / "dotnet"
            marker = Path(directory) / "called"
            dotnet.write_text('#!/bin/sh\nprintf "%s\\n" "$@" > "$MARKER"\nexit 1\n')
            dotnet.chmod(0o700)
            result = subprocess.run(["/bin/bash", "--noprofile", "--norc", "-c", guard + '\necho unexpected-success'],
                                    env={"PATH": directory, "MARKER": str(marker)},
                                    capture_output=True, text=True, timeout=5)
            self.assertEqual(result.returncode, 1)
            self.assertEqual(result.stdout, "")
            self.assertEqual(marker.read_text().splitlines(),
                             ["/app/Fleet.Agent.dll", "--contentRoot", "/app", "--validate-stock-enrollment"])

    def test_padded_true_actual_validator_refuses_before_entrypoint_continues(self):
        # CI builds Release before Python discovery; never substitutes a fake validator.
        assembly = ROOT / "src/Fleet.Agent/bin/Release/net10.0/Fleet.Agent.dll"
        self.assertTrue(assembly.is_file(), "build Fleet.Agent with --configuration Release before this source fixture")
        guard = (ROOT / "entrypoint.sh").read_text().split("# Read provider", 1)[0]
        guard = guard.replace("/app/Fleet.Agent.dll", str(assembly))
        for value in ["true", " true", "true ", " true "]:
            with self.subTest(value=value), tempfile.TemporaryDirectory() as directory:
                canonical = Path(directory) / "canonical"
                canonical.mkdir()
                command = guard.replace("--contentRoot /app", "--contentRoot " + shlex.quote(str(canonical)))
                env = {"PATH": "/usr/bin:/bin", "HOME": directory, "DOTNET_CLI_HOME": directory,
                       "StockOpenShell__Enabled": value}
                result = subprocess.run(["/bin/bash", "--noprofile", "--norc", "-c",
                                         command + '\nprintf unexpected-side-effect'],
                                        cwd=directory, env=env, capture_output=True, text=True, timeout=10)
                self.assertEqual(result.returncode, 1)
                self.assertEqual(result.stdout, "")
                self.assertEqual(result.stderr.strip(), "stock_enrollment_file_missing")

    def test_canonical_configuration_wins_over_unrelated_cwd(self):
        import json
        assembly = ROOT / "src/Fleet.Agent/bin/Release/net10.0/Fleet.Agent.dll"
        self.assertTrue(assembly.is_file(), "build Release Fleet.Agent first")
        prefix = (ROOT / "entrypoint.sh").read_text().split("# Read provider", 1)[0]
        for mode in ["enabled", "disabled", "revoked"]:
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                canonical, cwd = root / "canonical", root / "unrelated"
                canonical.mkdir(); cwd.mkdir()
                authority = canonical / "authority.json"
                authority.write_text(json.dumps({"Agent": "agent1", "State": "revoked"}))
                stock = {"Enabled": mode != "disabled"}
                if mode == "revoked":
                    stock.update({"EnrollmentFile": str(authority), "AuthorityFile": str(authority)})
                (canonical / "appsettings.json").write_text(json.dumps({"StockOpenShell": stock}))
                (cwd / "appsettings.json").write_text(json.dumps({"StockOpenShell": {"Enabled": mode == "disabled"}}))
                command = prefix.replace("/app/Fleet.Agent.dll", str(assembly)).replace("--contentRoot /app", "--contentRoot " + str(canonical))
                result = subprocess.run(["/bin/bash", "--noprofile", "--norc", "-c", command + '\nprintf continued'],
                    cwd=cwd, env={"PATH": "/usr/bin:/bin", "HOME": directory, "DOTNET_CLI_HOME": directory},
                    capture_output=True, text=True, timeout=10)
                if mode == "disabled":
                    self.assertEqual(0, result.returncode, result.stderr)
                    self.assertEqual("continued", result.stdout)
                else:
                    self.assertEqual(1, result.returncode)
                    self.assertEqual("", result.stdout)
                    expected = "stock_enrollment_file_missing" if mode == "enabled" else "stock_enrollment_profile_invalid"
                    self.assertEqual(expected, result.stderr.strip())

    def test_stock_workspace_is_owned_and_default_image_unchanged(self):
        docker = (ROOT / "Dockerfile").read_text()
        runtime, stock = docker.split("FROM agent-runtime AS stock-agent", 1)
        stock, default = stock.split("FROM agent-runtime AS default", 1)
        self.assertEqual("WORKDIR /app", [line for line in runtime.splitlines() if line.startswith("WORKDIR ")][-1])
        self.assertIn("WORKDIR /home/agent", stock)
        self.assertIn('test -w "$PWD"', stock)
        self.assertIn("test ! -w /app", stock)
        self.assertIn("test -r /app/Fleet.Agent.dll", stock)
        self.assertEqual("", default.strip())

    def test_ci_qualifies_both_image_profiles_without_network(self):
        ci = (ROOT / ".github/workflows/ci.yml").read_text().split("  agent-image:", 1)[1].split("  minio-init-dependencies:", 1)[0]
        self.assertIn("docker build --target stock-agent -t fleet:stock-agent-ci .", ci)
        self.assertIn("{{.Config.User}}", ci)
        self.assertIn("{{.Config.WorkingDir}}", ci)
        self.assertIn('= "10001:10001"', ci)
        self.assertIn('= "/home/agent"', ci)
        self.assertIn('= "/app"', ci)
        self.assertIn("docker run --rm --network none", ci)
        for check in ['test "$(id -u)" = 10001', 'test "$(id -g)" = 10001',
                      'test ! -w /app', 'test -r /app/Fleet.Agent.dll',
                      'for directory in "$PWD" /workspace', 'cmp "$probe" "$probe.copy"']:
            self.assertIn(check, ci)

    def test_disabled_validation_continues_existing_entrypoint(self):
        guard = (ROOT / "entrypoint.sh").read_text().split("# Read provider", 1)[0]
        with tempfile.TemporaryDirectory() as directory:
            dotnet = Path(directory) / "dotnet"
            dotnet.write_text("#!/bin/sh\nexit 0\n")
            dotnet.chmod(0o700)
            result = subprocess.run(["/bin/bash", "--noprofile", "--norc", "-c", guard + '\nprintf continued'],
                                    env={"PATH": directory}, capture_output=True, text=True, timeout=5)
            self.assertEqual(result.returncode, 0)
            self.assertEqual(result.stdout, "continued")


if __name__ == "__main__":
    unittest.main()
