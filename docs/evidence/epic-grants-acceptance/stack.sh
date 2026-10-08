#!/usr/bin/env bash
# #436 isolated acceptance stack: start (with empty state) or stop the app-level services.
#
#   ACCEPT_DIR   working dir holding the `temporal` CLI binary and a Python venv with pika
#   REPO_ROOT    phleet checkout whose Release build is under test (default: this checkout)
#
# MySQL 8.0 (127.0.0.1:3306, user accept / accept-only) and RabbitMQ (127.0.0.1:5672) must already
# run on loopback; see docs/evidence/epic-grants-acceptance.md. Everything binds 127.0.0.1 except the
# bridge, whose MCP port 3001 is hard-coded to all interfaces — run this only on a disposable host.
# Every credential below is a throwaway value for this stack, never a deployment secret.
set -u
ACCEPT_DIR=${ACCEPT_DIR:-/tmp/epic-grants-accept}
REPO_ROOT=${REPO_ROOT:-$(cd "$(dirname "$0")/../../.." && pwd)}
cd "$ACCEPT_DIR"

stop() {
  for pat in '^dotnet Fleet.Orchestrator.dll' '^dotnet Fleet.Temporal.dll' '^./venv/bin/python fake_agent.py' \
             '^python3 github_stub.py' '^./temporal server start-dev'; do
    for p in $(pgrep -f "$pat"); do kill "$p"; done
  done
  sleep 3
}

start() {
  stop
  cp "$REPO_ROOT"/docs/evidence/epic-grants-acceptance/*.py .
  rm -f temporal.db temporal.db-* state.json calls.jsonl fake_agent.log evidence.log orchestrator.log bridge.log github_stub.log
  mysql -uaccept -paccept-only -h127.0.0.1 -e "DROP DATABASE IF EXISTS orch_accept436" 2>/dev/null
  echo '{"heads": {}, "merged": {}, "bodies": {}, "ceo_concern_valid": {}, "head_version": {}}' > state.json
  : > env.file

  cat > orch.env <<EOF
ASPNETCORE_ENVIRONMENT=Production
Kestrel__Endpoints__Http__Url=http://127.0.0.1:3600
ConnectionStrings__OrchestratorDb='Server=127.0.0.1;Port=3306;Database=orch_accept436;User=accept;Password=accept-only;AllowPublicKeyRetrieval=true'
Temporal__Address=127.0.0.1:7233
Temporal__Namespaces__0=fleet
RabbitMq__Host=127.0.0.1
Orchestrator__AuthToken=accept436-admin-token
Orchestrator__ConfigToken=accept436-config-token
Provisioning__SeedFilePath=$REPO_ROOT/seed.example.json
Provisioning__RolesDir=$REPO_ROOT/src/Fleet.Orchestrator/roles
Provisioning__ProjectsDir=$REPO_ROOT/src/Fleet.Orchestrator/projects
Provisioning__EnvFilePath=$ACCEPT_DIR/env.file
Provisioning__BaseDir=$ACCEPT_DIR
Docker__SocketPath=$ACCEPT_DIR/no-docker.sock
FleetMemory__Url=http://127.0.0.1:9
FleetMemory__McpUrl=http://127.0.0.1:9
FLEET_CTO_AGENT=cto-agent
EpicGrants__Enabled=true
EpicGrants__MaxDays=14
EpicGrants__DeniedRepos=example-org/accept-denied
EpicGrants__GitHubApiBaseUrl=http://127.0.0.1:3901
EOF
  cat > bridge.env <<EOF
TemporalBridge__TemporalAddress=127.0.0.1:7233
TemporalBridge__Namespaces__0=fleet
TemporalBridge__OrchestratorUrl=http://127.0.0.1:3600
TemporalBridge__OrchestratorAuthToken=accept436-admin-token
TemporalBridge__AgentTimeoutSeconds=300
RabbitMq__Host=127.0.0.1
FleetWorkflows__CtoAgent=cto-agent
EOF

  # The seed starts its children on task queue "fleet", so the throwaway server's namespace is
  # "fleet" too; the separate server instance is the isolation.
  setsid nohup ./temporal server start-dev --ip 127.0.0.1 --port 7233 --ui-port 8233 --namespace fleet \
    --db-filename "$ACCEPT_DIR/temporal.db" --log-level warn > temporal.log 2>&1 < /dev/null &
  setsid nohup python3 github_stub.py > github_stub.log 2>&1 < /dev/null &
  sleep 6
  (set -a; . ./orch.env; set +a; cd "$REPO_ROOT/src/Fleet.Orchestrator/bin/Release/net10.0" && \
    setsid nohup dotnet Fleet.Orchestrator.dll > "$ACCEPT_DIR/orchestrator.log" 2>&1 < /dev/null &)
  for _ in $(seq 1 60); do curl -sf http://127.0.0.1:3600/api/epic-grants >/dev/null 2>&1 && break; sleep 1; done
  (set -a; . ./bridge.env; set +a; cd "$REPO_ROOT/src/Fleet.Temporal/bin/Release/net10.0" && \
    setsid nohup dotnet Fleet.Temporal.dll > "$ACCEPT_DIR/bridge.log" 2>&1 < /dev/null &)
  sleep 10
  setsid nohup ./venv/bin/python fake_agent.py > fake_agent.out 2>&1 < /dev/null &
  sleep 3
  grep -q "Epic grants are enabled" orchestrator.log && grep -q "Registered search attributes" bridge.log \
    && grep -q "fake agent ready" fake_agent.log && echo "stack ready" || { echo "stack NOT ready"; exit 1; }
}

case "${1:-}" in
  start) start ;;
  stop) stop ;;
  *) echo "usage: $0 start|stop" >&2; exit 2 ;;
esac
