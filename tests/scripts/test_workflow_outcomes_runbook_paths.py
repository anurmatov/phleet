"""Exercise documented paths and the upgrade guard in owned directories, never live APIs."""
import os
from pathlib import Path
import re
import shutil
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

    def upgrade_command(self):
        needle = 'bash "$REPO_ROOT/upgrade.sh"'
        for block in re.findall(r'```bash\n(.*?)```', self.doc, re.S):
            if needle in block:
                return block
        # Also reproduce the prior inline command before the guard is documented.
        self.assertIn('`' + needle + '`', self.doc)
        return needle

    def test_documented_upgrade_refuses_separate_runtime_before_actual_script(self):
        source = DOC.parents[2]
        shutil.copyfile(source / 'upgrade.sh', self.repo / 'upgrade.sh')
        (self.repo / 'scripts/lib').mkdir(parents=True)
        shutil.copyfile(source / 'scripts/lib/comms-profiles.sh', self.repo / 'scripts/lib/comms-profiles.sh')
        other_runtime = self.repo / 'fleet'
        other_runtime.mkdir()
        other_env = other_runtime / '.env'
        other_env.write_text('UNRELATED_ONLY=existing-installation\n')
        before = other_env.read_bytes()
        bin_dir = self.repo / 'bin'
        bin_dir.mkdir()
        docker = bin_dir / 'docker'
        docker.write_text('#!/bin/bash\nprintf called > "$CALLS"\nexit 97\n')
        docker.chmod(0o755)
        env = {**self.env, 'PATH': str(bin_dir) + os.pathsep + os.environ['PATH']}
        result = subprocess.run(['bash', '-euc', self.bootstrap + '\n' + self.upgrade_command()],
                                cwd=self.repo, env=env, capture_output=True, text=True, timeout=5)
        self.assertEqual(3, result.returncode, result.stdout + result.stderr)
        self.assertIn('REFUSED:', result.stderr)
        self.assertFalse(Path(self.env['CALLS']).exists())
        self.assertEqual(before, other_env.read_bytes())

    def test_documented_upgrade_delegates_for_default_runtime_only(self):
        default = self.repo / 'fleet'
        default.mkdir()
        self.bootstrap = self.bootstrap.replace(str(self.runtime), str(default))
        # Positive guard/delegation proof only, not an upgrade/runtime qualification.
        (self.repo / 'upgrade.sh').write_text('printf "%s\\n" "$PWD" > "$CALLS"\n')
        self.run_paths(self.upgrade_command())
        self.assertEqual(str(default), Path(self.env['CALLS']).read_text().strip())


if __name__ == '__main__':
    unittest.main()
