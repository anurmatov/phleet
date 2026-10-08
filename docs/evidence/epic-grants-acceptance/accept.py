"""#436 isolated acceptance run: spec "Before merge" steps 1-6 and 4b on a throwaway stack.

Real: Temporal dev server, MySQL 8.0, RabbitMQ, the orchestrator and bridge built from the exact
head, the seed definitions as loaded by the orchestrator, the bridge MCP tool and the orchestrator
REST API. Simulated: agents (fake_agent.py) and GitHub (github_stub.py, state.json).
"""
import os
DIR = os.environ.get("ACCEPT_DIR", "/tmp/epic-grants-accept")

import hashlib, json, subprocess, sys, time, urllib.error, urllib.request
from datetime import datetime, timedelta, timezone

import mcp

ORCH = "http://127.0.0.1:3600"
TOKEN = "accept436-admin-token"
STATE = os.path.join(DIR, "state.json")
REPO = "example-org/accept-private"
EVIDENCE = open(os.path.join(DIR, "evidence.log"), "w", buffering=1)
results = []


def log(msg):
    line = f"{datetime.now(timezone.utc).strftime('%H:%M:%S')} {msg}"
    print(line, flush=True)
    EVIDENCE.write(line + "\n")


def check(name, cond, detail=""):
    results.append((name, bool(cond)))
    log(f"{'PASS' if cond else 'FAIL'} {name} {detail}")


def http(method, path, body=None, raw=None):
    data = raw.encode() if raw is not None else (json.dumps(body).encode() if body is not None else None)
    req = urllib.request.Request(ORCH + path, data, {"Content-Type": "application/json", "Authorization": f"Bearer {TOKEN}"}, method=method)
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            txt = r.read().decode()
            return r.status, (json.loads(txt) if txt.strip() else None)
    except urllib.error.HTTPError as e:
        txt = e.read().decode()
        return e.code, (json.loads(txt) if txt.strip().startswith(("{", "[")) else txt)


def describe(wf):
    out = subprocess.run([os.path.join(DIR, "temporal"), "workflow", "describe", "-w", wf, "--address", "127.0.0.1:7233",
                          "--namespace", "fleet", "-o", "json"], capture_output=True, text=True)
    if out.returncode != 0:
        return None
    d = json.loads(out.stdout)
    info = d.get("workflowExecutionInfo", {})
    attrs = {}
    for k, v in (info.get("searchAttributes", {}).get("indexedFields", {}) or {}).items():
        try:
            import base64
            attrs[k] = json.loads(base64.b64decode(v["data"]).decode())
        except Exception:
            attrs[k] = v
    return {"status": info.get("status"), "runId": info.get("execution", {}).get("runId"), "attrs": attrs}


def wait_for(pred, what, timeout=180):
    end = time.time() + timeout
    while time.time() < end:
        v = pred()
        if v:
            return v
        time.sleep(1)
    raise TimeoutError(what)


def wait_gate(wf, visit):
    return wait_for(lambda: (d := describe(wf)) and d["attrs"].get("GateVisit") == visit and d, f"{wf} at {visit}")


def wait_closed(wf):
    return wait_for(lambda: (d := describe(wf)) and d["status"] != "WORKFLOW_EXECUTION_STATUS_RUNNING" and d, f"{wf} closed")


def calls(wf=None, prefix=None):
    """Delegations the scripted agents received, filtered by the [fleet-wf:Type:Id] tag."""
    out = []
    for line in open(os.path.join(DIR, "calls.jsonl")):
        c = json.loads(line)
        tag = c.get("wf") or ""
        if (wf and tag.endswith(":" + wf)) or (prefix and tag.startswith(prefix)):
            out.append(c)
    return out


def steps(wf):
    return [c["step"] for c in calls(wf)]


def result(wf):
    """The run's result payload, decoded: None for a null result."""
    import base64
    out = subprocess.run([os.path.join(DIR, "temporal"), "workflow", "result", "-w", wf, "--address", "127.0.0.1:7233",
                          "--namespace", "fleet", "-o", "json"], capture_output=True, text=True)
    payload = json.loads(out.stdout)["result"]
    # The CLI decodes JSON payloads itself; a null result stays an undecoded binary/null payload.
    if isinstance(payload, dict) and "metadata" in payload and "data" not in payload:
        if base64.b64decode(payload["metadata"]["encoding"]).decode() == "binary/null":
            return None
    return payload


