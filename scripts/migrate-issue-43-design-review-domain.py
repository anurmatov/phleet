#!/usr/bin/env python3
"""
Migration: add ReviewDomain to UweDesignWorkflow's ConsensusReviewWorkflow child steps.

Issue #43 — per-domain review rubrics for ConsensusReviewWorkflow.

UweDesignWorkflow has two child_workflow steps that call ConsensusReviewWorkflow:
  - "consensus_review"     (inner consensus loop)
  - "ceo_consensus_review" (outer loop after CEO changes_requested)

This script adds "ReviewDomain": "design_review" to the args of both steps so
reviewers use the design-spec rubric instead of the default code-review rubric.

MemoryStoreRequestWorkflow delegates directly to acto (single-reviewer pattern)
and does not call ConsensusReviewWorkflow, so no update is needed there.

Usage:
  ORCHESTRATOR_URL=http://fleet-orchestrator:3600 \
  ORCHESTRATOR_AUTH_TOKEN=<token> \
  python3 scripts/migrate-issue-43-design-review-domain.py

Dry-run (print diff, no write):
  DRY_RUN=1 python3 scripts/migrate-issue-43-design-review-domain.py
"""

import json
import os
import sys
import urllib.request
import urllib.error

ORCHESTRATOR_URL = os.environ.get("ORCHESTRATOR_URL", "http://fleet-orchestrator:3600")
AUTH_TOKEN = os.environ.get("ORCHESTRATOR_AUTH_TOKEN", "")
DRY_RUN = os.environ.get("DRY_RUN", "0") == "1"
WORKFLOW_NAME = "UweDesignWorkflow"
REVIEW_DOMAIN = "design_review"


def request(method: str, path: str, body: dict | None = None) -> dict:
    url = f"{ORCHESTRATOR_URL}{path}"
    data = json.dumps(body).encode() if body else None
    headers = {"Content-Type": "application/json"}
    if AUTH_TOKEN:
        headers["Authorization"] = f"Bearer {AUTH_TOKEN}"
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req) as resp:
            return json.loads(resp.read())
    except urllib.error.HTTPError as e:
        print(f"HTTP {e.code} {method} {path}: {e.read().decode()}", file=sys.stderr)
        sys.exit(1)


def inject_domain(step: dict) -> bool:
    """Recursively find ConsensusReviewWorkflow child steps and add ReviewDomain. Returns True if any change."""
    changed = False
    if step.get("type") == "child_workflow" and step.get("workflowType") == "ConsensusReviewWorkflow":
        args = step.setdefault("args", {})
        if args.get("ReviewDomain") != REVIEW_DOMAIN:
            args["ReviewDomain"] = REVIEW_DOMAIN
            changed = True
            print(f"  + Added ReviewDomain to step '{step.get('name', '?')}'")
    for key in ("steps", "cases", "default"):
        child = step.get(key)
        if isinstance(child, list):
            for s in child:
                if inject_domain(s):
                    changed = True
        elif isinstance(child, dict):
            for s in child.values():
                if isinstance(s, dict) and inject_domain(s):
                    changed = True
    return changed


def main():
    print(f"Fetching {WORKFLOW_NAME} ...")
    wf = request("GET", f"/api/workflow-definitions/{WORKFLOW_NAME}")
    definition = json.loads(wf["definition"])
    original = json.dumps(definition, indent=2)

    print(f"Injecting ReviewDomain='{REVIEW_DOMAIN}' into ConsensusReviewWorkflow steps ...")
    changed = inject_domain(definition)

    if not changed:
        print("No changes needed — already up to date.")
        return

    updated = json.dumps(definition, indent=2)
    print("\n--- diff (first change) ---")
    for i, (a, b) in enumerate(zip(original.splitlines(), updated.splitlines())):
        if a != b:
            print(f"  - {a.strip()}")
            print(f"  + {b.strip()}")
    print("---\n")

    if DRY_RUN:
        print("DRY_RUN=1 — skipping write.")
        return

    request("PUT", f"/api/workflow-definitions/{WORKFLOW_NAME}", {
        "definition": json.dumps(definition)
    })
    print(f"Updated {WORKFLOW_NAME} successfully.")


if __name__ == "__main__":
    main()
