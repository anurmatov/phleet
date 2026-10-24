"""#476: the temporal bridge image is stamped with a verified source commit, or with `unknown`.

The bridge records GIT_COMMIT as its Temporal worker build id, and compiled classifier rules match
only runs whose workflow tasks all recorded their exact commit. These tests run the real helper
against throwaway git repositories, and the real setup.sh/upgrade.sh build lines against a docker
stub, so a script that stops passing the commit (or claims one for a dirty tree) fails here.
"""
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
HELPER = ROOT / 'scripts' / 'source-commit.sh'
GIT_ENV = {
    'GIT_AUTHOR_NAME': 'fixture', 'GIT_AUTHOR_EMAIL': 'fixture@example.com',
    'GIT_COMMITTER_NAME': 'fixture', 'GIT_COMMITTER_EMAIL': 'fixture@example.com',
    'GIT_CONFIG_GLOBAL': os.devnull, 'GIT_CONFIG_NOSYSTEM': '1',
}


def git(repo, *args):
    env = {**os.environ, **GIT_ENV}
    return subprocess.run(['git', '-C', str(repo), '-c', 'init.defaultBranch=main', '-c', 'commit.gpgsign=false', *args],
                          check=True, capture_output=True, text=True, env=env).stdout.strip()


def make_repo(root):
    """A committed checkout holding the helper, a gitignored runtime dir and a placeholder Dockerfile."""
    repo = Path(root) / 'repo'
    (repo / 'scripts').mkdir(parents=True)
    shutil.copy(HELPER, repo / 'scripts' / 'source-commit.sh')
    (repo / 'Dockerfile.temporal').write_text('FROM scratch\n')
    (repo / '.gitignore').write_text('/fleet/\n')
    git(repo, 'init', '-q')
    git(repo, 'add', '-A')
    git(repo, 'commit', '-q', '-m', 'fixture')
    return repo


def helper(repo, path=None):
    env = dict(os.environ)
    if path is not None:
        env['PATH'] = path
    result = subprocess.run(['/bin/bash', str(HELPER), str(repo)], capture_output=True, text=True, env=env)
    return result.returncode, result.stdout, result.stderr


class SourceCommitHelperTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.repo = make_repo(self.tmp)
        self.head = git(self.repo, 'rev-parse', 'HEAD')

    def tearDown(self):
        shutil.rmtree(self.tmp)

    def test_clean_checkout_prints_its_40_hex_head(self):
        rc, out, _ = helper(self.repo)
        self.assertEqual((0, self.head + '\n'), (rc, out))
        self.assertRegex(self.head, r'^[0-9a-f]{40}$')

    def test_ignored_runtime_files_do_not_make_it_dirty(self):
        (self.repo / 'fleet').mkdir()
        (self.repo / 'fleet' / '.env').write_text('X=1\n')
        self.assertEqual(self.head + '\n', helper(self.repo)[1])

    def test_any_unreviewed_change_prints_unknown(self):
        cases = {
            'modified': lambda: (self.repo / 'Dockerfile.temporal').write_text('FROM scratch\nRUN true\n'),
            'staged': lambda: ((self.repo / 'staged.txt').write_text('x'), git(self.repo, 'add', 'staged.txt')),
            'untracked': lambda: (self.repo / 'untracked.cs').write_text('// not reviewed'),
        }
        for name, change in cases.items():
            with self.subTest(name):
                git(self.repo, 'reset', '-q', '--hard')
                git(self.repo, 'clean', '-qfd')
                change()
                rc, out, err = helper(self.repo)
                self.assertEqual((0, 'unknown\n'), (rc, out))
                self.assertIn('modified, staged or untracked', err)

    def test_not_a_checkout_or_no_git_prints_unknown(self):
        plain = Path(self.tmp) / 'plain'
        plain.mkdir()
        self.assertEqual((0, 'unknown\n'), helper(plain)[:2])
        rc, out, err = helper(self.repo, path=str(Path(self.tmp) / 'no-bin'))
        self.assertEqual((0, 'unknown\n'), (rc, out))
        self.assertIn('git is not installed', err)