def wakeups():
    """blocker-resolved signals the gated runs sent the driver: {blockerRef: decision}."""
    import base64
    out = subprocess.run([os.path.join(DIR, "temporal"), "workflow", "show", "-w", "accept436-driver", "--address", "127.0.0.1:7233",
                          "--namespace", "fleet", "-o", "json"], capture_output=True, text=True)
    found = {}
    for e in json.loads(out.stdout)["events"]:
        a = e.get("workflowExecutionSignaledEventAttributes")
        if a and a.get("signalName") == "blocker-resolved":
            body = json.loads(base64.b64decode(a["input"]["payloads"][0]["data"]).decode())
            found[body.get("blockerRef")] = body.get("decision")
    return found


def state(update=None):
    s = json.load(open(STATE))
    if update:
        update(s)
        json.dump(s, open(STATE, "w"), indent=1, sort_keys=True)
    return s


def start(wf_type, wf_id, inp):
    code, body = http("POST", "/api/workflows/start", {"workflowType": wf_type, "namespace": "fleet", "taskQueue": "fleet",
                                                       "workflowId": wf_id, "input": inp})
    log(f"start {wf_type} {wf_id} -> {code}")
    assert code in (200, 201), body


def human(wf, wf_type, signal, payload):
    code, body = http("POST", f"/api/workflows/fleet/signal/{wf}", {"signalName": signal, "workflowType": wf_type,
                                                                    "payload": json.dumps(payload)})
    log(f"human (dashboard REST) {signal} {payload.get('Decision')} -> {code}")
    return code


cto = mcp.Mcp("cto-agent")
cto.init()


def delegate(wf, gate, grant, visit, ref):
    args = json.dumps({"Decision": "approved", "GrantId": grant, "VisitId": visit, "ArtifactRef": ref,
                       "Evidence": "https://example.com/accept436/review"})
    out = cto.call("temporal_signal_workflow", {"workflow_id": wf, "signal_name": gate, "args": args})
    log(f"CTO delegated {gate} {wf} visit={visit} ref={ref[:12]} -> {out}")
    try:
        return json.loads(out)
    except json.JSONDecodeError:
        return {"error": out}


def pr_input(issue):
    return {"Repo": REPO, "IssueNumber": issue, "TargetAgent": "impl-agent", "ConsensusAgents": "reviewer-a",
            "DocPrepAgent": "prep-agent", "WaiterWorkflowId": "accept436-driver"}


def design_input(issue):
    return {"Repo": REPO, "ExistingIssueNumber": issue, "TargetAgent": "design-agent", "ConsensusAgents": "reviewer-a",
            "Description": "acceptance design", "WaiterWorkflowId": "accept436-driver"}


# ── Driver + pinned definitions ───────────────────────────────────────────────
driver_def = json.dumps({"type": "sequence", "steps": [{"type": "wait_for_signal", "name": "hold", "signalName": "stop-driver"}]})
code, _ = http("POST", "/api/workflow-definitions", {"name": "Accept436DriverWorkflow", "namespace": "fleet", "taskQueue": "fleet",
                                                     "definition": driver_def, "description": "acceptance driver"})
log(f"driver definition -> {code}")
start("Accept436DriverWorkflow", "accept436-driver", {})
driver = wait_for(lambda: describe("accept436-driver"), "driver")
log(f"driver run {driver['runId']} {driver['status']}")

pins = []
for name in ("UwePrImplementationWorkflow", "UweDesignWorkflow", "UweDocMaintenanceWorkflow"):
    code, d = http("GET", f"/api/workflow-definitions/{name}")
    digest = hashlib.sha256(d["definition"].encode()).hexdigest()
    pins.append({"type": name, "version": d["version"], "sha256": digest})
    log(f"pin {name} v{d['version']} {digest[:16]}")

scope = {
    "driver": {"namespace": "fleet", "workflowId": "accept436-driver", "runId": driver["runId"]},
    "targets": [{"repo": REPO, "issues": [101, 102, 201, 202, 203, 204]}],
    "gates": ["design-approval", "merge-approval", "doc-review"],
    "workflows": pins,
    "expiresAt": (datetime.now(timezone.utc) + timedelta(days=2)).strftime("%Y-%m-%dT%H:%M:%SZ"),
}

# ── Step 1: create a grant ────────────────────────────────────────────────────
code, report = http("POST", "/api/epic-grants/validate", scope)
check("1 validate report is valid", code == 200 and report["valid"], json.dumps(report.get("errors")))
denied = dict(scope, targets=[{"repo": "example-org/accept-denied", "issues": [1], "allowPublic": True}])
code, bad = http("POST", "/api/epic-grants", denied)
check("1 a denied repo with allowPublic is refused and nothing stored", code == 400 and not bad["valid"], json.dumps(bad.get("errors")))
code, grant = http("POST", "/api/epic-grants", scope)
check("1 grant created", code == 201 and grant["effectiveStatus"] == "active", f"id={grant.get('id')}")
GRANT = grant["id"]
code, grants = http("GET", "/api/epic-grants")
check("1 exactly one grant stored", code == 200 and len(grants) == 1, f"count={len(grants)}")

