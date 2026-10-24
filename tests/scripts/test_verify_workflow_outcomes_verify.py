"""Run the actual verifier in an owned tree; Docker, Git and the runner are isolated stubs."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[2] / 'scripts' / 'verify-workflow-outcomes.sh'


class VerifyProvenanceTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        (self.root / 'scripts').mkdir()
        shutil.copyfile(SCRIPT, self.root / 'scripts' / SCRIPT.name)
        runner = self.root / 'tests/integration/workflow-outcomes/run.sh'
        runner.parent.mkdir(parents=True)
        runner.write_text('printf called >> "$RUNNER_CALLS"\nexit 0\n')
        self.bin = self.root / 'bin'
        self.bin.mkdir()
        for tool in ('bash', 'dirname', 'mktemp', 'grep'):
            (self.bin / tool).symlink_to(shutil.which(tool))
        self.stub('sleep', '#!/bin/bash\nexec ' + shutil.which('sleep') + ' 0.01\n')
        self.stub('docker', '#!/bin/bash\nprintf "sha256:%s\\n" "$IMAGE_HASH"\n')
        self.stub('git', '''#!/bin/bash
case "$*" in
  *rev-parse*) printf '%s\\n' "$TEST_HEAD"; exit "$HEAD_RC" ;;
  *status*) printf '%s' "$TEST_STATUS"; exit "$STATUS_RC" ;;
  *) exit 97 ;;
esac
''')
        # Deterministic PermissionError even when tests run as root; only the owned file is denied.
        self.stub('python3', f'''#!{sys.executable}
import builtins, os, sys
real_open = builtins.open
def guarded_open(path, *args, **kwargs):
    if os.fspath(path) == os.environ.get('UNREADABLE_POLICY'):
        raise PermissionError('owned synthetic denial')
    return real_open(path, *args, **kwargs)
builtins.open = guarded_open
exec(compile(sys.stdin.read(), '<actual-verifier>', 'exec'))
''')
        self.env = {**os.environ, 'PATH': str(self.bin), 'TMPDIR': str(self.root),
                    'RUNNER_CALLS': str(self.root / 'runner-calls'), 'IMAGE_HASH': 'a' * 64,
                    'TEST_HEAD': 'b' * 40, 'HEAD_RC': '0', 'TEST_STATUS': '', 'STATUS_RC': '0',
                    'FLEET_ORCHESTRATOR_MIGRATION_CONNECTION': 'Server=woq-test-mysql;',
                    'FLEET_WORKFLOW_OUTCOMES_NAMESPACES': '', 'FLEET_CTO_AGENT': '',
                    'UNREADABLE_POLICY': ''}
        self.manifest = {
            'schemaVersion': 1, 'kind': 'workflow-outcomes-qualification',
            'approvalRef': 'https://example.com/approval', 'resourcePrefix': 'woq-test-',
            'namespaces': ['woq-test-a'], 'taskQueues': ['woq-test-queue'],
            'coordinatorClone': {'name': 'woq-test-coordinator', 'cloneOf': 'coordinator'},
            'database': {'connectionEnv': 'FLEET_ORCHESTRATOR_MIGRATION_CONNECTION', 'server': 'woq-test-mysql'},
            'maxDurationSeconds': 60,
            'images': {'orchestrator': 'fixture:orchestrator', 'agent': 'fixture:agent', 'temporalBridge': 'fixture:bridge'},
            'configFiles': ['ownership.json', 'classifiers.json'],
            'cleanup': {'verify': True, 'owned': ['woq-test-a', 'woq-test-queue', 'woq-test-coordinator', 'woq-test-mysql']},
        }
        for name in self.manifest['configFiles']:
            (self.root / name).write_text('{}')

    def tearDown(self):
        self.tmp.cleanup()

    def stub(self, name, text):
        path = self.bin / name
        path.write_text(text)
        path.chmod(0o755)

    def verify(self):
        path = self.root / 'manifest.json'
        path.write_text(json.dumps(self.manifest))
        return subprocess.run([str(self.bin / 'bash'), str(self.root / 'scripts' / SCRIPT.name),
            'verify', '--manifest', str(path), '--approval', self.manifest['approvalRef']],
            env=self.env, capture_output=True, text=True, timeout=10)

    def refused(self):
        result = self.verify()
        self.assertEqual(3, result.returncode, result.stdout + result.stderr)
        self.assertFalse(Path(self.env['RUNNER_CALLS']).exists(), result.stdout + result.stderr)

    def test_missing_each_image_role_refuses(self):
        for role in ('orchestrator', 'agent', 'temporalBridge'):
            with self.subTest(role=role):
                ref = self.manifest['images'].pop(role)
                self.refused()
                self.manifest['images'][role] = ref

    def test_missing_each_policy_refuses(self):
        for name in ('ownership.json', 'classifiers.json'):
            with self.subTest(name=name):
                self.manifest['configFiles'] = [n for n in ('ownership.json', 'classifiers.json') if n != name]
                self.refused()

    def test_missing_image_map_or_policy_list_refuses(self):
        for key in ('images', 'configFiles'):
            with self.subTest(key=key):
                value = self.manifest.pop(key)
                self.refused()
                self.manifest[key] = value

    def test_absent_policy_file_refuses(self):
        for name in ('ownership.json', 'classifiers.json'):
            with self.subTest(name=name):
                path = self.root / name
                path.unlink()
                self.refused()
                path.write_text('{}')

    def test_unreadable_policy_refuses(self):
        for name in ('ownership.json', 'classifiers.json'):
            with self.subTest(name=name):
                self.env['UNREADABLE_POLICY'] = str(self.root / name)
                self.refused()

    def test_failed_head_even_with_valid_stdout_refuses(self):
        self.env['HEAD_RC'] = '1'
        self.refused()

    def test_invalid_or_missing_head_refuses(self):
        for head in ('', 'unknown', 'b' * 39):
            with self.subTest(head=head):
                self.env['TEST_HEAD'] = head
                self.refused()

    def test_failed_status_even_with_clean_stdout_refuses(self):
        self.env['STATUS_RC'] = '1'
        self.refused()

    def test_dirty_status_refuses(self):
        self.env['TEST_STATUS'] = ' M synthetic.txt'
        self.refused()

    def test_scope_time_and_cleanup_refusals_remain(self):
        self.manifest['maxDurationSeconds'] = 0
        self.refused()
        self.manifest['maxDurationSeconds'] = 60
        self.manifest['cleanup']['owned'].remove('woq-test-a')
        self.refused()
        self.manifest['cleanup']['owned'].append('woq-test-a')
        self.manifest['namespaces'] = ['default']
        self.refused()

    def test_complete_valid_clean_provenance_reaches_only_stub_runner(self):
        result = self.verify()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual('called', Path(self.env['RUNNER_CALLS']).read_text())
        provenance = json.loads(next(self.root.glob('wo-qualification.*/provenance.json')).read_text())
        self.assertEqual('b' * 40, provenance['head'])
        self.assertIs(False, provenance['dirty'])
        self.assertEqual({role: {'ref': ref, 'id': 'sha256:' + 'a' * 64}
                          for role, ref in self.manifest['images'].items()}, provenance['images'])
        self.assertEqual({name: hashlib.sha256(b'{}').hexdigest() for name in self.manifest['configFiles']},
                         provenance['configFiles'])


if __name__ == '__main__':
    unittest.main()
