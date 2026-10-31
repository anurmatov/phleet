#!/usr/bin/env python3
"""#507 operator regression: the managed workspace layout through the REAL pinned stock CLI and adapter.

Entry point: `bash scripts/test-stock-workspace-transfer.sh PRIVATE-FIXTURE.json` (see that file for the fixture
contract). This module holds the logic so its identity and cleanup paths are unit-tested without a gateway
(tests/scripts/test_stock_workspace_transfer.py). It never creates fixtures, selects a live identity, looks up
credentials implicitly, or changes production configuration, networks or grants.
"""
import contextlib
import datetime
import hashlib
import json
import os
import re
import shutil
import signal
import stat
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

PINNED_CLI_SHA256 = "446f766842d2bf03abc37b56a3f5a3e46b96140b5b6c305d3f168d67921fd271"
KEYS = {"orchestratorUrl", "tokenEnv", "cli", "cliConfigDir", "workloadHome", "freshAgent", "freshDockerWorkspace",
        "legacyAgent", "baselineImage", "replacementImage", "receiptDir"}
CLEANUP_SECONDS = 120
# Excluded-state reconciliation (A5). Ordinary provisioning (ContainerProvisioningService) rewrites these generated
# control outputs on every apply; the Docker bind of .generated/output-styles creates its mount point; the committed
# Docker workload mounts these homes read-write. Any other change, and any change to a script-placed sentinel, fails.
GENERATED_FILES = {".generated/appsettings.json", ".generated/.mcp.json", ".generated/settings.json", ".generated/codex-root.toml"}
GENERATED_TREES = (".generated/roles", ".generated/projects", ".generated/output-styles")
BIND_TARGET_DIRECTORIES = {".claude/output-styles"}
WORKLOAD_HOMES = {"claude", "codex"}
SENTINEL = "phleet-507-sentinel"
STAGED = 'test -f "$HOME/.fleet-staged" && test ! -f "$HOME/.fleet-ready"'

MANIFEST = r'''
import hashlib, json, os, stat, sys
def manifest(top):
    rows = {}
    for directory, dirs, files in os.walk(top):
        for name in sorted(dirs + files):
            path = os.path.join(directory, name)
            rel = os.path.relpath(path, top)
            mode = os.lstat(path).st_mode
            if stat.S_ISDIR(mode):
                rows[rel] = ["d"]
            elif stat.S_ISREG(mode):
                with open(path, "rb") as handle:
                    rows[rel] = ["f", hashlib.sha256(handle.read()).hexdigest(), bool(mode & stat.S_IXUSR)]
            else:
                rows[rel] = ["other"]
    return rows
if __name__ == "__main__":
    print(json.dumps({top: manifest(top) for top in sys.argv[1:]}))
'''
_namespace = {"__name__": "manifest"}
exec(MANIFEST, _namespace)  # the same source defines manifest() here and runs inside the sandbox
manifest = _namespace["manifest"]


class Blocked(Exception):
    """A missing or malformed prerequisite: nothing was mutated."""


class Interrupted(BaseException):
    """SIGINT/SIGTERM: like KeyboardInterrupt, it unwinds through every cleanup `finally` and ends the run."""


class CleanupDeadline(Exception):
    """A cleanup action used its whole 120-second budget."""


def now():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def digest(rows):
    return hashlib.sha256(json.dumps(rows, sort_keys=True).encode()).hexdigest()


def load_fixture(path):
    try:
        info = os.lstat(path)
    except OSError:
        raise Blocked("fixture file missing")
    if not stat.S_ISREG(info.st_mode) or info.st_uid != os.geteuid() or info.st_mode & 0o077:
        raise Blocked("fixture must be a regular, caller-owned, owner-only file")
    try:
        with open(path) as handle:
            data = json.load(handle)
    except (OSError, ValueError):
        raise Blocked("fixture is not valid JSON")
    if not isinstance(data, dict) or set(data) != KEYS or not all(isinstance(v, str) and v for v in data.values()):
        raise Blocked("fixture keys must be exactly: " + ", ".join(sorted(KEYS)))
    for key in ("cli", "cliConfigDir", "workloadHome", "freshDockerWorkspace", "receiptDir"):
        if not os.path.isabs(data[key]) or os.path.normpath(data[key]) != data[key]:
            raise Blocked(f"{key} must be a canonical absolute path")
    for key in ("freshAgent", "legacyAgent"):
        if not re.fullmatch(r"[a-z0-9][a-z0-9-]{0,40}", data[key]):
            raise Blocked(f"{key} is not a supported agent name")
    for key in ("baselineImage", "replacementImage"):
        if not re.fullmatch(r"\S+@sha256:[0-9a-f]{64}", data[key]):
            raise Blocked(f"{key} must be an immutable name@sha256 reference")
    if data["baselineImage"] == data["replacementImage"] or data["freshAgent"] == data["legacyAgent"]:
        raise Blocked("the fixture needs two distinct images and two distinct agents")
    if not re.fullmatch(r"https?://[^\s/]+", data["orchestratorUrl"].rstrip("/")):
        raise Blocked("orchestratorUrl must be an origin")
    receipt_dir = data["receiptDir"]
    if not os.path.isdir(receipt_dir) or os.path.islink(receipt_dir) or os.stat(receipt_dir).st_uid != os.geteuid() \
            or os.stat(receipt_dir).st_mode & 0o077:
        raise Blocked("receiptDir must be an existing, caller-owned 0700 directory")
    return data


