"""Run the actual setup path twice with owned env files and isolated external-client stubs."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
DEFAULTS = {
    'FLEET_WORKFLOW_OUTCOMES_ENABLED': 'false', 'FLEET_WORKFLOW_OUTCOMES_NAMESPACES': '',
    'FLEET_WORKFLOW_OUTCOMES_POLL_SECONDS': '30', 'FLEET_WORKFLOW_OUTCOMES_RECENT_SCAN_SECONDS': '60',
    'FLEET_WORKFLOW_OUTCOMES_FULL_SCAN_SECONDS': '300', 'FLEET_WORKFLOW_OUTCOMES_BATCH_SECONDS': '30',
    'FLEET_WORKFLOW_OUTCOMES_BATCH_SIZE': '20',
    'FLEET_WORKFLOW_OUTCOMES_OWNERS_FILE': '/etc/phleet/workflow-outcomes/ownership.json',
    'FLEET_WORKFLOW_OUTCOMES_CLASSIFIERS_FILE': '/etc/phleet/workflow-outcomes/classifiers.json',
    'FLEET_WORKFLOW_OUTCOMES_RECOVERY_APPROVALS_FILE': '/etc/phleet/workflow-outcomes/recovery-approvals.json',
    'FLEET_WORKFLOW_OUTCOMES_KEYRING_DIR': '/var/lib/phleet/workflow-outcomes/keys',
    'FLEET_WORKFLOW_OUTCOMES_COORDINATOR_TOKEN_KEY': 'FLEET_WORKFLOW_OUTCOMES_COORDINATOR_TOKEN',
    'FLEET_WORKFLOW_OUTCOMES_COORDINATOR_TOKEN': '',
    'FLEET_WORKFLOW_OUTCOMES_COORDINATOR_CREDENTIAL_VERSION': '1',
    'FLEET_WORKFLOW_OUTCOMES_COORDINATOR_GENERATION': '1',
    'FLEET_WORKFLOW_OUTCOMES_COORDINATOR_BASE_URL': '',
    'FLEET_WORKFLOW_OUTCOMES_LATE_CAPTURE_ENABLED': 'false',
}


class SetupWorkflowDefaultsTests(unittest.TestCase):
    def run_setup_twice(self, newline, configured):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for name in ('setup.sh', '.env.example', 'docker-compose.example.yml', 'seed.example.json'):
                shutil.copyfile(ROOT / name, root / name)
            (root / 'scripts/lib').mkdir(parents=True)
            shutil.copyfile(ROOT / 'scripts/lib/comms-profiles.sh', root / 'scripts/lib/comms-profiles.sh')
            fleet, home, bin_dir = root / 'fleet', root / 'home', root / 'bin'
            fleet.mkdir()
            (home / '.codex').mkdir(parents=True)
            (home / '.codex/auth.json').write_text('{}')  # synthetic fixture; jq is a stub
            bin_dir.mkdir()
            for tool in ('bash', 'dirname', 'basename', 'grep', 'head', 'cut', 'sed', 'mkdir', 'chmod',
                         'cp', 'install', 'ln', 'tail', 'tr', 'date', 'cat', 'rm', 'mktemp', 'awk'):
                (bin_dir / tool).symlink_to(shutil.which(tool))
            def stub(name, body):
                path = bin_dir / name
                path.write_text('#!/bin/bash\n' + body)
                path.chmod(0o755)
            stub('docker', '''printf '%s\\n' "$*" >> "$CALLS"
case "$*" in info|'compose version'|'network inspect fleet-net') exit 0 ;; *) exit 97 ;; esac
''')
            stub('jq', '''case "$*" in *tokens.refresh_token*) printf 'fixture-refresh\\n' ;; *tokens.id_token*) : ;; *) exit 97 ;; esac
''')
            for name in ('curl', 'openssl', 'git', 'npm', 'security'):
                stub(name, 'exit 97\n')
            original = {
                'FLEET_BASE_DIR': str(fleet), 'FLEET_CTO_AGENT': 'coordinator', 'FLEET_COMMS_ENABLED': 'false',
                'ORCHESTRATOR_AUTH_TOKEN': 'fixture-auth', 'ORCHESTRATOR_CONFIG_TOKEN': 'fixture-config',
                'MINIO_ACCESS_KEY': 'fixture-access', 'MINIO_SECRET_KEY': 'fixture-secret',
                'FLEET_MYSQL_ROOT_PASSWORD': 'fixture-root', 'FLEET_MYSQL_PASSWORD': 'fixture-db',
                'FLEET_MEMORY_EMBEDDING_PROVIDER': 'onnx', 'FLEET_MEMORY_EMBEDDING_DIMENSIONS': '384',
                'FLEET_EPIC_WATCHER_ENABLED': 'false', 'FLEET_EPIC_WATCHER_INTERVAL_SECONDS': '321',
                'FLEET_EPIC_GRANTS_ENABLED': 'false', 'FLEET_EPIC_GRANTS_MAX_DAYS': '7',
                'FLEET_EPIC_GRANTS_DENIED_REPOS': 'owner/repo', 'FLEET_AUTONOMY_ENABLED': 'false',
                **configured, 'UNRELATED_FINAL_VALUE': 'preserve-this-value',
            }
            env_file = fleet / '.env'
            initial = '\n'.join(f'{key}={value}' for key, value in original.items()) + ('\n' if newline else '')
            env_file.write_text(initial)
            env = {**os.environ, 'HOME': str(home), 'PATH': str(bin_dir), 'CALLS': str(root / 'calls'), 'OSTYPE': 'linux-gnu'}
            after_first = None
            for attempt in range(2):
                result = subprocess.run([str(bin_dir / 'bash'), str(root / 'setup.sh'), '--skip-build', '--skip-services'],
                    input='2\n\n1\n', env=env, capture_output=True, text=True, timeout=20)
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                data = env_file.read_bytes()
                lines = data.decode().splitlines()
                values = dict(line.split('=', 1) for line in lines)
                for key, value in {**DEFAULTS, **original}.items():
                    self.assertEqual(value, values.get(key), key)
                    self.assertEqual(1, sum(line.startswith(key + '=') for line in lines), key)
                if attempt == 0:
                    after_first = data
                else:
                    self.assertEqual(after_first, data)
            self.assertEqual(['info', 'compose version', 'network inspect fleet-net'] * 2,
                             (root / 'calls').read_text().splitlines())

    def test_missing_workflow_defaults_after_unterminated_unrelated_value(self):
        self.run_setup_twice(False, {})

    def test_configured_values_survive_partial_backfill_without_newline(self):
        self.run_setup_twice(False, {'FLEET_WORKFLOW_OUTCOMES_POLL_SECONDS': '77',
                                    'FLEET_WORKFLOW_OUTCOMES_NAMESPACES': 'namespace-a'})

    def test_already_terminated_file_remains_idempotent(self):
        self.run_setup_twice(True, {})


if __name__ == '__main__':
    unittest.main()
