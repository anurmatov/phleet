"""#507 in-sandbox workspace copy helper: synthetic owned temporary trees only; no sandbox, CLI or network."""
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
HELPER = ROOT / "scripts" / "stock-workspace-copy.py"
RUNTIME = ROOT / "src" / "Fleet.Orchestrator" / "Services" / "StockOpenShellRuntime.cs"


def exclusions():
    """The adapter's one authoritative list (PrivateWorkspaceEntries), read from source."""
    text = RUNTIME.read_text()
    body = re.search(r"PrivateWorkspaceEntries = \[(.*?)\];", text, re.S).group(1)
    return re.findall(r'"([^"]+)"', body)


def manifest(tree):
    """An independent recomputation of the documented manifest over a copied tree."""
    rows = []
    for directory, dirs, files in os.walk(tree):
        for name in dirs + files:
            path = os.path.join(directory, name)
            rows.append((os.path.relpath(path, tree), path))
    rows.sort(key=lambda row: row[0].encode("utf-8"))
    digest, total = hashlib.sha256(), 0
    for relative, path in rows:
        mode = os.lstat(path).st_mode
        if stat.S_ISDIR(mode):
            digest.update(b"d\0" + relative.encode() + b"\0" + b"0\0" + b"0\0" + b"\0")
        else:
            data = Path(path).read_bytes()
            total += len(data)
            digest.update(b"f\0%s\0%d\0%d\0%s\0" % (relative.encode(), len(data), 1 if mode & stat.S_IXUSR else 0,
                                                    hashlib.sha256(data).hexdigest().encode()))
    return {"bytes": total, "entries": len(rows), "sha256": digest.hexdigest()}