def control_state(top, excluded):
    """Every entry under an excluded top-level name, without following links: type, size, content hash and mtime."""
    rows = {}
    for directory, dirs, files in os.walk(top):
        for name in dirs + files:
            path = os.path.join(directory, name)
            rel = os.path.relpath(path, top)
            if rel.split("/")[0] not in excluded:
                continue
            info = os.lstat(path)
            kind = "d" if stat.S_ISDIR(info.st_mode) else "f" if stat.S_ISREG(info.st_mode) else "l" if stat.S_ISLNK(info.st_mode) else "other"
            content = None
            if kind == "f":
                with open(path, "rb") as handle:
                    content = hashlib.sha256(handle.read()).hexdigest()
            rows[rel] = {"type": kind, "size": info.st_size if kind == "f" else 0, "sha256": content, "mtime": info.st_mtime}
    return rows


def control_changes(before, after):
    """Added, removed or changed (type or bytes) excluded-state entries; a directory whose listing changed shows as its entries."""
    return [{"path": rel, "before": before.get(rel), "after": after.get(rel)} for rel in sorted(set(before) | set(after))
            if not (rel in before and rel in after and (before[rel]["type"], before[rel]["sha256"]) == (after[rel]["type"], after[rel]["sha256"]))]


def attribute(change, sentinels, started_at):
    """Why an excluded-state entry changed; anything not positively attributable is `unattributed`, never assumed benign."""
    path, before, after = change["path"], change["before"], change["after"]
    if path in sentinels:
        return "sentinel_violation"
    if path in GENERATED_FILES or any(path == tree or path.startswith(tree + "/") for tree in GENERATED_TREES):
        return "provisioning_regenerated"
    if path in BIND_TARGET_DIRECTORIES and before is None and after is not None and after["type"] == "d":
        return "docker_bind_target"
    if path.split("/")[0] in WORKLOAD_HOMES and after is not None and started_at is not None and after["mtime"] >= started_at:
        return "committed_workload_write"
    return "unattributed"


def docker_time(value):
    """Docker's RFC 3339 nanosecond timestamp as epoch seconds; None when unreadable or the zero time."""
    match = re.fullmatch(r"(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)(?:\.(\d+))?(Z|[+-]\d\d:\d\d)", value.strip())
    if not match or match.group(1).startswith("0001-"):
        return None
    zone = "+00:00" if match.group(3) == "Z" else match.group(3)
    moment = datetime.datetime.fromisoformat(match.group(1) + zone)
    return moment.timestamp() + float("0." + (match.group(2) or "0"))


def exclusions(root):
    """The adapter's one exclusion list (PrivateWorkspaceEntries), read from the exact checkout under test."""
    with open(os.path.join(root, "src/Fleet.Orchestrator/Services/StockOpenShellRuntime.cs")) as source:
        text = source.read()
    return re.findall(r'"([^"]+)"', re.search(r"PrivateWorkspaceEntries = \[(.*?)\];", text, re.S).group(1))