def build_program(script):
    """The script's real build_image helper, SOURCE_COMMIT block and temporal-bridge call."""
    text = (ROOT / script).read_text()
    function = re.search(r'^( *)build_image\(\) \{\n.*?^\1\}\n', text, re.M | re.S)
    commit = re.search(r'^ *SOURCE_COMMIT=\$\(.*\n *\[\[ "\$SOURCE_COMMIT" != unknown \]\] \\\n *\|\| warn .*\n', text, re.M)
    call = re.search(r'^ *build_image "fleet:temporal-bridge" .*\\\n.*\n', text, re.M)
    for part, found in (('build_image', function), ('SOURCE_COMMIT', commit), ('temporal-bridge call', call)):
        if found is None:
            raise AssertionError(f'{script}: {part} not found')
    if commit.start() > call.start():
        raise AssertionError(f'{script}: SOURCE_COMMIT is computed after the temporal-bridge build')
    return function.group(0), commit.group(0), call.group(0)


class InstallerBuildArgTests(unittest.TestCase):
    def run_build(self, script, repo, tmp):
        function, commit, call = build_program(script)
        stub = Path(tmp) / 'bin'
        stub.mkdir(exist_ok=True)
        argv = Path(tmp) / f'{Path(script).stem}.argv'
        (stub / 'docker').write_text('#!/bin/sh\nfor a in "$@"; do printf \'%s\\n\' "$a"; done > "$DOCKER_ARGV"\n')
        (stub / 'docker').chmod(0o755)
        program = '\n'.join([
            'set -euo pipefail',
            'ok() { :; }; fail() { echo "FAIL $*"; }; warn() { echo "WARN $*"; }',
            'DRY_RUN=false; NO_CACHE=""', f'SCRIPT_DIR="{repo}"', function, commit, call])
        env = {**os.environ, 'PATH': f'{stub}:{os.environ["PATH"]}', 'DOCKER_ARGV': str(argv)}
        result = subprocess.run(['bash', '-c', program], capture_output=True, text=True, env=env)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        return argv.read_text().splitlines(), result.stdout

    def test_setup_and_upgrade_pass_the_verified_commit_or_unknown(self):
        for script in ('setup.sh', 'upgrade.sh'):
            with self.subTest(script), tempfile.TemporaryDirectory() as tmp:
                repo = make_repo(tmp)
                head = git(repo, 'rev-parse', 'HEAD')

                args, out = self.run_build(script, repo, tmp)
                self.assertIn('fleet:temporal-bridge', args)
                self.assertEqual(f'{repo}/Dockerfile.temporal', args[args.index('-f') + 1])
                self.assertEqual(f'GIT_COMMIT={head}', args[args.index('--build-arg') + 1])
                self.assertNotIn('WARN', out)

                (repo / 'local-edit.cs').write_text('// not reviewed')
                args, out = self.run_build(script, repo, tmp)
                self.assertEqual('GIT_COMMIT=unknown', args[args.index('--build-arg') + 1])
                self.assertIn('WARN temporal bridge has no source commit', out)


class ImageAndComposeTests(unittest.TestCase):
    def test_dockerfile_defaults_to_unknown_and_bakes_the_stamp(self):
        text = (ROOT / 'Dockerfile.temporal').read_text()
        self.assertIn('ARG GIT_COMMIT=unknown\n', text)
        self.assertIn('ENV FLEET_BUILD_COMMIT=$GIT_COMMIT\n', text)

    def test_compose_build_takes_the_commit_from_the_command_line_only(self):
        text = (ROOT / 'docker-compose.example.yml').read_text()
        block = re.search(r'^  fleet-temporal-bridge:\n((?:    .*\n|\s*\n)+)', text, re.M).group(1)
        self.assertRegex(block, r'(?m)^    build:\n(?:      .*\n)*      args:\n(?:        #.*\n)*        GIT_COMMIT: \$\{FLEET_SOURCE_COMMIT:-unknown\}\n')
        # A value in .env would outlive the checkout it names and stamp newer code with it.
        self.assertNotIn('FLEET_SOURCE_COMMIT', (ROOT / '.env.example').read_text())


if __name__ == '__main__':
    unittest.main()
