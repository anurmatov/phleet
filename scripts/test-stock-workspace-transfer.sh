#!/usr/bin/env bash
# #507 operator regression: the managed workspace layout through the REAL pinned stock CLI and adapter, against an
# explicitly supplied, already provisioned, isolated fixture. It never creates fixtures, selects a live identity,
# looks up credentials implicitly, or changes production configuration, networks or grants.
#
#   bash scripts/test-stock-workspace-transfer.sh PRIVATE-FIXTURE.json
#
# The fixture file is an owner-only (0600) regular JSON file owned by the caller, with exactly these keys:
#   orchestratorUrl       the fixture orchestrator (exact candidate image, managed mode enabled there)
#   tokenEnv              NAME of the environment variable holding that orchestrator's operator token (never the value)
#   cli, cliConfigDir     the pinned OpenShell 0.1.5 binary and its private client configuration (XDG_CONFIG_HOME)
#   workloadHome          the stock image HOME (normally /home/agent)
#   freshAgent            synthetic agent: desired openshell, applied docker, empty/absent Docker workspace
#   freshDockerWorkspace  that agent's owned Docker workspace path
#   legacyAgent           synthetic agent: a retained revoked/stopped binding without a persisted path (legacy layout)
#   baselineImage         the legacy binding's immutable image (name@sha256:...)
#   replacementImage      the immutable image the fixture orchestrator is configured with (name@sha256:...)
#   receiptDir            owner-only (0700) directory for the private receipt
#
# Cases, in order: A1 legacy control (the pinned CLI refuses /workspace), A3 legacy image change, A2 fresh layout,
# A5/A6 current layout to Docker. Exit 0 only when every case is PASS; 1 when any case FAILs; 2 BLOCKED (missing or
# malformed fixture, runtime, gateway or tool) before any mutating request. stdout carries results and booleans only;
# manifests, identities and hashes go to the private receipt. Needs bash, python3, curl, ssh and git.
set -euo pipefail
[[ $# -eq 1 ]] || { echo "usage: $0 PRIVATE-FIXTURE.json" >&2; exit 2; }
ROOT=$(cd "$(dirname "$0")/.." && pwd)
exec python3 - "$1" "$ROOT" <<'PY'
import datetime, hashlib, json, os, re, shutil, stat, subprocess, sys, tempfile, time, urllib.error, urllib.request

PINNED_CLI_SHA256 = "446f766842d2bf03abc37b56a3f5a3e46b96140b5b6c305d3f168d67921fd271"
KEYS = {"orchestratorUrl", "tokenEnv", "cli", "cliConfigDir", "workloadHome", "freshAgent", "freshDockerWorkspace",
        "legacyAgent", "baselineImage", "replacementImage", "receiptDir"}
fixture_path, root = sys.argv[1], sys.argv[2]
receipt = {"cases": {}, "started": datetime.datetime.now(datetime.timezone.utc).isoformat()}


class Blocked(Exception):
    pass


def blocked(reason):
    print(f"BLOCKED: {reason}")
    sys.exit(2)


# ── fixture and prerequisites: nothing below mutates anything until every check passes ──
def load_fixture():
    try:
        info = os.lstat(fixture_path)
    except OSError:
        blocked("fixture file missing")
    if not stat.S_ISREG(info.st_mode) or info.st_uid != os.geteuid() or info.st_mode & 0o077:
        blocked("fixture must be a regular, caller-owned, owner-only file")
    try:
        data = json.load(open(fixture_path))
    except (OSError, ValueError):
        blocked("fixture is not valid JSON")
    if not isinstance(data, dict) or set(data) != KEYS or not all(isinstance(v, str) and v for v in data.values()):
        blocked("fixture keys must be exactly: " + ", ".join(sorted(KEYS)))
    for key in ("cli", "cliConfigDir", "workloadHome", "freshDockerWorkspace", "receiptDir"):
        if not os.path.isabs(data[key]) or os.path.normpath(data[key]) != data[key]:
            blocked(f"{key} must be a canonical absolute path")
    for key in ("freshAgent", "legacyAgent"):
        if not re.fullmatch(r"[a-z0-9][a-z0-9-]{0,40}", data[key]):
            blocked(f"{key} is not a supported agent name")
    for key in ("baselineImage", "replacementImage"):
        if not re.fullmatch(r"\S+@sha256:[0-9a-f]{64}", data[key]):
            blocked(f"{key} must be an immutable name@sha256 reference")
    if data["baselineImage"] == data["replacementImage"] or data["freshAgent"] == data["legacyAgent"]:
        blocked("the fixture needs two distinct images and two distinct agents")
    if not re.fullmatch(r"https?://[^\s/]+", data["orchestratorUrl"].rstrip("/")):
        blocked("orchestratorUrl must be an origin")
    receipt_dir = data["receiptDir"]
    if not os.path.isdir(receipt_dir) or os.path.islink(receipt_dir) or os.stat(receipt_dir).st_uid != os.geteuid() \
            or os.stat(receipt_dir).st_mode & 0o077:
        blocked("receiptDir must be an existing, caller-owned 0700 directory")
    return data


F = load_fixture()
TOKEN = os.environ.get(F["tokenEnv"], "")
if not TOKEN:
    blocked("the named token environment variable is unset")
for tool in ("curl", "ssh", "git"):
    if shutil.which(tool) is None:
        blocked(f"{tool} not on PATH (the pinned CLI spawns ssh and git)")
if not os.path.isfile(F["cli"]) or hashlib.sha256(open(F["cli"], "rb").read()).hexdigest() != PINNED_CLI_SHA256:
    blocked("cli is not the pinned OpenShell 0.1.5 binary")
HOME = F["workloadHome"]
W = HOME + "/workspace"
text = open(os.path.join(root, "src/Fleet.Orchestrator/Services/StockOpenShellRuntime.cs")).read()
EXCLUDED = re.findall(r'"([^"]+)"', re.search(r"PrivateWorkspaceEntries = \[(.*?)\];", text, re.S).group(1))
WORK = ""  # owned download area, created only once every prerequisite passed


def cli(args, timeout=300, scope=None):
    env = {k: v for k, v in os.environ.items() if k not in ("OPENSHELL_GATEWAY_INSECURE", "OPENSHELL_GATEWAY_ENDPOINT", "OPENSHELL_GATEWAY")}
    env["XDG_CONFIG_HOME"] = F["cliConfigDir"]
    prefix = ["--workspace", scope] if scope else []
    try:
        run = subprocess.run([F["cli"], *prefix, *args], env=env, stdin=subprocess.DEVNULL, capture_output=True, timeout=timeout)
    except subprocess.TimeoutExpired:
        return 124, b"", b"timeout"
    return run.returncode, run.stdout, run.stderr


def api(method, path, body=None):
    request = urllib.request.Request(F["orchestratorUrl"].rstrip("/") + path, method=method,
                                     data=None if body is None else json.dumps(body).encode(),
                                     headers={"Authorization": "Bearer " + TOKEN, "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=900) as response:
            return response.status, json.loads(response.read() or b"null")
    except urllib.error.HTTPError as error:
        try:
            return error.code, json.loads(error.read() or b"null")
        except ValueError:
            return error.code, None
    except (urllib.error.URLError, OSError, ValueError):
        return 0, None


def config(agent):
    status, body = api("GET", f"/api/agents/{agent}/config")
    if status != 200 or not isinstance(body, dict):
        raise Blocked(f"agent {agent} config unreadable")
    record = json.loads(body.get("appliedRuntimeBinding") or "{}")
    return body, record


version = cli(["--version"], timeout=30)
if version[0] != 0 or not re.match(rb"openshell 0\.1\.5([+ \n]|$)", version[1]):
    blocked("pinned CLI does not report 0.1.5")
if cli(["sandbox", "list", "-o", "json", "--page-size", "1"], timeout=60)[0] != 0:
    blocked("gateway unreachable with the supplied client configuration")
try:
    fresh_cfg, fresh_rec = config(F["freshAgent"])
    legacy_cfg, legacy_rec = config(F["legacyAgent"])
except Blocked as error:
    blocked(str(error))
old = legacy_rec.get("applied") or {}
if fresh_cfg.get("runtimeKind") != "openshell" or fresh_cfg.get("appliedRuntimeKind") not in ("docker", None, ""):
    blocked("freshAgent must be desired openshell and applied docker")
docker_ws = F["freshDockerWorkspace"]
if os.path.islink(docker_ws) or (os.path.exists(docker_ws) and (not os.path.isdir(docker_ws) or os.listdir(docker_ws))):
    blocked("freshDockerWorkspace must be absent or an empty real directory")
if legacy_cfg.get("appliedRuntimeKind") != "openshell" or legacy_rec.get("state") not in ("revoked", "stopped") \
        or legacy_rec.get("candidate") or "workloadWorkspacePath" in old or old.get("image") != F["baselineImage"]:
    blocked("legacyAgent must hold a revoked or stopped legacy binding on baselineImage")
receipt["cli"] = {"sha256": PINNED_CLI_SHA256, "version": version[1].decode().strip()}
receipt["images"] = {"baseline": F["baselineImage"], "replacement": F["replacementImage"]}

# ── shared helpers ──
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
                rows[rel] = ["f", hashlib.sha256(open(path, "rb").read()).hexdigest(), bool(mode & stat.S_IXUSR)]
            else:
                rows[rel] = ["other"]
    return rows
if __name__ == "__main__":
    print(json.dumps({top: manifest(top) for top in sys.argv[1:]}))
'''
_namespace = {"__name__": "manifest"}
exec(MANIFEST, _namespace)  # the same source defines manifest() here and runs inside the sandbox
manifest = _namespace["manifest"]


def digest(rows):
    return hashlib.sha256(json.dumps(rows, sort_keys=True).encode()).hexdigest()


def remote_manifest(name, scope, *paths):
    code, out, _ = cli(["sandbox", "exec", "-n", name, "--", "python3", "-c", MANIFEST, *paths], scope=scope)
    if code != 0:
        raise AssertionError("in-sandbox manifest failed")
    return json.loads(out.decode().strip().splitlines()[-1])


def phase(name, scope):
    code, out, _ = cli(["sandbox", "get", name, "-o", "json"], timeout=60, scope=scope)
    return json.loads(out).get("phase") if code == 0 else None


def wait(predicate, seconds=180):
    end = time.time() + seconds
    while time.time() < end:
        if predicate():
            return True
        time.sleep(2)
    return False


def staged_start(name, scope):
    """Boots a retained, revoked sandbox into its staged launcher only (no activation, daemon or model turn)."""
    if phase(name, scope) != "Ready" and cli(["sandbox", "start", name], scope=scope)[0] != 0:
        raise AssertionError("staged start failed")
    staged = lambda: cli(["sandbox", "exec", "-n", name, "--", "sh", "-c", 'test -f "$HOME/.fleet-staged" && test ! -f "$HOME/.fleet-ready"'],
                         timeout=60, scope=scope)[0] == 0
    if not wait(staged):
        raise AssertionError("sandbox never reported staged")


def stop(name, scope):
    cli(["sandbox", "stop", name], scope=scope)
    if not wait(lambda: phase(name, scope) == "Stopped"):
        raise AssertionError("sandbox did not stop")


def download(name, scope, source, label):
    target = os.path.join(WORK, label)
    os.makedirs(target)  # an existing directory: a tree's contents or a file both land inside it
    code, _, _ = cli(["sandbox", "download", name, source, target], scope=scope)
    return code, target


def portable(rows):
    return {k: v for k, v in rows.items() if k.split("/")[0] not in EXCLUDED}


def case(name, function):
    try:
        detail = function()
        receipt["cases"][name] = {"result": "PASS", **(detail or {})}
    except AssertionError as error:
        receipt["cases"][name] = {"result": "FAIL", "reason": str(error)}
    except Blocked as error:
        receipt["cases"][name] = {"result": "BLOCKED", "reason": str(error)}
    print(f"{name}: {receipt['cases'][name]['result']}")


scope = old.get("sandboxWorkspace")
state = {}


def a1_legacy_control():
    staged_start(old["sandboxName"], scope)
    before = remote_manifest(old["sandboxName"], scope, "/workspace", HOME + "/app", HOME + "/trust")
    state["legacy"] = before
    os.makedirs(os.path.join(WORK, "a1"))
    code, _, err = cli(["sandbox", "download", old["sandboxName"], "/workspace", os.path.join(WORK, "a1")], scope=scope)
    stop(old["sandboxName"], scope)
    assert code != 0 and b"outside the sandbox workspace" in err, "pinned CLI did not refuse the legacy root"
    return {"refused": True, "legacySha256": digest(before["/workspace"]), "entries": len(before["/workspace"])}


def a3_legacy_image_change():
    assert "legacy" in state, "A1 did not record the legacy tree"
    status, _ = api("POST", f"/api/agents/{F['legacyAgent']}/revoke")
    assert status == 200, f"revoke returned {status}"
    status, body = api("POST", f"/api/agents/{F['legacyAgent']}/runtime/apply")
    assert status == 200, f"apply returned {status} {json.dumps(body)[:200]}"
    _, record = config(F["legacyAgent"])
    new = record.get("applied") or {}
    assert record.get("state") == "applied" and new.get("sandboxId") != old["sandboxId"], "no fresh sandbox was applied"
    assert new.get("image") == F["replacementImage"] and new.get("workloadWorkspacePath") == W, "new binding is not the W layout on the replacement image"
    assert any(s.get("sandboxId") == old["sandboxId"] and "workloadWorkspacePath" not in s for s in record.get("superseded", [])), "old custody not retained"
    code, target = download(new["sandboxName"], new.get("sandboxWorkspace"), W, "a3-new")
    assert code == 0, "unmodified CLI could not download W"
    carried = manifest(target)
    carried.pop(".mcp.json", None)
    assert carried == portable(state["legacy"]["/workspace"]), "W is not the legacy tree's portable part"
    assert phase(old["sandboxName"], scope) == "Stopped", "old sandbox is not stopped"
    staged_start(old["sandboxName"], scope)
    after = remote_manifest(old["sandboxName"], scope, "/workspace", HOME + "/app", HOME + "/trust")
    stop(old["sandboxName"], scope)
    assert after == state["legacy"], "original legacy or control bytes changed"
    return {"oldSandboxId": old["sandboxId"], "newSandboxId": new["sandboxId"], "wSha256": digest(carried), "entries": len(carried)}


def a2_fresh_layout():
    os.makedirs(docker_ws, exist_ok=True)
    for args in (["init", "-q", docker_ws],):
        subprocess.run(["git", *args], check=True, stdin=subprocess.DEVNULL, capture_output=True)
    files = {"README.md": "synthetic\n", "src/main.py": "print('fresh')\n", "app/notes.txt": "a user app directory\n",
             "nested/.ssh/known": "nested names are user data\n", "with space.txt": "x\n", ".gitignore": "ignored.log\n",
             "ignored.log": "filtered by the CLI's Git filtering\n", "run.sh": "#!/bin/sh\n"}
    for rel, content in files.items():
        os.makedirs(os.path.dirname(os.path.join(docker_ws, rel)) or docker_ws, exist_ok=True)
        open(os.path.join(docker_ws, rel), "w").write(content)
    os.chmod(os.path.join(docker_ws, "run.sh"), 0o755)
    for name in EXCLUDED:  # destination sentinels for every exclusion
        path = os.path.join(docker_ws, name)
        if name.startswith(".fleet-") or name == ".mcp.json":
            open(path, "w").write("synthetic docker control\n")
        else:
            os.makedirs(path, exist_ok=True)
            open(os.path.join(path, "sentinel"), "w").write("synthetic docker control\n")
    state["dockerControl"] = {k: v for k, v in manifest(docker_ws).items() if k.split("/")[0] in EXCLUDED}
    status, body = api("POST", f"/api/agents/{F['freshAgent']}/runtime/apply")
    assert status == 200, f"apply returned {status} {json.dumps(body)[:200]}"
    _, record = config(F["freshAgent"])
    applied = record.get("applied") or {}
    assert record.get("state") == "applied" and applied.get("workloadWorkspacePath") == W, "fresh binding is not the W layout"
    state["fresh"] = applied
    code, target = download(applied["sandboxName"], applied.get("sandboxWorkspace"), W, "a2")
    assert code == 0, "unmodified CLI could not download W"
    assert not os.path.exists(os.path.join(target, "workspace")), "W nested a workspace directory"
    got = manifest(target)
    assert ".mcp.json" in got, "fresh routes missing from W"
    kept = subprocess.run(["git", "-C", docker_ws, "ls-files", "-co", "--exclude-standard", "-z"], capture_output=True, check=True).stdout
    expected = {rel for rel in kept.decode().split("\0") if rel and rel.split("/")[0] not in EXCLUDED}
    assert {k for k, v in got.items() if v[0] == "f"} - {".mcp.json"} == expected, "W files differ from the filtered Docker tree"
    assert "ignored.log" not in got and os.path.exists(os.path.join(docker_ws, "ignored.log")), "Git-ignore control not omitted or not retained"
    code, settings = download(applied["sandboxName"], applied.get("sandboxWorkspace"), HOME + "/app/appsettings.json", "a2-settings")
    work_dir = json.load(open(os.path.join(settings, "appsettings.json")))["Agent"]["WorkDir"] if code == 0 else None
    assert work_dir == W, "generated WorkDir is not W"
    return {"sandboxId": applied["sandboxId"], "wSha256": digest(got), "entries": len(got)}


def a5_current_to_docker():
    applied = state.get("fresh")
    assert applied, "A2 did not leave a current-layout binding"
    edit = ("cd \"$1\" && printf 'edited\\n' > README.md && rm -f 'with space.txt' && rm -rf app && printf 'now a file\\n' > app "
            "&& mkdir -p empty-dir && printf 'new\\n' > src/new.txt")
    code, _, _ = cli(["sandbox", "exec", "-n", applied["sandboxName"], "--", "sh", "-c", edit, "edit", W], scope=applied.get("sandboxWorkspace"))
    assert code == 0, "user edits in W failed"
    code, target = download(applied["sandboxName"], applied.get("sandboxWorkspace"), W, "a5-w")
    assert code == 0, "W download failed"
    expected = portable(manifest(target))
    status, _ = api("PUT", f"/api/agents/{F['freshAgent']}/config", {"runtimeKind": "docker"})
    assert status == 200, f"desired runtime change returned {status}"
    status, body = api("POST", f"/api/agents/{F['freshAgent']}/runtime/apply")
    assert status == 200, f"Docker apply returned {status} {json.dumps(body)[:200]}"
    cfg, _ = config(F["freshAgent"])
    assert cfg.get("appliedRuntimeKind") == "docker", "Docker was not committed"
    after = manifest(docker_ws)
    assert portable(after) == expected, "Docker workspace does not mirror W"
    assert {k: v for k, v in after.items() if k.split("/")[0] in EXCLUDED} == state["dockerControl"], "excluded Docker state changed"
    return {"dockerSha256": digest(portable(after)), "entries": len(expected), "excludedUnchanged": True}


WORK = tempfile.mkdtemp(prefix="work-", dir=F["receiptDir"])
try:
    case("A1 legacy control", a1_legacy_control)
    case("A3 legacy image change", a3_legacy_image_change)
    case("A2 fresh layout", a2_fresh_layout)
    case("A5 current layout to Docker", a5_current_to_docker)
finally:
    shutil.rmtree(WORK, ignore_errors=True)  # owned downloads only; fixture state is never cleaned up here
    receipt["finished"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
    path = os.path.join(F["receiptDir"], "workspace-transfer-" + time.strftime("%Y%m%dT%H%M%SZ", time.gmtime()) + ".json")
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, "w") as handle:
        json.dump(receipt, handle, indent=2, sort_keys=True)
results = [c["result"] for c in receipt["cases"].values()]
print("RESULT: " + ("PASS" if results and all(r == "PASS" for r in results) else "FAIL"))
sys.exit(0 if results and all(r == "PASS" for r in results) else (2 if "BLOCKED" in results else 1))
PY