class Regression:
    """One run against one fixture. `cli`, `api`, `clock` and `sleep` are the only effects; tests replace them."""

    def __init__(self, fixture, root, token, work):
        self.f, self.token, self.work = fixture, token, work
        self.home = fixture["workloadHome"]
        self.w = self.home + "/workspace"
        self.excluded = exclusions(root)
        self.receipt = {"cases": {}, "cleanup": [], "signals": [], "started": now()}
        self.state = {}
        self.cleaning = False  # while True, a signal is recorded and deferred, never allowed to cut cleanup short
        self.interrupted = None

    # ── effects ──
    def cli(self, args, timeout=300, scope=None):
        env = {k: v for k, v in os.environ.items() if k not in ("OPENSHELL_GATEWAY_INSECURE", "OPENSHELL_GATEWAY_ENDPOINT", "OPENSHELL_GATEWAY")}
        env["XDG_CONFIG_HOME"] = self.f["cliConfigDir"]
        prefix = ["--workspace", scope] if scope else []
        try:
            run = subprocess.run([self.f["cli"], *prefix, *args], env=env, stdin=subprocess.DEVNULL, capture_output=True, timeout=timeout)
        except subprocess.TimeoutExpired:
            return 124, b"", b"timeout"
        return run.returncode, run.stdout, run.stderr

    def api(self, method, path, body=None, timeout=900):
        request = urllib.request.Request(self.f["orchestratorUrl"].rstrip("/") + path, method=method,
                                         data=None if body is None else json.dumps(body).encode(),
                                         headers={"Authorization": "Bearer " + self.token, "Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(request, timeout=timeout) as response:
                return response.status, json.loads(response.read() or b"null")
        except urllib.error.HTTPError as error:
            try:
                return error.code, json.loads(error.read() or b"null")
            except ValueError:
                return error.code, None
        except (urllib.error.URLError, OSError, ValueError):
            return 0, None

    def clock(self):
        return time.monotonic()

    def sleep(self, seconds):
        time.sleep(seconds)

    def docker(self, args, timeout=60):
        """Read-only Docker queries on the gateway host (image and label lookups); (exit code, stdout)."""
        try:
            run = subprocess.run(["docker", *args], stdin=subprocess.DEVNULL, capture_output=True, timeout=timeout)
        except (subprocess.TimeoutExpired, OSError):
            return 124, b""
        return run.returncode, run.stdout

    def wait(self, predicate, seconds=180):
        end = self.clock() + seconds
        while self.clock() < end:
            if predicate():
                return True
            self.sleep(max(0.0, min(2.0, end - self.clock())))
        return False

    # ── interruption: SIGINT/SIGTERM unwind through the same cleanup as a failure, which they never cut short ──
    def install_signal_handlers(self):
        def handler(signum, frame):
            name = signal.Signals(signum).name
            self.receipt["signals"].append(name)
            if self.cleaning or self.interrupted:
                return  # deferred: raised once the cleanup in progress has finished
            self.interrupted = name
            raise Interrupted(name)
        return {sig: signal.signal(sig, handler) for sig in (signal.SIGINT, signal.SIGTERM)}

    @contextlib.contextmanager
    def cleaning_up(self):
        nested = self.cleaning
        self.cleaning = True
        try:
            yield
        finally:
            self.cleaning = nested
        if not nested and self.receipt["signals"] and self.interrupted is None:
            self.interrupted = self.receipt["signals"][-1]
            raise Interrupted(self.interrupted)

    # ── identity: every fixture mutation targets the exact persisted sandbox in its persisted scope ──
    def config(self, agent):
        status, body = self.api("GET", f"/api/agents/{agent}/config", timeout=60)
        if status != 200 or not isinstance(body, dict):
            raise Blocked(f"agent {agent} config unreadable")
        return body, json.loads(body.get("appliedRuntimeBinding") or "{}")

    def observe(self, sandbox, timeout=60):
        """The phase of exactly this binding's sandbox (name, UUID, scope); anything else refuses."""
        code, out, _ = self.cli(["sandbox", "get", sandbox["sandboxName"], "-o", "json"], timeout, sandbox.get("sandboxWorkspace"))
        try:
            document = json.loads(out) if code == 0 else None
        except ValueError:
            document = None
        if not isinstance(document, dict):
            raise AssertionError("sandbox unobservable in the supplied gateway")
        if document.get("id") != sandbox["sandboxId"] or document.get("name") != sandbox["sandboxName"]:
            raise AssertionError("gateway sandbox differs from the persisted binding")
        return document.get("phase")

    def mutate(self, sandbox, args, timeout=300):
        self.observe(sandbox)
        return self.cli(args, timeout, sandbox.get("sandboxWorkspace"))

    def exec_ok(self, sandbox, script, *args, timeout=60):
        return self.mutate(sandbox, ["sandbox", "exec", "-n", sandbox["sandboxName"], "--", "sh", "-c", script, "probe", *args], timeout)[0] == 0

    def remote_manifest(self, sandbox, *paths):
        code, out, _ = self.mutate(sandbox, ["sandbox", "exec", "-n", sandbox["sandboxName"], "--", "python3", "-c", MANIFEST, *paths])
        if code != 0:
            raise AssertionError("in-sandbox manifest failed")
        try:
            return json.loads(out.decode().strip().splitlines()[-1])
        except (ValueError, IndexError):
            raise AssertionError("in-sandbox manifest unreadable")

    def download(self, sandbox, source, label):
        self.observe(sandbox)
        target = os.path.join(self.work, label)
        os.makedirs(target)  # an existing directory: a tree's contents or a file both land inside it
        code, _, err = self.cli(["sandbox", "download", sandbox["sandboxName"], source, target], scope=sandbox.get("sandboxWorkspace"))
        return code, target, err

    @contextlib.contextmanager
    def staged(self, sandbox):
        """Boots a retained, revoked sandbox into its staged launcher only, and always stops it again (bounded)."""
        if self.observe(sandbox) == "Ready":
            raise AssertionError("sandbox already running; the regression never touches a live workload")
        try:
            if self.mutate(sandbox, ["sandbox", "start", sandbox["sandboxName"]])[0] != 0:
                raise AssertionError("staged start failed")
            if not self.wait(lambda: self.exec_ok(sandbox, STAGED)):
                raise AssertionError("sandbox never reported staged")
            yield
        finally:  # also on interruption
            with self.cleaning_up():
                self.stop_cleanup(sandbox)

    # ── cleanup: bounded, independent of the case outcome, every result kept in the private receipt ──
    def stop_cleanup(self, sandbox):
        """Identity probe, stop and Stopped polling under ONE 120-second deadline; every call gets only the time left."""
        entry = {"action": "stop", "sandboxName": sandbox["sandboxName"], "sandboxId": sandbox["sandboxId"]}
        end = self.clock() + CLEANUP_SECONDS

        def left(cap=60.0):
            remaining = end - self.clock()
            if remaining <= 0:
                raise CleanupDeadline(f"cleanup deadline of {CLEANUP_SECONDS}s exceeded")
            return min(cap, remaining)
        try:
            self.observe(sandbox, timeout=left())  # never stop a different sandbox
            code = self.cli(["sandbox", "stop", sandbox["sandboxName"]], left(), sandbox.get("sandboxWorkspace"))[0]
            while self.observe(sandbox, timeout=left()) != "Stopped":
                self.sleep(left(2.0))
            entry["result"] = "stopped"
            if code != 0:
                entry["stopExit"] = code
        except Exception as error:  # cleanup never masks the case result, and is never retried
            entry.update(result="error", reason=str(error))
        self.receipt["cleanup"].append(entry)

    def container_id(self, name, timeout=60):
        """The full ID of the one container with exactly this name; None when absent; raises when ambiguous or unobservable."""
        code, out = self.docker(["ps", "-a", "--no-trunc", "--filter", f"name=^/{name}$", "--format", "{{.ID}}"], timeout)
        if code != 0:
            raise AssertionError("Docker container observation failed")
        ids = out.decode(errors="replace").split()
        if len(ids) > 1:
            raise AssertionError("several containers carry the agent's container name")
        return ids[0] if ids else None

    def agent_cleanup(self, agent):
        """After a Docker commit the managed binding is gone (revoke would be a 409): stop the committed Docker workload."""
        pending = self.state.get("dockerApply")
        if not pending or pending["agent"] != agent:
            self.revoke_cleanup(agent)
            return
        status, body = self.api("GET", f"/api/agents/{agent}/config", timeout=60)
        applied = body.get("appliedRuntimeKind") if status == 200 and isinstance(body, dict) else None
        if applied == "docker":
            self.docker_stop_cleanup(pending)
            return
        if applied is None:
            self.receipt["cleanup"].append({"action": "commit-state", "agent": agent, "result": "error", "reason": f"applied runtime unreadable ({status})"})
            self.docker_stop_cleanup(pending)  # identity-checked: stops only a container provisioned after the Docker apply
        self.revoke_cleanup(agent)

    def docker_stop_cleanup(self, pending):
        """Identity check, stop and not-running polling of the committed Docker workload under ONE 120-second deadline."""
        entry = {"action": "docker-stop", "agent": pending["agent"], "container": pending["container"]}
        end = self.clock() + CLEANUP_SECONDS

        def left(cap=60.0):
            remaining = end - self.clock()
            if remaining <= 0:
                raise CleanupDeadline(f"cleanup deadline of {CLEANUP_SECONDS}s exceeded")
            return min(cap, remaining)
        try:
            current = self.container_id(pending["container"], timeout=left())
            if current is None:
                entry["result"] = "absent"
            else:
                entry["containerId"] = current
                # Only the workload the Docker apply provisioned: never the container that existed before it, and when the
                # commit was observed, exactly that container.
                if current == pending.get("before") or pending.get("committed") not in (None, current):
                    raise AssertionError("the named container is not the committed Docker workload")
                code, _ = self.docker(["stop", "--time", str(int(min(30, left()))), current], timeout=left())
                while True:
                    probe, out = self.docker(["inspect", "--format", "{{.State.Running}}", current], timeout=left())
                    if probe == 0 and out.decode(errors="replace").strip() == "false":
                        break
                    self.sleep(left(2.0))
                entry["result"] = "stopped"
                if code != 0:
                    entry["stopExit"] = code
        except Exception as error:  # cleanup never masks the case result, and is never retried
            entry.update(result="error", reason=str(error))
        self.receipt["cleanup"].append(entry)

    def revoke_cleanup(self, agent):
        """The orchestrator's own bounded revoke (cut, detach, broker revoke, verified Stop) as one call within the deadline."""
        status, body = self.api("POST", f"/api/agents/{agent}/revoke", timeout=CLEANUP_SECONDS)
        entry = {"action": "revoke", "agent": agent, "result": "revoked" if status == 200 else "error"}
        if status != 200:
            entry["reason"] = f"revoke returned {status} {json.dumps(body)[:200]}"
        self.receipt["cleanup"].append(entry)

    def case(self, name, function, agent=None):
        result = {"result": "INTERRUPTED"}
        try:
            detail = function()
            result = {"result": "PASS", **(detail or {})}
        except AssertionError as error:
            result = {"result": "FAIL", "reason": str(error)}
        except Blocked as error:
            result = {"result": "BLOCKED", "reason": str(error)}
        except Exception as error:  # an unexpected local error is a failure, never a pass
            result = {"result": "FAIL", "reason": type(error).__name__}
        finally:  # also on Interrupted/KeyboardInterrupt, e.g. right after a successful Apply
            with self.cleaning_up():
                self.receipt["cases"][name] = result
                if result["result"] != "PASS" and agent:
                    self.agent_cleanup(agent)  # a failed or interrupted case never leaves its agent's workload running
                print(f"{name}: {result['result']}")

    # ── image identity: references may be tags or digests; the pinned image is compared by its resolved local ID ──
    def image_id(self, reference):
        """The local image ID a reference (tag or digest) resolves to now; None when it does not resolve exactly."""
        code, out = self.docker(["image", "inspect", "--format", "{{.Id}}", reference])
        value = out.decode(errors="replace").strip() if code == 0 else ""
        return value if re.fullmatch(r"sha256:[0-9a-f]{64}", value) else None

    def workload_image_id(self, sandbox_id):
        """The image ID the stock driver's workload container for exactly this sandbox UUID was created from."""
        code, out = self.docker(["ps", "-a", "--no-trunc", "--filter", "label=openshell.ai/sandbox-id=" + sandbox_id,
                                 "--filter", "label=openshell.ai/isolation-role=sandbox", "--format", "{{.ID}}"])
        containers = out.decode(errors="replace").split() if code == 0 else []
        if len(containers) != 1:
            return None
        code, out = self.docker(["inspect", "--format", "{{.Image}}", containers[0]])
        value = out.decode(errors="replace").strip() if code == 0 else ""
        return value if re.fullmatch(r"sha256:[0-9a-f]{64}", value) else None

    def image_fault(self, binding, pinned):
        """None when the binding's recorded reference (kept as recorded) and its workload both resolve to the pinned image."""
        expected = self.image_id(pinned)
        if expected is None:
            return "pinned image reference does not resolve on the gateway host"
        recorded = self.image_id(binding.get("image") or "")
        workload = self.workload_image_id(binding.get("sandboxId") or "")
        self.receipt.setdefault("imageIdentity", []).append({"sandboxId": binding.get("sandboxId"), "recorded": binding.get("image"),
                                                             "pinned": pinned, "pinnedId": expected, "recordedId": recorded, "workloadId": workload})
        if recorded != expected:
            return "the binding's recorded image reference does not resolve to the pinned image"
        if workload != expected:
            return "the sandbox workload does not run the pinned image"
        return None

    # ── prerequisites: nothing mutates until all of these hold ──
    def check_fixture_state(self):
        fresh_cfg, _ = self.config(self.f["freshAgent"])
        legacy_cfg, legacy = self.config(self.f["legacyAgent"])
        old = legacy.get("applied") or {}
        if fresh_cfg.get("runtimeKind") != "openshell" or fresh_cfg.get("appliedRuntimeKind") not in ("docker", None, ""):
            raise Blocked("freshAgent must be desired openshell and applied docker")
        ws = self.f["freshDockerWorkspace"]
        if os.path.islink(ws) or (os.path.exists(ws) and (not os.path.isdir(ws) or os.listdir(ws))):
            raise Blocked("freshDockerWorkspace must be absent or an empty real directory")
        if legacy_cfg.get("appliedRuntimeKind") != "openshell" or legacy.get("state") not in ("revoked", "stopped") \
                or legacy.get("candidate") or "workloadWorkspacePath" in old or not old.get("image") \
                or not old.get("sandboxName") or not old.get("sandboxId"):
            raise Blocked("legacyAgent must hold a revoked or stopped legacy binding")
        # Two distinct pinned images; the legacy binding's recorded reference (a tag or a digest, never rewritten) and its
        # actual workload container must both resolve to the pinned baseline.
        baseline, replacement = self.image_id(self.f["baselineImage"]), self.image_id(self.f["replacementImage"])
        if baseline is None or replacement is None or baseline == replacement:
            raise Blocked("baselineImage and replacementImage must resolve to two distinct local images")
        if (fault := self.image_fault(old, self.f["baselineImage"])) is not None:
            raise Blocked("legacy binding is not on baselineImage: " + fault)
        # The supplied gateway must hold exactly that persisted sandbox, stopped, before anything starts.
        try:
            phase = self.observe(old)
        except AssertionError as error:
            raise Blocked(f"legacy fixture does not match the supplied gateway: {error}")
        if phase == "Ready":
            raise Blocked("legacy fixture sandbox is running")
        self.state["old"] = old

    # ── cases ──
    def portable(self, rows):
        return {k: v for k, v in rows.items() if k.split("/")[0] not in self.excluded}

    def a1_legacy_control(self):
        old = self.state["old"]
        with self.staged(old):
            self.state["legacy"] = self.remote_manifest(old, "/workspace", self.home + "/app", self.home + "/trust")
            code, _, err = self.download(old, "/workspace", "a1")
        assert code != 0 and b"outside the sandbox workspace" in err, "pinned CLI did not refuse the legacy root"
        return {"refused": True, "legacySha256": digest(self.state["legacy"]["/workspace"]), "entries": len(self.state["legacy"]["/workspace"])}

    def a3_legacy_image_change(self):
        old = self.state["old"]
        assert "legacy" in self.state, "A1 did not record the legacy tree"
        status, _ = self.api("POST", f"/api/agents/{self.f['legacyAgent']}/revoke")
        assert status == 200, f"revoke returned {status}"
        status, body = self.api("POST", f"/api/agents/{self.f['legacyAgent']}/runtime/apply")
        assert status == 200, f"apply returned {status} {json.dumps(body)[:200]}"
        _, record = self.config(self.f["legacyAgent"])
        new = record.get("applied") or {}
        assert record.get("state") == "applied" and new.get("sandboxId") not in (None, old["sandboxId"]), "no fresh sandbox was applied"
        assert new.get("workloadWorkspacePath") == self.w, "new binding is not the W layout"
        fault = self.image_fault(new, self.f["replacementImage"])
        assert fault is None, f"new binding is not on the replacement image: {fault}"
        # The old identity stays exactly as recorded: same UUID, same recorded image reference, no persisted path.
        assert any(s.get("sandboxId") == old["sandboxId"] and s.get("image") == old["image"] and "workloadWorkspacePath" not in s
                   for s in record.get("superseded", [])), "old custody not retained as recorded"
        code, target, _ = self.download(new, self.w, "a3-new")
        assert code == 0, "unmodified CLI could not download W"
        carried = manifest(target)
        carried.pop(".mcp.json", None)
        assert carried == self.portable(self.state["legacy"]["/workspace"]), "W is not the legacy tree's portable part"
        assert self.observe(old) == "Stopped", "old sandbox is not stopped"
        with self.staged(old):
            after = self.remote_manifest(old, "/workspace", self.home + "/app", self.home + "/trust")
        assert after == self.state["legacy"], "original legacy or control bytes changed"
        return {"oldSandboxId": old["sandboxId"], "newSandboxId": new["sandboxId"], "wSha256": digest(carried), "entries": len(carried)}

    def a2_fresh_layout(self):
        ws = self.f["freshDockerWorkspace"]
        os.makedirs(ws, exist_ok=True)
        subprocess.run(["git", "init", "-q", ws], check=True, stdin=subprocess.DEVNULL, capture_output=True)
        files = {"README.md": "synthetic\n", "src/main.py": "print('fresh')\n", "app/notes.txt": "a user app directory\n",
                 "nested/.ssh/known": "nested names are user data\n", "with space.txt": "x\n", ".gitignore": "ignored.log\n",
                 "ignored.log": "filtered by the CLI's Git filtering\n", "run.sh": "#!/bin/sh\n"}
        for rel, content in files.items():
            os.makedirs(os.path.dirname(os.path.join(ws, rel)) or ws, exist_ok=True)
            with open(os.path.join(ws, rel), "w") as handle:
                handle.write(content)
        os.chmod(os.path.join(ws, "run.sh"), 0o755)
        self.state["sentinels"] = self.seed_sentinels(ws)
        status, body = self.api("POST", f"/api/agents/{self.f['freshAgent']}/runtime/apply")
        assert status == 200, f"apply returned {status} {json.dumps(body)[:200]}"
        _, record = self.config(self.f["freshAgent"])
        applied = record.get("applied") or {}
        assert record.get("state") == "applied" and applied.get("workloadWorkspacePath") == self.w, "fresh binding is not the W layout"
        self.state["fresh"] = applied
        code, target, _ = self.download(applied, self.w, "a2")
        assert code == 0, "unmodified CLI could not download W"
        assert not os.path.exists(os.path.join(target, "workspace")), "W nested a workspace directory"
        got = manifest(target)
        assert ".mcp.json" in got, "fresh routes missing from W"
        kept = subprocess.run(["git", "-C", ws, "ls-files", "-co", "--exclude-standard", "-z"], capture_output=True, check=True).stdout
        expected = {rel for rel in kept.decode().split("\0") if rel and rel.split("/")[0] not in self.excluded}
        assert {k for k, v in got.items() if v[0] == "f"} - {".mcp.json"} == expected, "W files differ from the filtered Docker tree"
        assert "ignored.log" not in got and os.path.exists(os.path.join(ws, "ignored.log")), "Git-ignore control not omitted or not retained"
        code, settings, _ = self.download(applied, self.home + "/app/appsettings.json", "a2-settings")
        work_dir = None
        if code == 0:
            with open(os.path.join(settings, "appsettings.json")) as handle:
                work_dir = json.load(handle)["Agent"]["WorkDir"]
        assert work_dir == self.w, "generated WorkDir is not W"
        return {"sandboxId": applied["sandboxId"], "wSha256": digest(got), "entries": len(got)}

    def seed_sentinels(self, ws):
        """A script-owned sentinel for every exclusion (the entry itself for file names); nothing legitimate writes them."""
        sentinels = []
        for name in self.excluded:
            path = os.path.join(ws, name)
            if name.startswith(".fleet-") or name == ".mcp.json":
                rel = name
            else:
                os.makedirs(path, exist_ok=True)
                rel = name + "/" + SENTINEL
            with open(os.path.join(ws, rel), "w") as handle:
                handle.write("synthetic docker control " + name + "\n")
            sentinels.append(rel)
        return sentinels

    def a5_current_to_docker(self):
        applied = self.state.get("fresh")
        assert applied, "A2 did not leave a current-layout binding"
        edit = ("cd \"$1\" && printf 'edited\\n' > README.md && rm -f 'with space.txt' && rm -rf app && printf 'now a file\\n' > app "
                "&& mkdir -p empty-dir && printf 'new\\n' > src/new.txt")
        assert self.exec_ok(applied, edit, self.w, timeout=120), "user edits in W failed"
        code, target, _ = self.download(applied, self.w, "a5-w")
        assert code == 0, "W download failed"
        expected = self.portable(manifest(target))
        status, body = self.api("GET", f"/api/agents/{self.f['freshAgent']}", timeout=60)
        container = body.get("containerName") if status == 200 and isinstance(body, dict) else None
        assert container, "the agent's Docker container name is unreadable"
        # Recorded before the Docker apply, so any cleanup (failure or interruption) can find the committed workload.
        pending = self.state["dockerApply"] = {"agent": self.f["freshAgent"], "container": container, "before": self.container_id(container)}
        status, _ = self.api("PUT", f"/api/agents/{self.f['freshAgent']}/config", {"runtimeKind": "docker"})
        assert status == 200, f"desired runtime change returned {status}"
        ws = self.f["freshDockerWorkspace"]
        before = control_state(ws, self.excluded)  # immediately before the handoff, not at seeding time
        status, body = self.api("POST", f"/api/agents/{self.f['freshAgent']}/runtime/apply")
        assert status == 200, f"Docker apply returned {status} {json.dumps(body)[:200]}"
        cfg, _ = self.config(self.f["freshAgent"])
        assert cfg.get("appliedRuntimeKind") == "docker", "Docker was not committed"
        committed = self.container_id(container)
        assert committed and committed != pending["before"], "no newly provisioned Docker workload"
        pending["committed"] = committed
        code, out = self.docker(["inspect", "--format", "{{.State.StartedAt}}", committed])
        started_at = docker_time(out.decode(errors="replace")) if code == 0 else None
        after = manifest(ws)
        changes = [dict(change, cause=attribute(change, self.state.get("sentinels", []), started_at))
                   for change in control_changes(before, control_state(ws, self.excluded))]
        # The full private difference is retained before any assertion.
        self.receipt["excludedStateChanges"] = {"containerStartedAt": started_at, "changes": changes}
        assert self.portable(after) == expected, "Docker workspace does not mirror W"
        violations = [c["path"] for c in changes if c["cause"] in ("sentinel_violation", "unattributed")]
        assert not violations, f"excluded Docker state changed without an attributable cause ({len(violations)} paths; see the receipt)"
        causes = sorted({c["cause"] for c in changes})
        return {"dockerSha256": digest(self.portable(after)), "entries": len(expected), "excludedSentinelsUnchanged": True,
                "excludedChangeCauses": causes}

    def run_cases(self):
        self.case("A1 legacy control", self.a1_legacy_control)
        self.case("A3 legacy image change", self.a3_legacy_image_change, agent=self.f["legacyAgent"])
        self.case("A2 fresh layout", self.a2_fresh_layout, agent=self.f["freshAgent"])
        self.case("A5 current layout to Docker", self.a5_current_to_docker, agent=self.f["freshAgent"])

    def exit_code(self):
        results = [c["result"] for c in self.receipt["cases"].values()]
        clean = all(entry["result"] != "error" for entry in self.receipt["cleanup"])
        if self.interrupted or "INTERRUPTED" in results:
            return 130
        if results and all(r == "PASS" for r in results) and clean:
            return 0
        return 2 if "BLOCKED" in results else 1

    def write_receipt(self, directory):
        self.receipt["finished"] = now()
        if self.interrupted:
            self.receipt["interrupted"] = self.interrupted
        path = os.path.join(directory, "workspace-transfer-" + time.strftime("%Y%m%dT%H%M%SZ", time.gmtime()) + ".json")
        with os.fdopen(os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), "w") as handle:
            json.dump(self.receipt, handle, indent=2, sort_keys=True)
        return path


def main(argv):
    if len(argv) != 2:
        print("usage: test-stock-workspace-transfer.sh PRIVATE-FIXTURE.json", file=sys.stderr)
        return 2
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    try:
        fixture = load_fixture(argv[1])
        token = os.environ.get(fixture["tokenEnv"], "")
        if not token:
            raise Blocked("the named token environment variable is unset")
        for tool in ("curl", "ssh", "git", "docker"):
            if shutil.which(tool) is None:
                raise Blocked(f"{tool} not on PATH (the pinned CLI spawns ssh and git; image identity is read from Docker)")
        if not os.path.isfile(fixture["cli"]) or hashlib.sha256(open(fixture["cli"], "rb").read()).hexdigest() != PINNED_CLI_SHA256:
            raise Blocked("cli is not the pinned OpenShell 0.1.5 binary")
        regression = Regression(fixture, root, token, work="")
        version = regression.cli(["--version"], timeout=30)
        if version[0] != 0 or not re.match(rb"openshell 0\.1\.5([+ \n]|$)", version[1]):
            raise Blocked("pinned CLI does not report 0.1.5")
        if regression.cli(["sandbox", "list", "-o", "json", "--page-size", "1"], timeout=60)[0] != 0:
            raise Blocked("gateway unreachable with the supplied client configuration")
        regression.check_fixture_state()
    except Blocked as error:
        print(f"BLOCKED: {error}")
        return 2
    regression.receipt["cli"] = {"sha256": PINNED_CLI_SHA256, "version": version[1].decode().strip()}
    regression.receipt["images"] = {"baseline": fixture["baselineImage"], "replacement": fixture["replacementImage"]}
    regression.work = tempfile.mkdtemp(prefix="work-", dir=fixture["receiptDir"])
    regression.install_signal_handlers()
    try:
        regression.run_cases()
    except (Interrupted, KeyboardInterrupt):
        regression.interrupted = regression.interrupted or "SIGINT"  # every case's cleanup already ran in its finally
    finally:
        regression.cleaning = True  # a late signal never truncates the receipt
        shutil.rmtree(regression.work, ignore_errors=True)  # owned downloads only; fixture state is never cleaned up here
        regression.write_receipt(fixture["receiptDir"])
    code = regression.exit_code()
    cleanup_errors = sum(entry["result"] == "error" for entry in regression.receipt["cleanup"])
    print(f"cleanup errors: {cleanup_errors}")
    print("RESULT: " + {0: "PASS", 2: "BLOCKED", 130: "INTERRUPTED"}.get(code, "FAIL"))
    return code


if __name__ == "__main__":
    sys.exit(main(sys.argv))
