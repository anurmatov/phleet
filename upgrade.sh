#!/usr/bin/env bash
# Fleet — Upgrade script
# Rebuilds all Docker images, regenerates docker-compose.yml, and restarts services.
# Unlike setup.sh, this skips all prompts, credential copying, and first-time setup.
#
# Usage: ./upgrade.sh [--no-cache] [--skip-restart]
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
FLEET_BASE_DIR="$SCRIPT_DIR/fleet"
ENV_FILE="$FLEET_BASE_DIR/.env"

# Compose lets a value exported in the invoking shell win over the same name in
# `--env-file`, so a stale `export FLEET_COMMS_BIND=...` from an earlier session
# would silently override the choice saved in .env — and the operator would see
# a service on an address they did not configure, with .env saying otherwise.
# Unset them here: .env is the record of the decision.
unset FLEET_COMMS_ENABLED FLEET_COMMS_BIND FLEET_COMMS_TRUST_PROXY \
      FLEET_COMMS_AGENT_LABEL FLEET_COMMS_STORE_PROVISIONED
# Epic grants (#436): .env is the record of the decision, never a stale shell export.
unset FLEET_EPIC_GRANTS_ENABLED FLEET_EPIC_GRANTS_MAX_DAYS FLEET_EPIC_GRANTS_DENIED_REPOS
# The journal decision in .env is preserved as it is: upgrade never asks, never flips it, and never
# generates a key for a host that did not opt in. `conversations migrate` below applies 0004 either
# way; it is additive.
unset FLEET_COMMS_JOURNAL_ENABLED FLEET_COMMS_JOURNAL_BIND FLEET_COMMS_JOURNAL_KEY \
      FLEET_COMMS_JOURNAL_EXCLUDED_CHAT_IDS FLEET_COMMS_JOURNAL_RETENTION \
      FLEET_COMMS_JOURNAL_READ_ALL_SUBJECTS
# Media likewise: the endpoint in .env is the recorded decision, and an upgrade never turns a
# bucket on for a host that declined one. Flipping it on would start a second MinIO holding
# archived conversation media that nobody chose to keep, and flipping it off would leave the
# bucket running with no service able to reach it. `conversations migrate` applies 0005 either
# way; it is additive.
unset FLEET_COMMS_MEDIA_ENDPOINT FLEET_COMMS_MEDIA_BUCKET FLEET_COMMS_MEDIA_BACKUP_DIR \
      FLEET_COMMS_MEDIA_ACCESS_KEY FLEET_COMMS_MEDIA_SECRET_KEY \
      FLEET_COMMS_MINIO_ROOT_USER FLEET_COMMS_MINIO_ROOT_PASSWORD \
      FLEET_COMMS_MEDIA_STORE FLEET_COMMS_SEAWEEDFS_SIGNING_KEY

source "$SCRIPT_DIR/scripts/lib/comms-profiles.sh"

COMPOSE_EXAMPLE="$SCRIPT_DIR/docker-compose.example.yml"
COMPOSE_FILE="$FLEET_BASE_DIR/docker-compose.yml"
COMPOSE_PROJECT="fleet"

# ── Flags ────────────────────────────────────────────────────────────────────
NO_CACHE=""
SKIP_RESTART=false

for arg in "$@"; do
  case "$arg" in
    --no-cache)     NO_CACHE="--no-cache" ;;
    --skip-restart) SKIP_RESTART=true ;;
    *) echo "Unknown flag: $arg. Valid flags: --no-cache --skip-restart"; exit 1 ;;
  esac
done

# ── Colors ───────────────────────────────────────────────────────────────────
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
BOLD='\033[1m'
NC='\033[0m'

ok()      { echo -e "${GREEN}✓${NC} $*"; }
warn()    { echo -e "${YELLOW}⚠${NC}  $*"; }
fail()    { echo -e "${RED}✗${NC} $*"; }
section() { echo -e "\n${BOLD}$*${NC}"; }

# ── Preflight checks ────────────────────────────────────────────────────────
if [[ ! -f "$ENV_FILE" ]]; then
  fail "No .env found at $ENV_FILE — run ./setup.sh first for initial setup."
  exit 1
fi

if ! docker info &>/dev/null; then
  fail "Docker daemon is not running."; exit 1
fi

# ── Helpers (from setup.sh) ──────────────────────────────────────────────────
read_env_var() {
  local file="$1" key="$2"
  [[ -f "$file" ]] || return 0
  grep "^${key}=" "$file" 2>/dev/null | head -1 | cut -d= -f2- || true
}

