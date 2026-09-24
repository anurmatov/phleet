# Project context cards

Each agent–project assignment chooses how that project's context reaches the agent. The mode lives
on the assignment (`agent_projects.ContextMode`), never on the project, so one agent can hold a
project as a card while every other holder keeps the full context.

| Mode | Resident in the system prompt | On a turn routed to the project | On any other turn |
|---|---|---|---|
| `full` (default) | the full context, as before this feature | nothing extra | nothing extra |
| `card` | the project's card plus a generated footer | the full context is attached to the turn | the agent may call `get_project_context` |

Every existing assignment is `full`. Nothing changes for an agent until an operator flips one of its
assignments and reprovisions it; an agent with no card assignment is provisioned byte for byte as
before.

## Modes

Set the mode per assignment:

- dashboard: agent **Config** → Projects → *Project context mode*
- MCP (admin `/mcp`): `update_agent_config(agent_name=agent-a, project_modes="project-a=card,project-b=full")`
- REST: `PUT /api/agents/agent-a/config` with `"projectModes": { "project-a": "card" }`

Rules:

- A key must be one of the agent's resulting assignments (names compare case-insensitively).
- `card` needs the project to have a card. Otherwise the write fails (tool error / HTTP 400) and
  nothing is saved.
- Replacing the `projects` list keeps the mode of every name that remains; a new name starts as
  `full` unless `projectModes` says otherwise.
- A mode-only change touches no memory ACL row and publishes nothing.
- **Every mode change takes effect on reprovision** of that agent.

### Effective mode

Provisioning decides the *effective* mode per assignment. Everything it generates keys on the
effective mode, never the stored one.

| Stored mode | Card state | Effective | `projects/<p>/context.md` | `projects/<p>/full.md` | Log |
|---|---|---|---|---|---|
| `full` | any | full | full content | not written | — |
| `card` | carries every keep marker, fresh | card | card + footer | full content | `Information` |
| `card` | carries every keep marker, stale | card | card + footer ("may be stale") | full content | `Warning card_stale` |
| `card` | missing a keep marker | **full** | full content | not written | `Warning card_fallback_full`, named in the reprovision result |
| `card` | no context row, or no card | — | — | — | provisioning throws; the agent is not started |

The last row is only reachable by editing the database by hand. It fails loudly on purpose.

## Authoring a card

A card is a compact, separately versioned summary of a project's full context. There is at most one
card per project, with its own history (20 versions, like full contexts).

Write one with:

- dashboard: **Project Contexts** → project → *Card* → **Save card for full vN**
- MCP (admin `/mcp`): `update_project_card(name, content, based_on_full_version, reason, created_by)`;
  read it with `get_project_card(name)`; roll back with `rollback_project_card(name, target_version)`
- REST: `POST /api/project-contexts/project-a/card/versions` with
  `{ "content": "...", "basedOnFullVersion": 3, "reason": "..." }`

`basedOnFullVersion` is required and must be between 1 and the full context's current version:
the author states which full version the card reflects. The dashboard sends the full version it is
displaying.

The agent never sees an authored footer. Provisioning appends a generated one to the resident card:

```
[project card: project-a · card v2 · written for full v3 · full is v3]
The full project-a context is attached to turns routed to this project.
On any other turn that needs it, call get_project_context with name "project-a".
```

When the card was written for an older full version, the first line ends in `· may be stale`.

Cards cannot be deleted. To stop using one, flip the assignments back to `full`.

### Keep markers

A keep marker tags a passage of the full context that every card must carry — a safety rule, a
deploy restriction, anything the agent must never lose. One parser (`KeepMarkerParser`) defines
them, and every consumer — the card gate, full-write validation, `missingKeeps`, provisioning and
the dashboard — reads it.

A valid marker is an HTML comment that holds the marker and nothing else, on one line:

```
<!-- keep:deploy-window -->
```

| Rule | Definition |
|---|---|
| Candidate | every match of `<!--[ \t]*keep[ \t]*:`, case-insensitive |
| Valid | the candidate starts a whole comment `<!--[ \t]*keep:SLUG[ \t]*-->`, where `SLUG` matches `[a-z0-9][a-z0-9-]{0,63}` (case-sensitive) |
| Invalid | any other candidate: uppercase or illegal slug, missing slug, extra text in the comment, `keep :`, spanning lines, unterminated |
| Code fences | not special — the parser reads raw text, so a marker in a fenced block is a marker |
| Duplicates | one slug counts once |

