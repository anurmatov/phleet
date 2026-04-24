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

# ── Stop services ────────────────────────────────────────────────────────────
section "[1/4] Stopping services..."
if docker compose -p "$COMPOSE_PROJECT" -f "$COMPOSE_FILE" ps --quiet 2>/dev/null | head -1 | grep -q .; then
  (cd "$FLEET_BASE_DIR" && docker compose -p "$COMPOSE_PROJECT" -f "$COMPOSE_FILE" down)
  ok "Services stopped"
else
  ok "No running services"
fi

# ── Regenerate docker-compose.yml ────────────────────────────────────────────
section "[2/4] Regenerating docker-compose.yml..."
if [[ ! -f "$COMPOSE_EXAMPLE" ]]; then
  fail "docker-compose.example.yml not found"; exit 1
fi
sed -E 's|^(      context: )\.$|\1..|' "$COMPOSE_EXAMPLE" > "$COMPOSE_FILE"
ok "Generated $COMPOSE_FILE"

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

ok "All images built"

# ── Restart services ─────────────────────────────────────────────────────────
section "[4/4] Starting services..."

if $SKIP_RESTART; then
  warn "Skipping restart (--skip-restart)"
else
  (cd "$FLEET_BASE_DIR" && docker compose -p "$COMPOSE_PROJECT" -f "$COMPOSE_FILE" --env-file .env up -d)
  ok "Services started"
fi

echo
echo -e "${GREEN}✓  Fleet upgrade complete.${NC}"
echo