# ── Epic grant keys (#436) ───────────────────────────────────────────────────
# Added with their defaults when missing and never changed when present, so an upgrade never
# turns the feature on. Newline-safe: a hand-edited .env often lacks a trailing newline.
add_missing_env_var() {
  local key="$1" value="$2"
  grep -qE "^${key}=" "$ENV_FILE" && return 0
  [[ -s "$ENV_FILE" && -n "$(tail -c 1 "$ENV_FILE")" ]] && printf '\n' >> "$ENV_FILE"
  printf '%s=%s\n' "$key" "$value" >> "$ENV_FILE"
  ok "Added ${key}=${value} to .env"
}
add_missing_env_var FLEET_EPIC_GRANTS_ENABLED false
add_missing_env_var FLEET_EPIC_GRANTS_MAX_DAYS 14
add_missing_env_var FLEET_EPIC_GRANTS_DENIED_REPOS ""

# ── Stop services ────────────────────────────────────────────────────────────
section "[1/4] Stopping services..."
# Resolved BEFORE any lifecycle operation. Building the flag after shutdown meant `down` ran
# without the profile, so a running fleet-comms was never stopped — and disabling the service left
# it running indefinitely. The volume is preserved either way: `down` without -v keeps it.
COMMS_ENABLED=$(read_env_var "$ENV_FILE" "FLEET_COMMS_ENABLED")
comms_backfill_store_key "$ENV_FILE"
resolved=$(comms_resolve_profiles "$ENV_FILE")
COMMS_PROFILE_ARGS=()
[[ -z "$resolved" ]] || read -r -a COMMS_PROFILE_ARGS <<< "$resolved"
MEDIA_ENDPOINT=$(read_env_var "$ENV_FILE" FLEET_COMMS_MEDIA_ENDPOINT)
MEDIA_STORE=off
if [[ "$COMMS_ENABLED" == true && -n "$MEDIA_ENDPOINT" ]]; then
  MEDIA_STORE=$(read_env_var "$ENV_FILE" FLEET_COMMS_MEDIA_STORE)
fi
echo "Media store: $MEDIA_STORE"
# Prepare the current definition beside the runtime .env before pulling, but
# retain the old generated file for shutdown until preflight succeeds.
[[ -f "$COMPOSE_EXAMPLE" ]] || { fail 'docker-compose.example.yml not found'; exit 1; }
COMPOSE_NEXT=$(mktemp "$FLEET_BASE_DIR/.compose-next.XXXXXX")
trap 'rm -f "$COMPOSE_NEXT"' EXIT
sed -E -e 's|^(      context: )\.$|\1..|' -e 's|\./deploy/comms-seaweedfs:|../deploy/comms-seaweedfs:|' "$COMPOSE_EXAMPLE" > "$COMPOSE_NEXT"
case "$MEDIA_STORE" in
  seaweedfs)
    (cd "$FLEET_BASE_DIR" && docker compose -p "$COMPOSE_PROJECT" -f "$COMPOSE_NEXT" --env-file .env --profile comms-media-seaweedfs pull comms-seaweedfs) \
      || { fail 'Could not pull the pinned comms-seaweedfs image from docker-compose.example.yml'; exit 1; }
    ;;
  minio)
    if ! docker image inspect minio/minio >/dev/null 2>&1 || ! docker image inspect minio/mc:RELEASE.2025-08-13T08-35-41Z >/dev/null 2>&1; then
      fail 'legacy MinIO media images are not on this host and are no longer published; set FLEET_COMMS_MEDIA_ENDPOINT= to run without media, or restore the images'; exit 1
    fi
    ;;
esac

if docker compose -p "$COMPOSE_PROJECT" -f "$COMPOSE_FILE" ps --quiet 2>/dev/null | head -1 | grep -q .; then
  # `--profile comms` on down too, so an enabled service is actually stopped. When disabling, the
  # explicit stop below catches a service the profile no longer selects.
  (cd "$FLEET_BASE_DIR" && docker compose -p "$COMPOSE_PROJECT" -f "$COMPOSE_FILE" "${COMMS_PROFILE_ARGS[@]}" down)
  ok "Services stopped"
else
  ok "No running services"
fi

# ── Regenerate docker-compose.yml ────────────────────────────────────────────
section "[2/4] Regenerating docker-compose.yml..."
mv "$COMPOSE_NEXT" "$COMPOSE_FILE"
ok "Generated $COMPOSE_FILE"