To mention a marker in a context's prose without declaring it, write it so it fails the candidate
pattern — for example with HTML entities: `&lt;!-- keep:x --&gt;`.

How markers are enforced:

| Write | Rule |
|---|---|
| Card write | every valid slug of the **current** full content must appear as a valid marker in the card, otherwise 400 naming the missing slugs, nothing saved. The text around a marker is never compared; extra card slugs are fine. An invalid candidate in the card is also a 400 |
| Full create / update | an invalid candidate is a 400 naming it and the valid grammar, nothing saved. Text without any `keep` candidate is unaffected |
| Rollback (full or card) | always allowed — it is the recovery path. Invalid candidates are logged and ignored |
| Provisioning | a card missing a current marker is rendered as **full** for that assignment (see *Effective mode*) |

## Routes and precedence

A route maps a signal on a turn to a project context. Routes belong to the context (they follow
the row by id and are deleted with it) and apply to every agent assigned the project.

| Kind | Value | Where the agent gets it |
|---|---|---|
| `repo` | `owner/name`, e.g. `org/app`; stored and compared lower-case | the relay message's `Repo`, set by a UWE `delegate` step's `repo` field |
| `workflow` | exact workflow type, e.g. `ExampleWorkflow` | the `[fleet-wf:<Type>:<Id>]` line of a workflow directive |
| `chat` | a signed 64-bit chat id, e.g. `-100000000001` | the chat of a direct or group message, or of a group check-in |

Manage them in the dashboard (*Routes* panel), with `manage_project_routes(action=list|add|remove, name, kind, value, id)`,
or with `POST` / `DELETE /api/project-contexts/project-a/routes[/{id}]`. Several routes may share a
signal; that is a deliberate fan-out.

Resolution, at the point where the turn text is built:

1. Only routes of projects the agent is assigned are considered (any mode).
2. Levels are evaluated `repo`, then `workflow`, then `chat`. The first level with at least one
   match wins and lower levels are not consulted — **even when every match at that level is
   effective-full**. Signals are never merged across levels.
3. The winning matches that are effective-card are attached, ordered by project name.
4. More than 3 → nothing is attached and the agent logs `route_too_broad`.

A relay directive's chat id is a posting target, not a topic, so it is never a `chat` signal.
Reactions, the welcome DM and first-party client turns are not routed; they rely on the card and
the fallback tool.

Repo routing needs the workflow to say which repository it is about. Add a templated `repo` to its
delegate steps:

```json
{ "type": "delegate", "target": "agent-a", "instruction": "...", "repo": "{{input.Repo}}" }
```

A step without `repo`, or one that resolves to empty, schedules exactly the activity input it
always did. A value that is not `owner/name` is dropped with a warning and the delegation still runs.

Route changes reach an agent on its next reprovision (they are part of its generated
`appsettings.json`).

## Delivery and the ledger

The route is resolved at intake and travels with the message; the attachment is rendered only when
the text is handed to the executor, and only there:

| Delivery path | What is attached |
|---|---|
| new turn | the message's own requests |
| mid-turn injection | the injected message's own requests, in the same frame |
| coalesced chat queue | the union of every merged part, rendered once |
| Inbox continuation after the final-answer gate | the union of the merged messages, rendered once |
| process-exit resume redelivery | re-rendered from the stored message (the process is cold) |

The attached text exists only in the executor input — never in Telegram output, queue notices,
conversation events or the stored message.

A per-session ledger of `(project, full version)` suppresses repeats:

- An entry is marked **only after the executor accepted the prompt** (`prompt_accepted` for a turn,
  `Injected` for an injection). A delivery that fails before that leaves it unmarked, so the next
  routed delivery attaches again. Cancel, timeout or error after acceptance keep the mark.
- The ledger clears when the process is cold, when the provider session id changes, and when the
  provider reports a compaction.
- Gemini runs a fresh CLI per task, so every routed delivery attaches.

The worst race is a duplicate attachment, never a missing one.

## Fallback: `get_project_context` on `/mcp/context`

For agents with at least one effective card assignment, provisioning adds:

- an MCP server `fleet-context` at `<Provisioning:ContextMcpUrl>?agent=<agent>`. The URL defaults
  to `http://fleet-orchestrator:3600/mcp/context`; override it with `Provisioning__ContextMcpUrl`
  on the orchestrator. A database MCP row named `fleet-context` wins, and still gets `?agent=`.
- one grant, `mcp__fleet-context__get_project_context` (Claude and Gemini `settings.json`, Codex
  `Agent.AllowedTools`).

