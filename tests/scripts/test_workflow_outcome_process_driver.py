"""Owned source oracles, not daemon/native/API/SQL/broker runtime qualification."""
import json
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
PROJECT = REPO / 'src/Fleet.WorkflowOutcomeQualification/Fleet.WorkflowOutcomeQualification.csproj'
DLL = PROJECT.parent / 'bin/Debug/net10.0/Fleet.WorkflowOutcomeQualification.dll'


class ProcessQualificationDriverTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.owned = tempfile.TemporaryDirectory(prefix='outcome-driver-source-')
        cls.addClassCleanup(cls.owned.cleanup)
        root = Path(cls.owned.name)
        for name in ('home', 'cache', 'tmp'):
            (root / name).mkdir()
        cls.environment = {
            'PATH': '/usr/bin:/bin',
            'HOME': str(root / 'home'),
            'DOTNET_CLI_HOME': str(root / 'home'),
            'XDG_CACHE_HOME': str(root / 'cache'),
            'TMPDIR': str(root / 'tmp'),
            'DOTNET_CLI_TELEMETRY_OPTOUT': '1',
            'DOTNET_SKIP_FIRST_TIME_EXPERIENCE': '1',
            'DOTNET_NOLOGO': '1',
        }
        # No external package references; use an empty feed, never the caller's NuGet config.
        cls.dotnet = shutil.which('dotnet')
        if cls.dotnet is None:
            raise RuntimeError('dotnet SDK is required; zero tests is not acceptance')
        built = subprocess.run(
            [cls.dotnet, 'build', str(PROJECT), '-nologo', '-v', 'q', '--source', str(root / 'cache')],
            cwd=REPO, env=cls.environment, text=True, capture_output=True, timeout=120)
        if built.returncode != 0:
            raise AssertionError(built.stdout + built.stderr)

    def run_driver(self, *args):
        return subprocess.run([self.dotnet, str(DLL), *args], cwd=REPO, env=self.environment,
                              text=True, capture_output=True, timeout=30)

    def test_self_test_covers_guards_fixture_and_scenario_assertions(self):
        result = self.run_driver('self-test')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        summary = json.loads(result.stdout.strip().splitlines()[-1])
        self.assertEqual(summary['kind'], 'workflow-outcome-process-self-test')
        self.assertEqual(summary['total'], 88)
        self.assertEqual(summary['passed'], 88)
        self.assertEqual(summary['failed'], 0)
        self.assertEqual(summary['skipped'], 0)
        self.assertEqual(sum(line.startswith('PASS ') for line in result.stdout.splitlines()), 88)

    def test_actual_configuration_provider_refuses_alias_ambiguity(self):
        project = REPO / 'tests/Fleet.WorkflowOutcomeQualification.SourceTests'
        built = subprocess.run(
            [self.dotnet, 'build', str(project), '-nologo', '-v', 'q',
             '--source', str(Path(self.owned.name) / 'cache')],
            cwd=REPO, env=self.environment, text=True, capture_output=True, timeout=120)
        self.assertEqual(built.returncode, 0, built.stdout + built.stderr)
        dll = project / 'bin/Debug/net10.0/Fleet.WorkflowOutcomeQualification.SourceTests.dll'
        result = subprocess.run([self.dotnet, str(dll)], cwd=REPO, env=self.environment,
                                text=True, capture_output=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        summary = json.loads(result.stdout.strip().splitlines()[-1])
        self.assertEqual(summary, {'total': 17, 'passed': 17, 'failed': 0, 'skipped': 0})

    def test_validate_refuses_an_empty_manifest_before_any_process(self):
        manifest = Path(self.owned.name) / 'empty.json'
        manifest.write_text('{}', encoding='utf-8')
        result = self.run_driver('validate', '--manifest', str(manifest))
        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
        self.assertIn('REFUSED', result.stderr)
        self.assertIn('schemaVersion', result.stderr)

    def test_unknown_arguments_fail_before_manifest_or_network(self):
        result = self.run_driver('validate', '--foreign-target', 'synthetic')
        self.assertEqual(result.returncode, 2)
        self.assertIn('unknown argument', result.stderr)


if __name__ == '__main__':
    unittest.main()