# ── Step 2: design gate approved by delegation ────────────────────────────────
start("UweDesignWorkflow", "accept436-design-101", design_input(101))
d = wait_gate("accept436-design-101", "design-approval:1")
ref = d["attrs"].get("ReviewRef")
check("2 design gate parked with an attested ReviewRef", bool(ref) and ref == state()["bodies"]["101"], f"ref={ref}")
r = delegate("accept436-design-101", "design-approval", GRANT, "design-approval:1", ref)
check("2 delegated design approval sent", r.get("result") == "sent", json.dumps(r))
d = wait_closed("accept436-design-101")
res = result("accept436-design-101")
check("2 design run approved (IssueNumber result, body check ran)",
      "101" in json.dumps(res) and "verify_approved_body" in steps("accept436-design-101"), json.dumps(res))

# ── Step 4b: issue-body edit after an approved design review ──────────────────
start("UweDesignWorkflow", "accept436-design-102", design_input(102))
d = wait_gate("accept436-design-102", "design-approval:1")
ref = d["attrs"].get("ReviewRef")
state(lambda s: s["bodies"].__setitem__("102", hashlib.sha256(b"issue102-edited-out-of-band").hexdigest()))
log("4b issue 102 body edited out of band after the approved review")
r = delegate("accept436-design-102", "design-approval", GRANT, "design-approval:1", ref)
check("4b decision still names the reviewed ref and is sent", r.get("result") == "sent", json.dumps(r))
wait_closed("accept436-design-102")
st = steps("accept436-design-102")
res = result("accept436-design-102")
check("4b ERROR:approved_artifact_changed: notice sent, no approval emitted, null result",
      "notify_approved_body_changed" in st and "emit_approved_result" not in st and "notify_ceo_approved" not in st and res is None,
      f"steps={st[-4:]} result={res}")

# ── Step 3: PR gate approved and merged with the pinned head ─────────────────
start("UwePrImplementationWorkflow", "accept436-pr-201", pr_input(201))
d = wait_gate("accept436-pr-201", "merge-approval:1")
ref = d["attrs"].get("ReviewRef")
check("3 merge gate parked with ReviewRef = PR head", ref == state()["heads"]["201"], f"ref={ref}")
r = delegate("accept436-pr-201", "merge-approval", GRANT, "merge-approval:1", ref)
check("3 delegated merge approval sent", r.get("result") == "sent", json.dumps(r))
wait_closed("accept436-pr-201")
pinned = [c for c in calls("accept436-pr-201") if c["step"] == "phase4_merge_pinned"]
check("3 merged with --match-head-commit of the reviewed head",
      state()["merged"].get("201") is True and len(pinned) == 1 and f"--match-head-commit {ref}" in pinned[0]["text"]
      and "phase4_merge" not in steps("accept436-pr-201"))
doc = [c for c in calls(prefix="UweDocMaintenanceWorkflow:") if c["step"] == "prepare" and "PR #201 " in c["text"]]
check("3 doc maintenance started and prepared by PrepAgent (prep-agent)", len(doc) == 1 and doc[0]["agent"] == "prep-agent",
      doc[0]["wf"] if doc else "no doc run")

# ── Step 4: push after review, then a new review -> stale_artifact ───────────
start("UwePrImplementationWorkflow", "accept436-pr-202", pr_input(202))
d = wait_gate("accept436-pr-202", "merge-approval:1")
old_ref = d["attrs"].get("ReviewRef")
state(lambda s: s["ceo_concern_valid"].__setitem__("202", True))
human("accept436-pr-202", "UwePrImplementationWorkflow", "merge-approval", {"Decision": "changes_requested", "Comment": "please fix X"})
d = wait_gate("accept436-pr-202", "merge-approval:2")
new_ref = d["attrs"].get("ReviewRef")
check("4 the push was re-reviewed: new visit, new ReviewRef", new_ref and new_ref != old_ref and new_ref == state()["heads"]["202"],
      f"old={old_ref[:12]} new={(new_ref or '')[:12]}")
