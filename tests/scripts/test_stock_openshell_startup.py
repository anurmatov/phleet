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
