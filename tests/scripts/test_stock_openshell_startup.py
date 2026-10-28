"""Synthetic entrypoint-prefix tests; no product service or dependency is started."""
import os
from pathlib import Path
import subprocess
import shlex
import re
import shutil
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]


class StockStartupTests(unittest.TestCase):
    def test_native_preparation_refusal_never_starts_agent(self):
        script = (ROOT / "stock-entrypoint.sh").read_text()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "id").write_text("#!/bin/sh\nprintf 10001")
            (root / "dotnet").write_text('#!/bin/sh\nprintf "%s\\n" "$*" >> "$MARKER"\nexit 1\n')
            (root / "id").chmod(0o700)
            (root / "dotnet").chmod(0o700)
            result = subprocess.run(["/bin/bash", "--noprofile", "--norc", "-c", script],
                env={"PATH": directory, "HOME": directory, "MARKER": str(root / "calls")},
                capture_output=True, text=True, timeout=5)
            self.assertEqual(1, result.returncode)
            self.assertEqual(["/app/Fleet.Agent.dll --contentRoot /app --prepare-stock-native"],
                (root / "calls").read_text().splitlines())
            self.assertEqual("", result.stdout)

    def test_actual_preparation_command_cannot_fall_back_to_ordinary_start(self):
        assembly = ROOT / "src/Fleet.Agent/bin/Release/net10.0/Fleet.Agent.dll"
        self.assertTrue(assembly.is_file(), "build Release Fleet.Agent first")
        with tempfile.TemporaryDirectory() as directory:
            result = subprocess.run(["dotnet", str(assembly), "--contentRoot", directory, "--prepare-stock-native"],
                env={"PATH": "/usr/bin:/bin", "HOME": directory, "DOTNET_CLI_HOME": directory},
                cwd=directory, capture_output=True, text=True, timeout=10)
            self.assertEqual(1, result.returncode)
            self.assertEqual("", result.stdout)
            self.assertEqual("stock_enrollment_native_preparation_disabled", result.stderr.strip())
            self.assertFalse((Path(directory) / ".codex").exists())

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

    def test_restrictive_context_startup_scripts_get_explicit_read_execute_modes(self):
        docker = (ROOT / "Dockerfile").read_text()
        runtime, stock = docker.split("FROM agent-runtime AS stock-agent", 1)
        stock = stock.split("FROM agent-runtime AS default", 1)[0]
        scripts = ["entrypoint.sh", "stock-entrypoint.sh", "gh-auth.sh"]
        # Replay only the actual chmod instructions on owned copies, never startup.
        with tempfile.TemporaryDirectory() as directory:
            app = Path(directory)
            for name in scripts:
                shutil.copyfile(ROOT / name, app / name)
                (app / name).chmod(0o600)
            for stage in [runtime, stock.split("USER 10001:10001", 1)[0]]:
                for command in re.findall(r"^RUN (chmod [^\n]+)", stage, re.MULTILINE):
                    subprocess.run(shlex.split(command.replace("/app/", str(app) + "/")), check=True)
            for name in scripts:
                with self.subTest(script=name):
                    self.assertEqual(0o755, (app / name).stat().st_mode & 0o777)

    def test_restrictive_context_public_artifacts_get_readable_modes(self):
        stock = (ROOT / "Dockerfile").read_text().split("FROM agent-runtime AS stock-agent", 1)[1]
        commands = re.findall(r"^RUN (find /app/skills[^\n]+)", stock.replace("\\\n", ""), re.MULTILINE)
        self.assertEqual(1, len(commands), "stock-only public artifact mode normalization required")
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            skills = root / "app/skills"
            shutil.copytree(ROOT / "skills", skills)
            helper = root / "opt/fleet/acceptance_plan.py"
            helper.parent.mkdir(parents=True)
            shutil.copyfile(ROOT / "scripts/acceptance_plan.py", helper)
            paths = [skills, *skills.rglob("*"), helper.parent, helper]
            for path in paths:
                path.chmod(0o700 if path.is_dir() else 0o600)
            command = commands[0].replace("/app/", str(root / "app") + "/").replace("/opt/", str(root / "opt") + "/")
            subprocess.run(["/bin/sh", "-ec", command], check=True)
            for path in paths:
                with self.subTest(path=path.relative_to(root)):
                    self.assertEqual(0o755 if path.is_dir() else 0o644, path.stat().st_mode & 0o777)

    def test_stock_build_and_ci_check_public_artifact_access(self):
        stock = (ROOT / "Dockerfile").read_text().split("USER 10001:10001", 1)[1].split("FROM agent-runtime AS default", 1)[0]
        ci = (ROOT / ".github/workflows/ci.yml").read_text().split("  agent-image:", 1)[1].split("  minio-init-dependencies:", 1)[0]
        for guard in [stock, ci]:
            for check in ['test -r /app/skills/fleet-review/SKILL.md',
                          'find /app/skills -type f -exec cat {} +',
                          'test -r /opt/fleet/acceptance_plan.py',
                          'python3 /opt/fleet/acceptance_plan.py --help']:
                self.assertIn(check, guard)
        for check in ['find skills -type d -exec chmod 0700 {} +',
                      'find skills -type f -exec chmod 0600 {} +',
                      'chmod 0600 scripts/acceptance_plan.py']:
            self.assertIn(check, ci)

    def test_stock_build_checks_all_startup_scripts_as_runtime_user(self):
        stock = (ROOT / "Dockerfile").read_text().split("FROM agent-runtime AS stock-agent", 1)[1]
        guard = stock.split("USER 10001:10001", 1)[1].split("FROM agent-runtime AS default", 1)[0]
        for name in ["entrypoint.sh", "stock-entrypoint.sh", "gh-auth.sh"]:
            self.assertIn("/app/" + name, guard)
        for check in ['test "$(id -u)" = 10001', 'test "$(id -g)" = 10001',
                      'test -r "$script"', 'test -x "$script"', 'bash -n "$script"']:
            self.assertIn(check, guard)

    def test_ci_qualifies_both_image_profiles_without_network(self):
        ci = (ROOT / ".github/workflows/ci.yml").read_text().split("  agent-image:", 1)[1].split("  minio-init-dependencies:", 1)[0]
        self.assertIn("docker build --target stock-agent -t fleet:stock-agent-ci .", ci)
        self.assertIn("chmod 0600 entrypoint.sh stock-entrypoint.sh gh-auth.sh", ci)
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
        for name in ["entrypoint.sh", "stock-entrypoint.sh", "gh-auth.sh"]:
            self.assertIn("/app/" + name, ci)
        for check in ['test -r "$script"', 'test -x "$script"', 'bash -n "$script"']:
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