r = delegate("accept436-pr-202", "merge-approval", GRANT, "merge-approval:2", old_ref)
check("4 approval naming the old ref is refused stale_artifact", r.get("result") == "refused" and r.get("reason") == "stale_artifact", json.dumps(r))
r = delegate("accept436-pr-202", "merge-approval", GRANT, "merge-approval:1", new_ref)
check("4 approval naming the old visit is refused stale_visit", r.get("result") == "refused" and r.get("reason") == "stale_visit", json.dumps(r))
human("accept436-pr-202", "UwePrImplementationWorkflow", "merge-approval", {"Decision": "rejected", "Comment": "acceptance cleanup"})
wait_closed("accept436-pr-202")
check("4 run closed by the human rejection, nothing merged", not state()["merged"].get("202"))

# ── Step 4: push after review with no re-review -> sent, pinned merge refused ─
start("UwePrImplementationWorkflow", "accept436-pr-203", pr_input(203))
d = wait_gate("accept436-pr-203", "merge-approval:1")
ref = d["attrs"].get("ReviewRef")
state(lambda s: s["heads"].__setitem__("203", hashlib.sha256(b"pr203-pushed-after-review").hexdigest()[:40]))
log("4 PR 203 head moved after review, no re-review")
r = delegate("accept436-pr-203", "merge-approval", GRANT, "merge-approval:1", ref)
check("4 un-re-reviewed push: decision is sent (ReviewRef still the reviewed head)", r.get("result") == "sent", json.dumps(r))
wait_closed("accept436-pr-203")
st = steps("accept436-pr-203")
pinned = [c for c in calls("accept436-pr-203") if c["step"] == "phase4_merge_pinned"]
doc203 = [c for c in calls(prefix="UweDocMaintenanceWorkflow:") if "PR #203 " in c["text"]]
check("4 --match-head-commit refused the merge: failure path + notice, no merge, no doc run",
      not state()["merged"].get("203") and "notify_merge_failed" in st and len(pinned) == 1
      and f"--match-head-commit {ref}" in pinned[0]["text"] and "Head branch was modified" in pinned[0]["reply"] and not doc203)

# ── Step 5: revoke -> refused ────────────────────────────────────────────────
start("UwePrImplementationWorkflow", "accept436-pr-204", pr_input(204))
d = wait_gate("accept436-pr-204", "merge-approval:1")
ref = d["attrs"].get("ReviewRef")
code, revoked = http("POST", f"/api/epic-grants/{GRANT}/revoke", {"reason": "acceptance step 5"})
check("5 grant revoked", code == 200 and revoked["effectiveStatus"] == "revoked")
r = delegate("accept436-pr-204", "merge-approval", GRANT, "merge-approval:1", ref)
check("5 decision after revoke is refused grant_inactive, nothing sent", r.get("result") == "refused" and r.get("reason") == "grant_inactive", json.dumps(r))

# ── Step 6: no-grant gate unchanged (human dashboard path) ──────────────────
code = human("accept436-pr-204", "UwePrImplementationWorkflow", "merge-approval", {"Decision": "approved"})
wait_closed("accept436-pr-204")
st = steps("accept436-pr-204")
merge = [c for c in calls("accept436-pr-204") if c["step"] == "phase4_merge"]
check("6 human approval merges exactly as before (unpinned)", code == 200 and state()["merged"].get("204") is True
      and "phase4_merge_pinned" not in st and len(merge) == 1 and "--match-head-commit" not in merge[0]["text"])
cto_plain = cto.call("temporal_signal_workflow", {"workflow_id": "accept436-driver", "signal_name": "merge-approval",
                                                  "args": json.dumps({"Decision": "approved"})})
check("6 CTO approval without GrantId is still refused as CEO-only", "CEO-only" in cto_plain, cto_plain[:100])

# ── Decisions table ──────────────────────────────────────────────────────────
code, detail = http("GET", f"/api/epic-grants/{GRANT}")
rows = [(x["workflowId"], x["gate"], x["visitId"], x["status"]) for x in detail["decisions"]]
log(f"decisions: {rows}")
check("decision rows: exactly the 4 sent decisions, all sent, none for refusals",
      sorted(r[0] for r in rows) == ["accept436-design-101", "accept436-design-102", "accept436-pr-201", "accept436-pr-203"]
      and all(r[3] == "sent" for r in rows))

w = wakeups()
log(f"driver wakeups: {w}")
check("terminal decisions reported to the driver", w.get("accept436-design-101") == "approved"
      and w.get("accept436-design-102") == "cancelled" and w.get("accept436-pr-201") == "approved"
      and w.get("accept436-pr-202") == "rejected" and w.get("accept436-pr-204") == "approved")

failed = [n for n, ok in results if not ok]
log(f"SUMMARY {len(results) - len(failed)}/{len(results)} passed" + (f"; FAILED: {failed}" if failed else ""))
sys.exit(1 if failed else 0)
