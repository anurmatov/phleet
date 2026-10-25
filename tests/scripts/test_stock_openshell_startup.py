"""Synthetic entrypoint-prefix tests; no product service or dependency is started."""
import os
from pathlib import Path
import subprocess
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
                             ["/app/Fleet.Agent.dll", "--validate-stock-enrollment"])

    def test_padded_true_actual_validator_refuses_before_entrypoint_continues(self):
        # Requires the named Fleet.Agent build first; never substitutes a fake validator.
        assembly = ROOT / "src/Fleet.Agent/bin/Debug/net10.0/Fleet.Agent.dll"
        self.assertTrue(assembly.is_file(), "build Fleet.Agent before this source fixture")
        guard = (ROOT / "entrypoint.sh").read_text().split("# Read provider", 1)[0]
        guard = guard.replace("/app/Fleet.Agent.dll", str(assembly))
        for value in ["true", " true", "true ", " true "]:
            with self.subTest(value=value), tempfile.TemporaryDirectory() as directory:
                env = {"PATH": "/usr/bin:/bin", "HOME": directory, "DOTNET_CLI_HOME": directory,
                       "StockOpenShell__Enabled": value}
                result = subprocess.run(["/bin/bash", "--noprofile", "--norc", "-c",
                                         guard + '\nprintf unexpected-side-effect'],
                                        cwd=directory, env=env, capture_output=True, text=True, timeout=10)
                self.assertEqual(result.returncode, 1)
                self.assertEqual(result.stdout, "")
                self.assertEqual(result.stderr.strip(), "stock_enrollment_file_missing")

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