class StockWorkspaceCopyTests(unittest.TestCase):
    def setUp(self):
        self.base = Path(os.path.realpath(tempfile.mkdtemp(prefix="stock-copy-")))
        self.addCleanup(self._cleanup)
        self.root = self.base / "home" / "agent"
        self.operation = self.root / ".phleet-workspace-transfer" / "op1"
        self.operation.mkdir(parents=True)
        (self.root / "app").mkdir()
        (self.root / "app" / "appsettings.json").write_text("{\"control\":true}")
        (self.root / "trust").mkdir()
        self.legacy = self.base / "workspace"
        self.legacy.mkdir()
        self.dest = self.operation / "data"

    def _cleanup(self):
        for directory, dirs, _ in os.walk(self.base):
            for name in dirs:
                path = os.path.join(directory, name)
                if not os.path.islink(path):
                    os.chmod(path, 0o700)
        subprocess.run(["rm", "-rf", str(self.base)], check=True)

    def run_helper(self, source=None, dest=None, root=None, cwd=None, home=None, extra=()):
        root = str(root or self.root)
        args = [sys.executable, str(HELPER), "--root", root, "--source", str(source or self.legacy),
                "--dest", str(dest or self.dest)]
        for name in exclusions():
            args += ["--exclude", name]
        return subprocess.run(args + list(extra), cwd=str(cwd or self.root),
                              env={"HOME": str(home or self.root), "PATH": os.environ.get("PATH", "/usr/bin:/bin")},
                              capture_output=True, text=True, timeout=30)

    def test_copies_portable_tree_with_bytes_empty_dirs_exec_bits_and_odd_names(self):
        (self.legacy / "notes.txt").write_bytes(b"alpha\x00beta")
        (self.legacy / "run.sh").write_text("#!/bin/sh\n")
        (self.legacy / "run.sh").chmod(0o755)
        (self.legacy / "empty").mkdir()
        (self.legacy / "app").mkdir()  # an ordinary project directory named app is user data
        (self.legacy / "app" / "main.py").write_text("print(1)\n")
        for odd in ["with space", "-leading-dash", "new\nline", "юникод", "a-b", "a"]:
            (self.legacy / odd).write_text(odd)
        (self.legacy / "a.d").mkdir()
        (self.legacy / "a.d" / "x").write_text("x")
        result = self.run_helper()
        self.assertEqual(0, result.returncode, result.stderr)
        reported = json.loads(result.stdout.strip().splitlines()[-1])
        self.assertEqual(manifest(str(self.dest)), reported)
        self.assertEqual(b"alpha\x00beta", (self.dest / "notes.txt").read_bytes())
        self.assertTrue(os.stat(self.dest / "run.sh").st_mode & stat.S_IXUSR)
        self.assertFalse(os.stat(self.dest / "notes.txt").st_mode & stat.S_IXUSR)
        self.assertTrue((self.dest / "empty").is_dir())
        self.assertEqual("print(1)\n", (self.dest / "app" / "main.py").read_text())
        self.assertEqual("new\nline", (self.dest / "new\nline").read_text())
        # Nothing was moved or deleted from the source.
        self.assertEqual(b"alpha\x00beta", (self.legacy / "notes.txt").read_bytes())

    def test_every_exclusion_is_skipped_at_the_top_only(self):
        names = exclusions()
        self.assertIn(".fleet-staged", names)
        self.assertIn("codex", names)
        for name in names:
            if name.startswith(".fleet-") or name == ".mcp.json":
                (self.legacy / name).write_text("control " + name)
            else:
                (self.legacy / name).mkdir()
                (self.legacy / name / "auth.json").write_text("synthetic control " + name)
        (self.legacy / "nested").mkdir()
        (self.legacy / "nested" / ".ssh").mkdir()
        (self.legacy / "nested" / ".ssh" / "known").write_text("user data")
        (self.legacy / "nested" / ".mcp.json").write_text("user data")
        # A link inside an excluded entry is never read.
        os.symlink("/etc", self.legacy / ".codex" / "outside")
        result = self.run_helper()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual({"nested"}, set(os.listdir(self.dest)))
        self.assertEqual("user data", (self.dest / "nested" / ".ssh" / "known").read_text())
        self.assertEqual("user data", (self.dest / "nested" / ".mcp.json").read_text())

    def test_current_tree_under_root_is_selectable(self):
        current = self.root / "workspace"
        current.mkdir()
        (current / "kept.txt").write_text("kept")
        result = self.run_helper(source=current)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("kept", (self.dest / "kept.txt").read_text())

    def test_included_links_and_special_files_refuse(self):
        for kind in ["file-link", "dir-link", "dangling", "fifo"]:
            with self.subTest(kind=kind):
                self.setUp()
                (self.legacy / "sub").mkdir()
                target = self.legacy / "sub" / "entry"
                if kind == "file-link":
                    os.symlink("/etc/hostname", target)
                elif kind == "dir-link":
                    os.symlink("/etc", target)
                elif kind == "dangling":
                    os.symlink("/nonexistent-target", target)
                else:
                    os.mkfifo(target)
                result = self.run_helper()
                self.assertEqual(5, result.returncode, result.stderr)
                self.assertEqual("", result.stdout)
                self.assertFalse((self.dest / "sub").exists())

    def test_existing_destination_is_a_conflict_and_untouched(self):
        self.dest.mkdir()
        (self.dest / "earlier").write_text("kept")
        (self.legacy / "new.txt").write_text("new")
        result = self.run_helper()
        self.assertEqual(4, result.returncode, result.stderr)
        self.assertEqual(["earlier"], os.listdir(self.dest))

    def test_root_and_home_must_be_the_observed_root(self):
        self.assertEqual(3, self.run_helper(cwd=self.base).returncode)
        self.assertEqual(3, self.run_helper(home=self.base).returncode)
        linked = self.base / "linked-home"
        os.symlink(self.root, linked)
        self.assertEqual(3, self.run_helper(root=linked, cwd=linked, home=linked).returncode)
        self.assertFalse(self.dest.exists())

    def test_never_selects_home_its_ancestors_or_siblings(self):
        for source in [self.root, self.root.parent, Path("/"), self.root / "app", self.root / "trust",
                       self.root / ".phleet-workspace-transfer", self.root / ".codex"]:
            with self.subTest(source=str(source)):
                self.assertEqual(3, self.run_helper(source=source).returncode)
        self.assertFalse(self.dest.exists())

    def test_missing_linked_or_noncanonical_source_and_foreign_destination(self):
        self.assertEqual(3, self.run_helper(source=self.base / "absent").returncode)
        os.symlink(self.legacy, self.base / "linked-workspace")
        self.assertEqual(3, self.run_helper(source=self.base / "linked-workspace").returncode)
        self.assertEqual(3, self.run_helper(source=str(self.legacy) + "/").returncode)
        self.assertEqual(3, self.run_helper(source=str(self.legacy) + "/../workspace").returncode)
        self.assertEqual(3, self.run_helper(dest=self.base / "elsewhere" / "data").returncode)
        self.assertEqual(3, self.run_helper(dest=self.operation / "other").returncode)
        self.assertFalse(self.dest.exists())

    @unittest.skipIf(os.geteuid() == 0, "root reads every mode")
    def test_unreadable_source_or_subtree_fails_export(self):
        (self.legacy / "locked").mkdir()
        (self.legacy / "locked" / "x").write_text("x")
        os.chmod(self.legacy / "locked", 0)
        self.assertEqual(5, self.run_helper().returncode)
        os.chmod(self.legacy / "locked", 0o700)
        os.chmod(self.legacy, 0)
        fresh = self.root / ".phleet-workspace-transfer" / "op2"
        fresh.mkdir()
        self.assertEqual(5, self.run_helper(dest=fresh / "data").returncode)
        self.assertFalse((fresh / "data").exists())

    def test_non_utf8_name_refuses(self):
        os.close(os.open(os.path.join(os.fsencode(self.legacy), b"bad-\xff"), os.O_CREAT | os.O_WRONLY, 0o600))
        self.assertEqual(5, self.run_helper().returncode)

    def test_bad_usage_fails_closed(self):
        result = subprocess.run([sys.executable, str(HELPER), "--root", str(self.root)], cwd=str(self.root),
                                env={"HOME": str(self.root)}, capture_output=True, text=True, timeout=30)
        self.assertEqual(5, result.returncode)
        self.assertEqual("", result.stdout)


if __name__ == "__main__":
    unittest.main()
