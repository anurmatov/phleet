"""Scripted agents for the #436 isolated acceptance run.

Consumes bridge directives from the throwaway RabbitMQ (exchange fleet.group, routing key = agent
name) and answers by step name. GitHub state (PR heads, merges, issue bodies) is simulated in
state.json, which the scenario driver edits to "push" or "edit the body" out of band.
Loopback only; nothing here talks to GitHub or any production service.
"""
import os
DIR = os.environ.get("ACCEPT_DIR", "/tmp/epic-grants-accept")

import hashlib, json, re, sys, threading
from datetime import datetime, timezone
import pika

STATE = os.path.join(DIR, "state.json")
LOG = open(os.path.join(DIR, "fake_agent.log"), "a", buffering=1)
CALLS = open(os.path.join(DIR, "calls.jsonl"), "a", buffering=1)
AGENTS = ["impl-agent", "design-agent", "reviewer-a", "prep-agent", "cto-agent"]
lock = threading.Lock()


def sha(text, n=40):
    return hashlib.sha256(text.encode()).hexdigest()[:n]


def load():
    try:
        return json.load(open(STATE))
    except FileNotFoundError:
        return {"heads": {}, "merged": {}, "bodies": {}, "ceo_concern_valid": {}, "head_version": {}}


def save(state):
    json.dump(state, open(STATE, "w"), indent=1, sort_keys=True)


def num(pattern, text):
    m = re.search(pattern, text)
    return m.group(1) if m else None


def review(verdict, ref=None, blocker=None):
    lines = ["Scripted review.", "SUMMARY: scripted acceptance review", "EVIDENCE: none",
             f"BLOCKER: {blocker or 'none'}"]
    if ref:
        lines.append(f"REVIEWED_REF: {ref}")
    lines.append(f"VERDICT: {verdict}")
    return "\n".join(lines)


def answer(agent, step, text):
    with lock:
        s = load()
        pr = num(r"/pull/(\d+)", text)
        if step == "phase1_implement":
            pr = num(r"Issue number: (\d+)", text)
            s["head_version"][pr] = 1
            s["heads"][pr] = sha(f"pr{pr}-v1")
            save(s)
            return f"Implemented.\nPR_URL: https://github.com/example-org/accept-private/pull/{pr}\nHEAD_SHA: {s['heads'][pr]}"
        if step.startswith("revise_"):
            v = s["head_version"].get(pr, 1) + 1
            s["head_version"][pr] = v
            s["heads"][pr] = sha(f"pr{pr}-v{v}")
            save(s)
            return f"Revised and pushed.\nHEAD_SHA: {s['heads'][pr]}"
        if step.startswith("review-"):
            if "The CEO has reviewed" in text:
                if s["ceo_concern_valid"].get(pr, False):
                    return review("changes_requested", blocker="apply the CEO's requested change")
                return review("approved")
            ref = num(r"Artifact under review \(ReviewRef\): (\S+)", text)
            if pr and ref and ref != s["heads"].get(pr):
                return review("changes_requested", blocker=f"PR head is {s['heads'].get(pr)}, not {ref}")
            issue = num(r"design spec for GitHub issue (\d+)", text)
            if issue and ref and ref != s["bodies"].get(issue):
                return review("changes_requested", blocker="issue body changed since the author's hash")
            return review("approved", ref=ref)
        if step == "synthesis":
            return "The concern holds.\nVERDICT: changes_requested"
        if step == "phase4_merge":
            s["merged"][pr] = True
            save(s)
            return f"Merged PR #{pr} (squash)."
        if step == "phase4_merge_pinned":
            pinned = num(r"--match-head-commit ([0-9a-f]{40})", text)
            if pinned and pinned == s["heads"].get(pr):
                s["merged"][pr] = True
                save(s)
                return f"Merged PR #{pr} (squash) at {pinned}."
            return ("gh pr merge failed: GraphQL: Head branch was modified. Review and try the merge again. "
                    f"(pinned {pinned}, head {s['heads'].get(pr)}). Not retried.")
        if step == "verify_merge_status":
            # Behaves like the real gh CLI: an unsupported `merged` field is a hard error, and the
            # supported fields come back as JSON for the definition to judge.
            fields = num(r"--json\s+(\S+)", text) or ""
            if "merged" in fields.split(","):
                return 'Unknown JSON field: "merged"\nAvailable fields: ... mergeCommit mergedAt ... state ...'
            if s["merged"].get(pr):
                return ('PR_STATE_JSON: {"mergeCommit":{"oid":"%s"},"mergedAt":"%s","state":"MERGED"}'
                        % (s["heads"].get(pr), datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")))
            return 'PR_STATE_JSON: {"mergeCommit":null,"mergedAt":null,"state":"OPEN"}'
        if step == "create_or_refine":
            issue = num(r"ExistingIssueNumber from input: (\d+)", text)
            s["bodies"].setdefault(issue, sha(f"issue{issue}-v1", 64))
            save(s)
            return f"Refined the issue.\nISSUE_NUMBER: {issue}\nBODY_SHA256: {s['bodies'][issue]}"
        if step == "verify_approved_body":
            issue = num(r"current body of issue (\d+)", text)
            return f"BODY_SHA256_NOW: {s['bodies'].get(issue, '')}"
        if step == "emit_approved_result":
            return "{\"IssueNumber\": %s}" % num(r'"IssueNumber": (\d+)', text)
        if step == "prepare":
            return "PREP: NO_DOC"
        if step == "phase2_5_unresolved_check":
            return "No unresolved review comments."
        return "Noted."


def main():
    conn = pika.BlockingConnection(pika.ConnectionParameters("127.0.0.1"))
    ch = conn.channel()
    ch.exchange_declare("fleet.group", "direct", durable=True)
    for agent in AGENTS:
        q = f"accept436.{agent}"
        ch.queue_declare(q, durable=False, auto_delete=False)
        ch.queue_bind(q, "fleet.group", routing_key=agent)

        def on_message(channel, method, props, body, agent=agent):
            msg = json.loads(body)
            task = msg.get("TaskId") or ""
            step = task.split("/")[-1]
            text = msg.get("Text") or ""
            reply = answer(agent, step, text)
            wf = num(r"\[fleet-wf:([^\]]+)\]", text)
            LOG.write(f"{datetime.now(timezone.utc).isoformat()} {agent} step={step} wf={wf} -> {reply.splitlines()[-1][:120]}\n")
            CALLS.write(json.dumps({"agent": agent, "step": step, "wf": wf, "task": task, "text": text, "reply": reply}) + "\n")
            out = {"ChatId": 0, "Sender": agent, "Text": reply,
                   "Timestamp": datetime.now(timezone.utc).isoformat(), "Type": "response", "TaskId": task}
            channel.basic_publish("fleet.group", "temporal-bridge", json.dumps(out).encode(),
                                  pika.BasicProperties(delivery_mode=2))
            channel.basic_ack(method.delivery_tag)

        ch.basic_consume(q, on_message)
    LOG.write("fake agent ready\n")
    ch.start_consuming()


if __name__ == "__main__":
    main()
