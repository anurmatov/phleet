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
#   baselineImage         the pinned digest (name@sha256:...) of the legacy binding's image
#   replacementImage      the pinned digest (name@sha256:...) of the image the fixture orchestrator is configured with
#
# Bindings keep the image reference they recorded (a tag or a digest), never rewritten here. Image identity is the
# resolved local image ID: the pinned digest, the binding's recorded reference and the image its workload container
# (stock label openshell.ai/sandbox-id) was created from must all be the same, else BLOCKED (legacy) or FAIL (replacement).
#   receiptDir            owner-only (0700) directory for the private receipt
#
# Cases, in order: A1 legacy control (the pinned CLI refuses /workspace), A3 legacy image change, A2 fresh layout,
# A5/A6 current layout to Docker. Exit 0 only when every case is PASS; 1 when any case FAILs; 2 BLOCKED (missing or
# malformed fixture, runtime, gateway or tool) before any mutating request. stdout carries results and booleans only;
# manifests, identities and hashes go to the private receipt. Needs bash, python3, curl, ssh, git and read-only Docker
# access to the gateway host (image and label lookups).
#
# Every fixture mutation (start, stop, exec, download) first proves the exact persisted sandbox UUID and name in its
# persisted scope on the supplied gateway; a fixture that disagrees with that gateway is BLOCKED before anything
# starts. A sandbox this script starts is always stopped again, and a failed or interrupted case revokes its agent
# through the orchestrator, or, once A5's Docker handoff committed, stops exactly the newly provisioned Docker container;
# each cleanup action has one 120-second deadline, every cleanup result is kept in the private
# receipt and a cleanup error fails the run. SIGINT/SIGTERM run the same cleanup (a further signal waits for it), the
# receipt is still written, and the run exits 130.
# Logic: scripts/stock_workspace_transfer.py (unit-tested by tests/scripts/test_stock_workspace_transfer.py).
set -euo pipefail
[[ $# -eq 1 ]] || { echo "usage: $0 PRIVATE-FIXTURE.json" >&2; exit 2; }
exec python3 "$(cd "$(dirname "$0")" && pwd)/stock_workspace_transfer.py" "$1"
