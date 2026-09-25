# Upgrading past the project-card removal (#346)

This release removes project cards, per-assignment `full` / `card` modes and project routes. Every
assignment now loads the project's **canonical context** — the version row `CurrentVersion` points
at — in full, exactly as a `full` assignment always did.

## Who needs this

Only installs that ran the card build: the orchestrator DB has migration
`20260924165651_AddProjectContextCards` applied. A fresh install, or one that never ran that build,
upgrades normally and the preflight below never runs.

What the new build does on its first start:

1. The orchestrator runs `ContextRemovalPreflight` before migrating.
2. If it passes, migration `RemoveProjectContextCards` flips every assignment to `full`, deletes the
   reserved `fleet-context` endpoint and grant rows, then drops `project_context_routes`,
   `project_context_card_versions`, `project_contexts.CurrentCardVersion` and
   `agent_projects.ContextMode`. It never writes `project_contexts` or `project_context_versions`.
   Card text is not copied anywhere. **The dump from step 6 below is the only copy.**
3. If it blocks, nothing is migrated and the orchestrator exits with code 1 before it serves
   anything. See [If the preflight blocks](#if-the-preflight-blocks).

## Operator stage — on the current build, before deploying

Every step here is reversible.

1. Deploy the size-guardrail release first. Its size warnings flag canonical contexts that are
   long; card agents are about to load them in full.
2. Run the read-only report against the orchestrator DB:

   ```sql
   -- Card assignments and what each agent will load instead.
   SELECT a.Name agent, ap.ProjectName project, pc.CurrentVersion fullVersion,
          LENGTH(v.Content) fullBytes, LENGTH(cv.Content) cardBytes
   FROM agent_projects ap JOIN agents a ON a.Id = ap.AgentId
   LEFT JOIN project_contexts pc ON pc.Name = ap.ProjectName
   LEFT JOIN project_context_versions v ON v.ProjectContextId = pc.Id AND v.VersionNumber = pc.CurrentVersion
   LEFT JOIN project_context_card_versions cv ON cv.ProjectContextId = pc.Id AND cv.VersionNumber = pc.CurrentCardVersion
   WHERE ap.ContextMode = 'card';

   -- Projects that have a card, with their canonical size (NULL = no canonical row).
   SELECT pc.Name project, pc.CurrentVersion, pc.CurrentCardVersion, LENGTH(v.Content) fullBytes
   FROM project_contexts pc
   LEFT JOIN project_context_versions v ON v.ProjectContextId = pc.Id AND v.VersionNumber = pc.CurrentVersion
   WHERE EXISTS (SELECT 1 FROM project_context_card_versions c WHERE c.ProjectContextId = pc.Id);

   -- The reserved fallback rows the migration deletes.
   SELECT 'endpoint' kind, a.Name agent, e.Url detail FROM agent_mcp_endpoints e JOIN agents a ON a.Id = e.AgentId
   WHERE e.McpName = 'fleet-context'
   UNION ALL
   SELECT 'tool', a.Name, t.ToolName FROM agent_tools t JOIN agents a ON a.Id = t.AgentId
   WHERE t.ToolName = 'mcp__fleet-context__get_project_context';
   ```

   `LENGTH` counts bytes. `fullBytes − cardBytes` is how much each card agent's resident prompt
   grows.
3. Give every project that has a card or a card assignment non-empty canonical content. Shorten the
   canonical contexts the size warnings flag.
4. Flip every card assignment to `full` — the dashboard table, or
   `update_agent_config project_modes="<project>=full"` — and reprovision the affected **running**
   agents. Do not start stopped agents for this. This is the "stop reading cards" step; flipping
   back undoes it.
5. Remove references to `mcp__fleet-context__` from instructions and workflow definitions.
6. Take a `mysqldump` of the orchestrator DB. Keep it until the post-deploy checks pass.

## Deploy order

Orchestrator (preflight and migration) → dashboard → agent image, which reprovisions the running
agents. If the orchestrator does not come up healthy, stop there.

Every mixed dashboard / orchestrator pair works during the rollout. The Temporal bridge is not
redeployed: delegations still carry `repo`, which the agent now ignores.

## If the preflight blocks

The orchestrator logs one Critical line per problem, then the line that nothing was applied:

```
Context removal blocked: project=<project> agents=<agent-a,agent-b|none> reason=<no project context|no versions|empty canonical content>
RemoveProjectContextCards not applied. The orchestrator will not start. Redeploy the previous orchestrator image, fix the listed contexts, then deploy again.
```

It then writes `Fleet.Orchestrator startup aborted, exiting 1: …` to stderr and exits with code 1.
Nothing listens and nothing can reprovision, so every running agent keeps its container and files.
**The orchestrator stays down until you act**: under a restart policy it repeats the check and
exits again.

1. Redeploy the previous orchestrator image. The schema and every card row are untouched.
2. Fix the listed contexts (step 3 above), or remove a card assignment whose project no longer
   exists.
3. Rerun the report, then deploy this release again.

The preflight also exits 1 when its own queries fail. The usual cause is a schema left half-dropped
by an earlier failed migration — see the next section.

## Post-deploy checks

- `SELECT MigrationId FROM __EFMigrationsHistory` lists `…_RemoveProjectContextCards`.
- `GET /api/project-contexts/<project>/card/versions` returns 404, and so does `/mcp/context`.
- Every agent the preflight report listed has been reprovisioned, or is stopped.
- No generated `.mcp.json` or `settings.json` contains `fleet-context`.
- No generated `projects/*/full.md` exists. Provisioning deletes them.

## Rolling back

- **Deploy failed before the migration:** redeploy the previous images. The schema is intact.
- **After the migration applied, a revert alone is not enough.** MySQL DDL commits implicitly, so a
  failure part-way through also lands here.
  1. Stop the orchestrator and restore the pre-deploy dump.
  2. Redeploy the previous orchestrator, dashboard and agent images.
  3. Reprovision, and flip assignments back to `card` if wanted.

  `Down()` exists for schema tooling only. It recreates the card schema but restores no data, so the
  previous build would run with empty card tables.
