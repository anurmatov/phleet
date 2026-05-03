# Fleet-Memory ACL Rollout Runbook

Project-scoped ACL gates which agents can read which memory projects. This document covers enabling it safely in production.

## Pre-flight checklist

Run these before flipping the flag:

1. **Migration applied** — confirm `agent_project_access` table exists:
   ```sql
   SELECT COUNT(*) FROM agent_project_access;
   ```
   Expect at least 1 row (co-cto wildcard).

2. **Co-cto wildcard seeded** — confirm wildcard row is present:
   ```sql
   SELECT * FROM agent_project_access WHERE Project = '*';
   ```

3. **No-project memory count** — check how many memories lack a project:
   ```
   GET /api/admin/memories/no-project-count
   ```
   If count is significant, decide: set `AclAllowNoProject=true` during rollout (allows all agents to read untagged memories), or bulk-tag them first.

4. **All agents have explicit rows** — verify each running agent has at least one row:
   ```sql
   SELECT a.Name, COUNT(apa.Project) as access_count
   FROM agents a
   LEFT JOIN agent_project_access apa ON apa.AgentName = LOWER(a.Name)
   GROUP BY a.Name;
   ```
   Zero-row agents will be fail-closed (read nothing) once ACL is enabled.

## Enable sequence

1. Edit fleet-memory appsettings (on macstudio at `~/fleet/deploy/appsettings.json` or the fleet-memory-specific config):
   ```json
   {
     "Acl": {
       "EnableProjectScopedAcl": true,
       "AclAllowNoProject": false
     }
   }
   ```
   Set `AclAllowNoProject: true` if pre-flight step 3 showed significant untagged memories.

2. Restart fleet-memory container:
   ```
   docker restart fleet-fleet-memory-1
   ```

3. Watch logs for the startup message confirming ACL is active:
   ```
   docker logs -f fleet-fleet-memory-1 | grep -i acl
   ```

## Validation probe

After restart, run the ACL probe to confirm behaviour:

1. Check denial counter baseline (should be 0):
   ```
   GET http://fleet-memory:3100/internal/stats/acl-denied
   ```

2. Have each agent run `memory_list` or `memory_search` for a topic in their project.

3. Re-check denial counter:
   ```
   GET http://fleet-memory:3100/internal/stats/acl-denied
   ```
   Any unexpected denials indicate a missing project row — grant access via `manage_agent_project_access action=add`.

## Granting access to an agent

Via the MCP tool (co-cto only):
```
manage_agent_project_access agent_name=adev action=add project=fleet
```

Via the dashboard: Config modal → Memory Project Access section → type project name → "+ add".

Via the REST API (orchestrator):
```
POST /api/agents/{name}/project-access
{"project": "fleet"}
```

Changes take effect immediately — fleet-memory polls orchestrator every 60 seconds, or force-refresh by sending `config.changed` via peer-config broadcast (`configService.ReloadAsync()`).

## Rollback procedure

If ACL causes unexpected widespread denial, disable immediately:

1. Set `EnableProjectScopedAcl: false` in appsettings.
2. Restart fleet-memory: `docker restart fleet-fleet-memory-1`.
3. All agents regain full read access (pre-ACL behaviour).

Root-cause the denials before re-enabling. Check `/internal/stats/acl-denied` snapshot if you captured it before rollback.

## Turning off AclAllowNoProject

Once all memories have been tagged with a project:

1. Confirm zero untagged: `GET /api/admin/memories/no-project-count` → `{"no_project_count": 0}`.
2. Set `AclAllowNoProject: false` in config and restart.
3. Non-wildcard agents can no longer read untagged memories — this is the desired final state.
