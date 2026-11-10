# Upgrading past the managed sandbox runtime removal

This release removes the stock OpenShell integration: manual whole-agent enrollment, the managed
sandbox runtime, saved sandbox policies, the identity signer service and the epoch broker lanes.
Agents run in Docker containers, as they always did by default. Secrets reach agents as
placeholders through the `fleet-secrets` forwarder instead; see
[secret placeholders](../secret-placeholders.md).

## Who needs this

Only installs whose orchestrator database has migration `20261028135434_AddManagedStockRuntime`
applied. A fresh install, or one that never ran that build, upgrades normally and the preflight
below never runs.

What the new build does on its first start:

1. The orchestrator runs `ManagedRuntimeRemovalPreflight` before migrating.
2. If it passes, migration `RemoveManagedStockRuntime` drops `sandbox_policies`,
   `sandbox_policy_versions` and seven `agents` columns: `RuntimeKind`, `SandboxPolicyVersionId`,
   `NativeAuthMode`, `ProviderApiKeyEnvRef`, `AppliedRuntimeKind`,
   `AppliedSandboxPolicyVersionId` and `AppliedRuntimeBinding`. Saved policies are not copied
   anywhere. **The dump from step 3 below is the only copy.**
3. If it blocks, nothing is migrated and the orchestrator exits with code 1 before it serves
   anything. See [If the preflight blocks](#if-the-preflight-blocks).

## Operator stage — on the current build, before deploying

1. Run the read-only report against the orchestrator database:

   ```sql
   -- Every agent that would block the upgrade. An empty result means the preflight passes.
   SELECT Name, RuntimeKind, AppliedRuntimeKind,
          JSON_UNQUOTE(JSON_EXTRACT(AppliedRuntimeBinding, '$.state')) AS recordState
   FROM agents
   WHERE RuntimeKind <> 'docker' OR AppliedRuntimeKind <> 'docker'
      OR (AppliedRuntimeBinding IS NOT NULL
          AND COALESCE(JSON_UNQUOTE(JSON_EXTRACT(AppliedRuntimeBinding, '$.state')), '') NOT IN ('none', 'revoked'));

   -- What the migration drops.
   SELECT (SELECT COUNT(*) FROM sandbox_policies) AS policies,
          (SELECT COUNT(*) FROM sandbox_policy_versions) AS versions;
   ```

2. For every agent the first query lists, on the current build: revoke its managed runtime, wait
   until it reports `revoked`, then select the Docker runtime and reprovision it. Do not edit the
   columns by hand. A sandbox that is still running outside Docker keeps running after the upgrade,
   and the new build has no code left to stop it.
3. Dump the orchestrator database. This is the only copy of the saved policies and runtime records.
4. Stop and remove what the integration left on the host, in this order. Each is separate from the
   ordinary fleet stack and none is touched by the new build:
   - sandbox workloads and their supervisors (containers created by the gateway);
   - the gateway container (`docker compose --profile openshell down` on the old compose file);
   - the identity signer, if you ran one;
   - per-epoch broker vhosts and users named `stock-<epoch>` (RabbitMQ management);
   - the gateway state directory, the `openshell-client` directory under the fleet base directory
     and the gateway backup directory. Keep them until you are sure you will not roll back.

## Deploy

1. Remove these keys from the fleet `.env`. They are ignored now, so leaving them is harmless:
   `FLEET_STOCK_MANAGED_ENABLED`, every other `FLEET_STOCK_*` key, `OPENSHELL_CLI_URL`,
   `OPENSHELL_CLI_SHA256` and `FLEET_DOCKER_GID`.
2. Regenerate `docker-compose.yml` from `docker-compose.example.yml` (`./setup.sh` or
   `./upgrade.sh`). The `openshell-gateway` service, the `openshell` profile and the
   `StockOpenShell__*` settings are gone.
3. Build and start. Every image changes: agent, orchestrator, memory, bridge, temporal bridge,
   telegram and dashboard. Rebuild them from the same commit; an old bridge or agent image next to a
   new orchestrator is not a supported mix.
4. Reprovision every agent so it runs the new agent image.

## What you will see

- Orchestrator log: `Managed runtime removal preflight: agents=N savedPolicies=N
  savedPolicyVersions=N result=pass`, then the migration.
- The dashboard no longer shows the Sandbox policies page or the runtime selector.
- The routes `/api/sandbox-policies`, `/api/agents/{name}/runtime/apply`, `/runtime/revoke` and
  `/api/managed-runtime/rollback-preflight` no longer exist.
- `create_agent` and `update_agent_config` no longer accept `runtime_kind`,
  `sandbox_policy_version_id`, `native_auth_mode` or `provider_api_key_env_ref`. REST bodies that
  still carry those fields are accepted and the fields are ignored.

## If the preflight blocks

The orchestrator logs one `Managed runtime removal blocked: agent=… reason=…` line per agent and
exits with code 1. Nothing was migrated. Reasons:

| Reason | Meaning |
|---|---|
| `selected_runtime_not_docker` | The agent's saved selection is the sandbox runtime. |
| `applied_runtime_not_docker` | The agent is running in a sandbox. |
| `runtime_record_not_revoked` | The stored record says applied, stopped, applying or needs attention. |
| `runtime_record_in_progress` | An apply or revoke was interrupted. |
| `runtime_record_unreadable` | The stored record is not valid JSON. It is treated as live. |

Redeploy the previous orchestrator image, complete step 2 above for each listed agent, then deploy
again.

## Rolling back

- **Deploy failed before the migration** (the preflight blocked, or the orchestrator never
  started): redeploy the previous images. The schema is intact.
- **After the migration applied, a revert alone is not enough.** MySQL DDL commits implicitly, so a
  failure part-way through also lands here.
  1. Stop the orchestrator and restore the pre-deploy dump.
  2. Redeploy the previous images: orchestrator, agent, memory, bridge, temporal bridge, telegram
     and dashboard.
  3. Reprovision every agent.

  `Down()` exists for schema tooling only. It recreates the tables and columns empty, with both
  runtime kinds defaulting to `docker`, and restores no policy or record.
