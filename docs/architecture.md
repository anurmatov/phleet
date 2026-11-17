# Architecture

The overview diagram and the five-step summary are in the [README](../README.md#how-it-works).

## What runs

`setup.sh` provisions the following services. Agents and most services share the `fleet-net`
Docker network; Temporal and Qdrant, which take no credential, are on networks of their own that
no agent joins ([docs/agent-identity.md](agent-identity.md#networks)):

- `rabbitmq` — message broker
- `fleet-mysql` — agent config + task history
- `qdrant` — vector store for Fleet Memory (on `fleet-memory-net`, with `fleet-memory`)
- `temporal-postgresql` — Temporal persistence (on `fleet-temporal-net`)
- `temporal-server` + `temporal-ui` — workflow engine (on `fleet-temporal-net`, with the orchestrator and the Temporal bridge)
- `fleet-minio` (+ `fleet-minio-init`) — S3-compatible store for inter-agent file sharing
- `fleet-memory` — semantic memory MCP server
- `fleet-playwright` — browser automation MCP server
- `fleet-orchestrator` — agent registry + lifecycle manager
- `fleet-temporal-bridge` — Temporal workflow runner
- `fleet-bridge` — RabbitMQ relay
- `fleet-dashboard` — web UI at http://localhost:3700

Optional, only with `FLEET_SECRETS_ENABLED=true`: `fleet-secrets` — swaps placeholders for bound
secrets on the way to their hosts, on its own internal `fleet-secrets-net`
([docs/secret-placeholders.md](secret-placeholders.md)).

## How a task flows

<p align="center">
  <img src="../.github/assets/phleet-task-flow.svg" alt="Sequence diagram showing how a task flows from your Telegram DM through the co-CTO, RabbitMQ, Temporal, and a worker agent, then back" width="900">
</p>

You DM the co-CTO. It starts a Temporal workflow via `fleet-temporal-bridge`. The workflow publishes a task directive into RabbitMQ; the worker agent picks it up from its queue, does the work (reading memory, editing code, opening a PR), and publishes the result back through RabbitMQ. The workflow resumes, notifies the co-CTO, and you get a Telegram reply with the outcome. Model Context Protocol (MCP) tool calls (start workflow, read memory) go directly from the agent to the MCP server; everything else flows through RabbitMQ.

## Consensus review

<p align="center">
  <img src="../.github/assets/phleet-consensus-review.svg" alt="Consensus review flow: multiple reviewer agents evaluate a subject in parallel, a synthesizer aggregates verdicts, then approved/rejected/changes_requested branches" width="900">
</p>

Used inside the design, PR-implementation, and memory-store workflows. Multiple reviewer agents — usually different models — evaluate the subject in parallel. A synthesizer aggregates their verdicts. `approved` proceeds, `rejected` escalates to the human, and `changes_requested` loops back to the author for revision and re-review.

## Visual Workflow Editor

Workflows can be authored as versioned JSON definitions through the dashboard's visual editor — no code, no redeploy. Control-flow primitives (`sequence`, `parallel`, `loop`, `branch`), agent delegation, child-workflow spawning, and signal-waiting compose into Temporal workflows that run on the same engine as compiled ones.

<p align="center">
  <img src="../.github/assets/phleet-workflow-editor.jpg" alt="Phleet workflow editor — visual editor for Temporal workflow definitions" width="900">
  <br>
  <em>Editing a workflow definition — steps, arguments, and live JSON/visual/split views.</em>
</p>
