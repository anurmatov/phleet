#!/usr/bin/env python3
"""Managed stock workspace export helper (#507).

The orchestrator uploads this file into a fresh operation directory under the stock workload's HOME and runs
it there with `sandbox exec` as the workload user. It copies exactly one user tree into a new, exclusively
created snapshot directory beneath the observed stock root, so the unmodified pinned CLI can download it:

    stock-workspace-copy.py --root ROOT --source SOURCE --dest DEST [--exclude NAME]...

Nothing is followed, moved or deleted. Only regular files and directories are copied; a symbolic link or any
other entry type anywhere in the selected tree refuses the whole copy. Excluded top-level names (account and
control state) are skipped without being read. On success one JSON line describes the snapshot; the
orchestrator recomputes it over the downloaded copy before using it. Standard library only: no network,
account or CLI logic.

Exit status: 0 copied; 3 layout unverified; 4 destination conflict; 5 export failed.
"""
import argparse
import hashlib
import json
import os
import stat
import sys

EXIT_LAYOUT = 3
EXIT_CONFLICT = 4
EXIT_EXPORT = 5

TRANSFER_DIR = ".phleet-workspace-transfer"
USER_TREE = "workspace"


class Refused(Exception):
    def __init__(self, code, reason):
        super().__init__(reason)
        self.code = code
        self.reason = reason


def canonical(path):
    """An absolute path that is already in canonical lexical form (no ., .., // or trailing slash)."""
    return isinstance(path, str) and path.startswith("/") and path != "/" and os.path.normpath(path) == path \
        and not path.startswith("//")


def real_directory_chain(path, code):
    """Every existing component from / to PATH is a real directory, checked without following links."""
    current = "/"
    for part in path.strip("/").split("/"):
        current = os.path.join(current, part)
        try:
            mode = os.lstat(current).st_mode
        except FileNotFoundError:
            raise Refused(code, "missing")
        except OSError:
            raise Refused(EXIT_EXPORT, "unreadable")
        if not stat.S_ISDIR(mode):
            raise Refused(code, "not_a_real_directory")


def utf8_name(name):
    try:
        name.encode("utf-8")
    except UnicodeEncodeError:
        raise Refused(EXIT_EXPORT, "unsupported_name")
    return name


def record(digest, kind, relative, size, executable, content):
    digest.update(b"%s\0%s\0%d\0%d\0%s\0" % (kind, relative.encode("utf-8"), size, executable, content))


def copy_file(source, target, mode):
    """Streams one regular file without following a link at either end; returns (size, sha256 hex)."""
    flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_NONBLOCK", 0) | getattr(os, "O_CLOEXEC", 0)
    source_fd = os.open(source, flags)
    try:
        if not stat.S_ISREG(os.fstat(source_fd).st_mode):
            raise Refused(EXIT_EXPORT, "unsupported_entry")
        target_fd = os.open(target, os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0)
                            | getattr(os, "O_CLOEXEC", 0), 0o600)
        try:
            content = hashlib.sha256()
            size = 0
            while True:
                chunk = os.read(source_fd, 1 << 20)
                if not chunk:
                    break
                content.update(chunk)
                size += len(chunk)
                view = memoryview(chunk)
                while view:
                    view = view[os.write(target_fd, view):]
            os.fchmod(target_fd, (stat.S_IMODE(mode) & 0o777) | 0o600)
        finally:
            os.close(target_fd)
    finally:
        os.close(source_fd)
    return size, content.hexdigest().encode("ascii")


def copy_tree(source, dest, excluded):
    """Copies SOURCE's portable tree into the new DEST; returns (entries, bytes, sha256 of the manifest)."""
    entries = []

    def walk(directory, relative, top):
        with os.scandir(directory) as iterator:
            names = [utf8_name(entry.name) for entry in iterator]
        for name in names:
            if top and name in excluded:
                continue
            path = os.path.join(directory, name)
            child = name if not relative else relative + "/" + name
            mode = os.lstat(path).st_mode
            if stat.S_ISDIR(mode):
                entries.append((child, "d", path, mode))
                walk(path, child, False)
            elif stat.S_ISREG(mode):
                entries.append((child, "f", path, mode))
            else:
                raise Refused(EXIT_EXPORT, "unsupported_entry")

    walk(source, "", True)
    entries.sort(key=lambda item: item[0].encode("utf-8"))
    digest = hashlib.sha256()
    total = 0
    for relative, kind, path, mode in entries:
        target = os.path.join(dest, relative)
        if kind == "d":
            os.mkdir(target, 0o700)
            record(digest, b"d", relative, 0, 0, b"")
        else:
            size, content = copy_file(path, target, mode)
            total += size
            record(digest, b"f", relative, size, 1 if mode & stat.S_IXUSR else 0, content)
    return len(entries), total, digest.hexdigest()


def run(arguments):
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--root", required=True)
    parser.add_argument("--source", required=True)
    parser.add_argument("--dest", required=True)
    parser.add_argument("--exclude", action="append", default=[])
    try:
        options = parser.parse_args(arguments)
    except SystemExit:
        raise Refused(EXIT_EXPORT, "usage")
    root, source, dest = options.root, options.source, options.dest
    excluded = set(options.exclude)
    if not all(canonical(p) for p in (root, source, dest)) or any("/" in name or not name for name in excluded):
        raise Refused(EXIT_LAYOUT, "path_not_canonical")
    # The observed stock root: the working directory and HOME both resolve to exactly ROOT.
    if os.path.realpath(os.getcwd()) != root or os.path.realpath(os.environ.get("HOME") or "/") != root:
        raise Refused(EXIT_LAYOUT, "root_mismatch")
    real_directory_chain(root, EXIT_LAYOUT)
    # The selected tree is the managed user tree under ROOT or a tree entirely outside it; never ROOT, an
    # ancestor of ROOT or any other HOME sibling (app, trust, native homes, the transfer area).
    if source == root or root.startswith(source + "/") or \
            (source.startswith(root + "/") and source != root + "/" + USER_TREE):
        raise Refused(EXIT_LAYOUT, "source_not_selectable")
    real_directory_chain(source, EXIT_LAYOUT)
    if not os.access(source, os.R_OK | os.X_OK):
        raise Refused(EXIT_EXPORT, "source_unreadable")
    # The snapshot is DEST = ROOT/.phleet-workspace-transfer/<operation>/data, created here and nowhere else.
    operation = os.path.dirname(dest)
    if os.path.basename(dest) != "data" or os.path.dirname(operation) != root + "/" + TRANSFER_DIR:
        raise Refused(EXIT_LAYOUT, "dest_not_owned")
    real_directory_chain(operation, EXIT_LAYOUT)
    try:
        os.mkdir(dest, 0o700)
    except FileExistsError:
        raise Refused(EXIT_CONFLICT, "dest_exists")
    except OSError:
        raise Refused(EXIT_EXPORT, "dest_unwritable")
    entries, size, digest = copy_tree(source, dest, excluded)
    return {"entries": entries, "bytes": size, "sha256": digest}


def main(arguments):
    try:
        result = run(arguments)
    except Refused as refused:
        print(refused.reason, file=sys.stderr)
        return refused.code
    except OSError as error:
        print("copy_failed:" + type(error).__name__, file=sys.stderr)
        return EXIT_EXPORT
    print(json.dumps(result, sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
