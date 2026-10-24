"""Execute only documented path setup and verifier invocations with an owned stub, never APIs."""
import os
from pathlib import Path
import re
import subprocess
import tempfile
import unittest

DOC = Path(__file__).resolve().parents[2] / 'docs/runbooks/workflow-outcomes.md'


class RunbookPathsTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        base = Path(self.tmp.name)
        self.repo = base / 'source checkout'
        self.runtime = base / 'separate runtime'
        self.repo.mkdir()
        self.runtime.mkdir()
        self.policy = self.runtime / 'workflow-outcomes'
        self.policy.mkdir()
        examples = self.repo / 'docs/examples/workflow-outcomes'
        examples.mkdir(parents=True)
        for name in ('ownership.json', 'classifiers.json', 'recovery-approvals.json', 'qualification.json'):
            (self.policy / name).write_text('{}')
            if name in ('ownership.json', 'classifiers.json'):
                (examples / name).write_text('{}')
        helper = self.repo / 'scripts/verify-workflow-outcomes.sh'
        helper.parent.mkdir()
        helper.write_text('printf "%s\\n" "$PWD" "$WORKFLOW_OUTCOMES_POLICY_DIR" "$@" > "$CALLS"\n')
        self.doc = DOC.read_text()
        block = re.search(r'```bash\n(.*?)```', self.doc, re.S).group(1)
        self.bootstrap = block.split('export ORCH=')[0]
        self.bootstrap = self.bootstrap.replace('/absolute/path/to/phleet', str(self.repo))
        self.bootstrap = self.bootstrap.replace('/absolute/path/to/runtime', str(self.runtime))
        self.env = {**os.environ, 'CALLS': str(base / 'calls')}
        # Do not inherit any prior operator roots or policy location.
        for key in ('REPO_ROOT', 'RUNTIME_ROOT', 'WORKFLOW_OUTCOMES_POLICY_DIR'):
            self.env.pop(key, None)

    def tearDown(self):
        self.tmp.cleanup()

    def run_paths(self, command):
        result = subprocess.run(['bash', '-euc', self.bootstrap + '\n' + command], cwd=self.repo,
                                env=self.env, capture_output=True, text=True, timeout=5)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        return result

    def test_plan_after_runtime_cd_resolves_checkout_helper_and_runtime_policies(self):
        command = re.search(r'`(bash [^`\n]*verify-workflow-outcomes\.sh"? plan --namespace namespace-a)`', self.doc).group(1)
        self.run_paths(command)
        self.assertEqual([str(self.runtime), str(self.policy), 'plan', '--namespace', 'namespace-a'],
                         Path(self.env['CALLS']).read_text().splitlines())

    def test_example_and_both_policy_paths_resolve_from_separate_runtime(self):
        example = re.search(r'`(\$REPO_ROOT/docs/examples/workflow-outcomes/)`', self.doc).group(1)
        for name in ('ownership.json', 'classifiers.json', 'recovery-approvals.json'):
            with self.subTest(name=name):
                path = re.search(r'`(\$WORKFLOW_OUTCOMES_POLICY_DIR/' + re.escape(name) + r')`', self.doc).group(1)
                check_example = f'test -f "{example}{name}"\n' if name != 'recovery-approvals.json' else ''
                result = self.run_paths(check_example + f'test -f "{path}"\nprintf "%s\\n" "{path}"')
                self.assertEqual(str(self.policy / name), result.stdout.strip())

    def test_verify_manifest_path_resolves_beside_runtime_policies(self):
        command = re.search(r'`(bash [^`\n]*verify-workflow-outcomes\.sh"? verify [^`\n]+)`', self.doc).group(1)
        command = command.replace('<https-url>', 'https://example.com/approval')
        self.run_paths(command)
        self.assertEqual([str(self.runtime), str(self.policy), 'verify', '--manifest',
                          str(self.policy / 'qualification.json'), '--approval', 'https://example.com/approval'],
                         Path(self.env['CALLS']).read_text().splitlines())


if __name__ == '__main__':
    unittest.main()