# The regenerated definition includes SeaweedFS even on a legacy installation.
remove_comms_services=()
if [[ "$COMMS_ENABLED" != true ]]; then
  remove_comms_services=(fleet-comms fleet-comms-ops comms-mysql comms-minio comms-minio-init comms-seaweedfs comms-seaweedfs-init)
elif [[ -z "$MEDIA_ENDPOINT" ]]; then
  remove_comms_services=(comms-minio comms-minio-init comms-seaweedfs comms-seaweedfs-init)
fi
if [[ ${#remove_comms_services[@]} -gt 0 ]]; then
  (cd "$FLEET_BASE_DIR" && docker compose -p "$COMPOSE_PROJECT" -f "$COMPOSE_FILE" --env-file .env \
    --profile comms --profile comms-ops --profile comms-media --profile comms-media-seaweedfs \
    rm -sf "${remove_comms_services[@]}")
fi

# ── Build images ─────────────────────────────────────────────────────────────
section "[3/4] Building Docker images..."

VITE_TOKEN=$(read_env_var "$ENV_FILE" "ORCHESTRATOR_AUTH_TOKEN")
CONFIG_TOKEN=$(read_env_var "$ENV_FILE" "ORCHESTRATOR_CONFIG_TOKEN")

build_image() {
  local tag="$1" dockerfile="$2" context="$3" extra_args="${4:-}"
  echo -n "  Building $tag ... "
  local tmplog
  tmplog=$(mktemp)
  # shellcheck disable=SC2086
  if docker build -f "$dockerfile" $NO_CACHE $extra_args -t "$tag" "$context" >"$tmplog" 2>&1; then
    ok "done"
  else
    echo
    fail "Failed to build $tag — last 30 lines:"
    tail -30 "$tmplog"; rm -f "$tmplog"; exit 1
  fi
  rm -f "$tmplog"
}

build_image "fleet:agent"           "$SCRIPT_DIR/Dockerfile"                        "$SCRIPT_DIR"
build_image "fleet:orchestrator"    "$SCRIPT_DIR/src/Fleet.Orchestrator/Dockerfile" "$SCRIPT_DIR"
build_image "fleet:bridge"          "$SCRIPT_DIR/src/Fleet.Bridge/Dockerfile"       "$SCRIPT_DIR"
build_image "fleet:memory"          "$SCRIPT_DIR/src/Fleet.Memory/Dockerfile"       "$SCRIPT_DIR"
build_image "fleet:temporal-bridge" "$SCRIPT_DIR/Dockerfile.temporal"               "$SCRIPT_DIR"
build_image "fleet:telegram"        "$SCRIPT_DIR/src/Fleet.Telegram/Dockerfile"     "$SCRIPT_DIR"
build_image "fleet:dashboard"       "$SCRIPT_DIR/src/fleet-dashboard/Dockerfile"    "$SCRIPT_DIR" \
  "--build-arg VITE_AUTH_TOKEN=$VITE_TOKEN --build-arg VITE_CONFIG_TOKEN=$CONFIG_TOKEN"

# Only when the user opted in. Someone who never enabled Fleet.Comms should not pay its build
# time on every upgrade, and an image built for a service that never starts is pure cost.
if [[ "$COMMS_ENABLED" == "true" ]]; then
  build_image "fleet:comms"         "$SCRIPT_DIR/src/Fleet.Comms/Dockerfile"        "$SCRIPT_DIR"
fi

ok "All images built"

# ── Restart services ─────────────────────────────────────────────────────────
section "[4/4] Starting services..."

if $SKIP_RESTART; then
  warn "Skipping restart (--skip-restart)"
else
  # `docker compose down` above removes containers, NOT named volumes — `fleet_comms_auth`
  # survives an upgrade, which is what keeps enrolled devices enrolled across one. Never add -v.
  # Before `up`, every time. The documented enable-on-an-existing-install path is an .env edit
  # followed by ./upgrade.sh, which never ran `store init` — so the service came up against a
  # database that did not exist and 503'd permanently. `store init` is idempotent, so running it on
  # every enabled upgrade costs one short-lived container and closes that hole.
  if [[ "$COMMS_ENABLED" == "true" ]]; then
    (cd "$FLEET_BASE_DIR" && docker compose -p "$COMPOSE_PROJECT" -f "$COMPOSE_FILE" --env-file .env \
      run --rm fleet-comms-ops store init) \
      || { fail "Could not initialise the Fleet.Comms auth store — see docs/comms-deployment.md"; exit 1; }

    # Outside the volume, so a later volume loss is recognisable as loss. See setup.sh.
    #
    # Newline-safe and idempotent. A bare `>>` appends to whatever the last line is, and .env files
    # written by hand routinely lack a trailing newline — so the key would have been glued onto the
    # end of the previous value, corrupting that setting and never taking effect itself.
    write_comms_env_var() {
      local key="$1" value="$2"
      if grep -qE "^${key}=" "$ENV_FILE"; then
        # Replace in place, so repeated upgrades do not accumulate duplicate keys.
        sed -i.bak -E "s|^${key}=.*|${key}=${value}|" "$ENV_FILE" && rm -f "$ENV_FILE.bak"
      else
        [[ -s "$ENV_FILE" && -n "$(tail -c 1 "$ENV_FILE")" ]] && printf '\n' >> "$ENV_FILE"
        printf '%s=%s\n' "$key" "$value" >> "$ENV_FILE"
      fi
    }
    write_comms_env_var "FLEET_COMMS_STORE_PROVISIONED" "true"

    # The conversation schema, on every enabled upgrade, for the same reason `store init` runs on
    # every one: the documented enable-on-an-existing-install path is an .env edit followed by
    # ./upgrade.sh, and a service started against an unmigrated schema refuses every conversation
    # route and reports unhealthy until somebody runs this by hand.
    #
    # Idempotent — a second run applies nothing and reports the version — so running it always costs
    # one short-lived container. It uses the DDL credential; the runtime account has no DDL grants
    # and would be refused by the database, which is the intended outcome rather than a
    # misconfiguration to work around.
    if [[ -n "$(read_env_var "$ENV_FILE" "FLEET_COMMS_CONVERSATION_MIGRATION_DB")" ]]; then
      (cd "$FLEET_BASE_DIR" && docker compose -p "$COMPOSE_PROJECT" -f "$COMPOSE_FILE" --env-file .env \
        up -d comms-mysql) \
        || { fail "Could not start the conversation database — see docs/comms-deployment.md"; exit 1; }

      (cd "$FLEET_BASE_DIR" && docker compose -p "$COMPOSE_PROJECT" -f "$COMPOSE_FILE" --env-file .env \
        run --rm fleet-comms-ops conversations migrate) \
        || { fail "Could not apply the conversation migrations — see docs/comms-deployment.md"; exit 1; }
    fi
  fi

  # The agent-side flag is DERIVED at provisioning time, so a reprovision is what turns an agent's
  # uploader on — restarting the container does not. Say so rather than let an operator conclude the
  # upgrade enabled it.
  if [[ -n "$MEDIA_ENDPOINT" ]]; then
    warn "Media is enabled: reprovision the agents that journal media to pick up Journal:MediaEnabled."
    warn "  POST /api/agents/<name>/reprovision, or the dashboard's Reprovision action."
  fi

  (cd "$FLEET_BASE_DIR" && docker compose -p "$COMPOSE_PROJECT" -f "$COMPOSE_FILE" --env-file .env "${COMMS_PROFILE_ARGS[@]}" up -d)
  ok "Services started"

  # Bounded wait for readiness. "Upgrade completed" while the auth boundary is 503-ing every
  # request is a success message about a broken service.
  if [[ "$COMMS_ENABLED" == "true" ]]; then
    echo -n "  Waiting for fleet-comms ... "
    for _ in $(seq 1 40); do
      state=$(docker inspect -f '{{.State.Health.Status}}' fleet-comms 2>/dev/null || echo starting)
      [[ "$state" == "healthy" ]] && { ok "healthy"; break; }
      [[ "$state" == "unhealthy" ]] && { echo; fail "fleet-comms is unhealthy — check: docker logs fleet-comms"; exit 1; }
      sleep 3
    done
    # Fatal, not a warning. "Upgrade completed" over an auth boundary that is 503-ing every
    # request is a success message about a broken service, and the exit code is what a script or a
    # human in a hurry actually reads.
    [[ "$state" == "healthy" ]] || {
      fail "fleet-comms did not become healthy — check /ready, the store mount, and docker logs fleet-comms"
      exit 1
    }
  fi
fi

echo
echo -e "${GREEN}✓  Fleet upgrade complete.${NC}"
echo