The admin `/mcp` endpoint is never granted by this feature.

A session created on `/mcp/context` is bound to that route and that agent, and only lists
`get_project_context`:

| Request | Result |
|---|---|
| `/mcp/context` without a valid `agent` | 403 |
| session bound to the same agent | allowed |
| session bound to another agent | 403 |
| unknown session id (admin session, expired, or lost in a restart) | 404 — the client re-initializes |
| no session id | allowed; the new session is bound to this agent |
| a context session used on `/mcp` | 403 |

The tool re-checks the path and agent on every call. On `/mcp/context` it returns a project only if
the calling agent has an assignment for it, in any mode; anything else gets the same
`Project context '<p>' is not available to this caller.` text, whether the project exists or not.
On the admin `/mcp`, `get_project_context` behaves as it always did.

## Stale handling

A card is **stale** when it was written for an older full version than the current one.

- A full write on a project with a card still succeeds. Its response carries
  `card: { stale, basedOnFullVersion, missingKeeps }` (MCP: one `Card:` line), and the dashboard's
  save message says so.
- A stale card is still used. The footer says `may be stale`, provisioning logs `card_stale`, and
  the dashboard shows a *stale* badge on the list row, in the Card panel and next to the agent's
  mode select.
- If the full edit **added a keep marker** the card lacks, the card is no longer usable: card-mode
  agents get the full context on their next reprovision (`card_fallback_full`) until the card is
  updated.

To refresh: edit the card and **Save card for full vN** (saving unchanged text for the new version
is valid), then reprovision the agents listed under *card mode* on that project.

## Rollout

1. Rebuild the agent image and roll it out. **The agent image goes first — it must be live before any
   assignment is flipped**: an old agent with a card assignment inlines the card and never attaches
   the full context. A new agent under an old orchestrator gets no routing block and behaves as today.
2. Deploy the orchestrator. The migration adds the columns and tables; every assignment is `full`,
   so every agent reprovisions byte for byte as before.
3. Deploy the temporal bridge when no consensus review or PR-implementation run is in flight
   (delegates run with a single attempt). Existing workflow histories replay unchanged.
4. Only then flip assignments, starting with one canary agent: write the card, add routes, flip one
   assignment, reprovision that agent.

It is working when:

- `projects/<p>/context.md` is the card plus footer and `projects/<p>/full.md` exists;
- `tools/list` on `/mcp/context` returns exactly one tool;
- two routed turns log `ProjectContextAttach … rendered=<p> accepted=true`, then
  `suppressed=<p>:already_attached`.

It is broken when an assigned project logs `denied reason=not_assigned`, every turn logs
`accepted=false`, or every provision logs `card_fallback_full`.

### Expected after an orchestrator deploy: `unbound_session`

The session registry lives in memory. After the orchestrator restarts, agents still hold their old
`Mcp-Session-Id`, so their next fallback call logs
`ContextMcpGuard rejected status=404 reason=unbound_session` and the client re-initializes. A short
burst right after a deploy is expected. A burst that keeps going after clients have re-initialized
is not.

## Rollback

- **One assignment:** `update_agent_config(project_modes="project-a=full")`, then reprovision that
  agent only. The full context was never modified.
- **A bad card:** `rollback_project_card`, then reprovision the agents in `cardAssignments`.
- **The mechanism:** flip every card assignment to `full`, reprovision those agents, then revert the
  code. Keep the schema — old code ignores the new columns and tables. The down migration exists but
  drops card history and routes, so export them first.

## Known limits

- **Duplicate on resume.** The ledger is in memory. A Claude process that restarts and resumes a
  prior session gets the full context attached a second time. That is safe and accepted.
- **`?agent=` is attribution, not authentication.** The session binding stops accidental
  cross-agent, cross-route and cross-project reads on a trusted Docker network. A container with a
  shell on that network can claim any agent name when it creates a session — the same trust model as
  fleet-memory's ACL.
- **Unauthenticated REST metadata.** `GET /api/project-contexts/{name}` was already unauthenticated
  and now also returns `routes` (repository names, workflow types, chat ids) and `cardAssignments`
  (agent names) — the same class of exposure as `GET /api/agents/{name}/config`. Authenticating
  orchestrator reads is tracked in #348.
- No card deletion and no rename API. Renaming a context by hand keeps its card and routes (they key
  on the row id) but detaches its name-keyed assignments; a `card` assignment to the old name then
  fails provisioning loudly.
