#!/usr/bin/env bash
# Fleet — Guided first-time setup script
# Usage: ./setup.sh [--dry-run] [--skip-build] [--skip-services] [--prompt-local-creds] [--full-setup]
set -euo pipefail

# Capture script directory as absolute path (required for symlink later)
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# ── Flags ─────────────────────────────────────────────────────────────────────
DRY_RUN=false
SKIP_BUILD=false
SKIP_SERVICES=false
PROMPT_LOCAL_CREDS=false
PROMPT_TELEGRAM=false
PROMPT_GITHUB=false

for arg in "$@"; do
  case "$arg" in
    --dry-run)            DRY_RUN=true ;;
    --skip-build)         SKIP_BUILD=true ;;
    --skip-services)      SKIP_SERVICES=true ;;
    --prompt-local-creds) PROMPT_LOCAL_CREDS=true ;;
    --full-setup)         PROMPT_TELEGRAM=true; PROMPT_GITHUB=true ;;
    *) echo "Unknown flag: $arg. Valid flags: --dry-run --skip-build --skip-services --prompt-local-creds --full-setup"; exit 1 ;;
  esac
done

# ── Colors ────────────────────────────────────────────────────────────────────
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
BOLD='\033[1m'
NC='\033[0m'

ok()   { echo -e "${GREEN}✓${NC} $*"; }
warn() { echo -e "${YELLOW}⚠${NC}  $*"; }
fail() { echo -e "${RED}✗${NC} $*"; }
section() { echo -e "\n${BOLD}$*${NC}"; }

if $DRY_RUN; then warn "Running in --dry-run mode — no changes will be made."; fi

# ── Environment hygiene ──────────────────────────────────────────────────────
# Docker Compose's variable substitution prefers SHELL environment variables
# over values passed via `--env-file`. If the user (or a prior setup run)
# exported any of these names into their shell, every `docker compose up -d`
# invocation below would bake the stale shell value into the containers,
# silently overriding the freshly-written .env file — the exact bug behind
# a mysterious 401 at step 7/8 when the orchestrator ran with a different
# ORCHESTRATOR_AUTH_TOKEN than the one setup.sh just saved. Unsetting these
# makes .env the single source of truth for compose substitution.
unset FLEET_BASE_DIR FLEET_CTO_AGENT FLEET_GROUP_CHAT_ID \
      FLEET_MYSQL_ROOT_PASSWORD FLEET_MYSQL_PASSWORD \
      ORCHESTRATOR_AUTH_TOKEN TELEGRAM_NOTIFIER_BOT_TOKEN \
      MINIO_ACCESS_KEY MINIO_SECRET_KEY \
      FLEET_MEMORY_EMBEDDING_PROVIDER FLEET_MEMORY_EMBEDDING_DIMENSIONS \
      FLEET_MEMORY_OLLAMA_URL FLEET_MEMORY_OLLAMA_MODEL \
      AUTHTOKENREFRESH__CLAUDECLIENTID AUTHTOKENREFRESH__CODEXCLIENTID

# ── Canonical paths ───────────────────────────────────────────────────────────
# FLEET_BASE_DIR is hardcoded to $SCRIPT_DIR/fleet — a nested, gitignored subdir
# under the repo root. All runtime state (env file, seed, generated compose,
# workspaces, memories, credentials) lives here, keeping the repo root pristine.
FLEET_BASE_DIR="$SCRIPT_DIR/fleet"
ENV_FILE="$FLEET_BASE_DIR/.env"
ENV_EXAMPLE="$SCRIPT_DIR/.env.example"
COMPOSE_EXAMPLE="$SCRIPT_DIR/docker-compose.example.yml"
COMPOSE_FILE="$FLEET_BASE_DIR/docker-compose.yml"
SEED_FILE="$FLEET_BASE_DIR/seed.json"
SEED_EXAMPLE="$SCRIPT_DIR/seed.example.json"
COMPOSE_PROJECT="fleet"

# ── Helpers ───────────────────────────────────────────────────────────────────

# Read a single variable from an env file (returns empty if file missing/key absent)
read_env_var() {
  local file="$1" key="$2"
  [[ -f "$file" ]] || return 0
  grep "^${key}=" "$file" 2>/dev/null | head -1 | cut -d= -f2- || true
}

# Write or update a variable in an env file
write_env_var() {
  local file="$1" key="$2" value="$3"
  if $DRY_RUN; then
    echo -e "  ${YELLOW}[dry-run]${NC} Would write ${key}=<value> to $(basename "$file")"
    return
  fi
  if grep -q "^${key}=" "$file" 2>/dev/null; then
    if [[ "$OSTYPE" == "darwin"* ]]; then
      sed -i '' "s|^${key}=.*|${key}=${value}|" "$file"
    else
      sed -i "s|^${key}=.*|${key}=${value}|" "$file"
    fi
  else
    echo "${key}=${value}" >> "$file"
  fi
}

# Check if a value looks like a real value (not a placeholder from .env.example)
is_placeholder() {
  local val="$1"
  [[ -z "$val" ]] && return 0
  [[ "$val" == *"/path/to/"* ]] && return 0
  [[ "$val" == "changeme" ]] && return 0
  [[ "$val" == "base64-encoded-private-key-here" ]] && return 0
  [[ "$val" == "your-secret-token-here" ]] && return 0
  [[ "$val" == "123456" ]] && return 0
  [[ "$val" == "123456:"* ]] && return 0
  [[ "$val" == "654321:"* ]] && return 0
  return 1
}

# Prompt for a config value if it's missing/placeholder; write to env file
# Usage: prompt_field <file> <key> <label> <guidance> <required:y|n> <masked:y|n> [default]
prompt_field() {
  local file="$1" key="$2" label="$3" guidance="$4" required="$5" masked="$6"
  local default="${7:-}"

  local current
  current=$(read_env_var "$file" "$key")

  # Skip if already set to a real value
  if ! is_placeholder "$current"; then
    return 0
  fi

  [[ -n "$guidance" ]] && echo -e "  ${YELLOW}→${NC} $guidance"
  local prompt_text="  $label"
  [[ -n "$default" ]] && prompt_text="  $label [${default}]"
  [[ "$required" == "n" ]] && prompt_text="$prompt_text (optional)"

  local value=""
  while true; do
    if [[ "$masked" == "y" ]]; then
      $DRY_RUN && { echo -e "  ${YELLOW}[dry-run]${NC} Would prompt for $key (masked)"; return; }
      read -rsp "${prompt_text}: " value; echo
    else
      $DRY_RUN && { echo -e "  ${YELLOW}[dry-run]${NC} Would prompt for $key"; return; }
      read -rp "${prompt_text}: " value
    fi
    [[ -z "$value" && -n "$default" ]] && value="$default"
    if [[ -z "$value" && "$required" == "y" ]]; then
      fail "  This field is required."; continue
    fi
    break
  done

  [[ -n "$value" ]] && write_env_var "$file" "$key" "$value"
}

# Poll a container's health status until healthy or timeout
# Usage: poll_health <container_name> <timeout_secs> <display_label>
poll_health() {
  local container="$1" timeout="$2" label="$3"
  local elapsed=0
  echo -n "  Waiting for $label"
  while [[ $elapsed -lt $timeout ]]; do
    local status
    status=$(docker inspect --format='{{.State.Health.Status}}' "$container" 2>/dev/null || echo "missing")
    case "$status" in
      healthy)
        echo; ok "$label ready"; return 0 ;;
      unhealthy)
        echo; fail "$label is unhealthy — run: docker compose -p $COMPOSE_PROJECT -f $COMPOSE_FILE logs $label"; exit 1 ;;
    esac
    echo -n "."; sleep 2; elapsed=$((elapsed + 2))
  done
  echo
  fail "$label timed out after ${timeout}s — run: docker compose -p $COMPOSE_PROJECT -f $COMPOSE_FILE logs $label"
  exit 1
}

# ─────────────────────────────────────────────────────────────────────────────
section "[1/8] Checking prerequisites..."
# ─────────────────────────────────────────────────────────────────────────────

check_cmd() {
  local cmd="$1" install_msg="$2"
  if ! command -v "$cmd" &>/dev/null; then
    fail "$cmd not found — $install_msg"; exit 1
  fi
  ok "$cmd found"
}

check_cmd docker  "Install Docker — https://docs.docker.com/get-docker/"

if ! docker info &>/dev/null; then
  fail "Docker daemon is not running."
  # macOS: offer to start colima
  if [[ "$OSTYPE" == "darwin"* ]] && command -v colima &>/dev/null; then
    read -rp "  Start colima now? (y/n) [y]: " start_colima
    start_colima="${start_colima:-y}"
    if [[ "$start_colima" =~ ^[Yy]$ ]]; then
      colima start --cpu 4 --memory 8
      ok "colima started"
    else
      fail "Docker must be running. Exiting."; exit 1
    fi
  else
    echo "  Start Docker Desktop or the Docker daemon and re-run ./setup.sh"; exit 1
  fi
else
  ok "Docker daemon running"
  # macOS: report colima status if present
  if [[ "$OSTYPE" == "darwin"* ]] && command -v colima &>/dev/null; then
    if colima status &>/dev/null; then ok "colima running"; fi
  fi
fi

if ! docker compose version &>/dev/null; then
  fail "docker compose (v2) not found — install Docker Compose v2"; exit 1
fi
ok "docker compose found"

check_cmd curl "Install curl — https://curl.se/download.html"
check_cmd jq   "Install jq — https://jqlang.github.io/jq/download/"

# ─────────────────────────────────────────────────────────────────────────────
section "[2/8] AI Provider Auth..."
# ─────────────────────────────────────────────────────────────────────────────

echo "  Which AI provider(s) do you want to use?"
echo "    1) claude"
echo "    2) codex"
echo "    3) both"
read -rp "  Choice [1]: " provider_choice
provider_choice="${provider_choice:-1}"

USE_CLAUDE=false; USE_CODEX=false
case "$provider_choice" in
  1) USE_CLAUDE=true ;;
  2) USE_CODEX=true ;;
  3) USE_CLAUDE=true; USE_CODEX=true ;;
  *) fail "Invalid choice — enter 1, 2, or 3"; exit 1 ;;
esac

# Set by check_creds_claude — path to a readable credentials JSON file
# (either the original ~/.claude/.credentials.json or a temp file extracted from keychain)
CLAUDE_CREDS_PATH=""

check_creds_claude() {
  local file_creds="$HOME/.claude/.credentials.json"

  if [[ -f "$file_creds" ]]; then
    CLAUDE_CREDS_PATH="$file_creds"
    ok "claude credentials file found at $file_creds"
  elif [[ "$OSTYPE" == "darwin"* ]] && security find-generic-password -s "Claude Code-credentials" &>/dev/null; then
    # macOS fallback: Claude Code stores credentials in the login keychain
    local tmp_creds
    tmp_creds=$(mktemp)
    chmod 600 "$tmp_creds"
    if ! security find-generic-password -s "Claude Code-credentials" -w > "$tmp_creds" 2>/dev/null; then
      rm -f "$tmp_creds"
      fail "failed to read 'Claude Code-credentials' from macOS keychain"
      exit 1
    fi
    CLAUDE_CREDS_PATH="$tmp_creds"
    ok "claude credentials read from macOS keychain (Claude Code-credentials)"
  else
    fail "claude credentials not found at $file_creds"
    [[ "$OSTYPE" == "darwin"* ]] && echo "  (also checked macOS keychain entry 'Claude Code-credentials' — not found)"
    echo "  Open the claude CLI at least once to authenticate, then re-run ./setup.sh"
    exit 1
  fi

  local expires_raw
  expires_raw=$(jq -r '.claudeAiOauth.expiresAt // empty' "$CLAUDE_CREDS_PATH" 2>/dev/null)
  if [[ -z "$expires_raw" ]]; then
    fail "claude credentials are missing expiresAt — re-authenticate and re-run ./setup.sh"
    exit 1
  fi
  local expires_ts now
  if [[ "$expires_raw" =~ ^[0-9]+$ ]]; then
    # Numeric: unix milliseconds (current Claude Code format)
    expires_ts=$((expires_raw / 1000))
  else
    # ISO 8601 string (legacy format)
    local expires_clean="${expires_raw%%.*}"  # strip sub-second precision
    if [[ "$OSTYPE" == "darwin"* ]]; then
      expires_ts=$(date -j -f "%Y-%m-%dT%H:%M:%S" "$expires_clean" "+%s" 2>/dev/null || echo 0)
    else
      expires_ts=$(date -d "$expires_raw" "+%s" 2>/dev/null || echo 0)
    fi
  fi
  now=$(date +%s)
  if [[ "$expires_ts" -le "$now" ]]; then
    fail "claude credentials are expired — re-authenticate and re-run ./setup.sh"; exit 1
  fi
  local human
  human=$(date -r "$expires_ts" 2>/dev/null || date -d "@$expires_ts" 2>/dev/null || echo "$expires_raw")
  ok "claude credentials valid (expires $human)"
}

check_creds_codex() {
  local creds="$HOME/.codex/auth.json"
  if [[ ! -f "$creds" ]]; then
    fail "codex credentials not found at $creds"
    echo "  Open the codex CLI at least once to authenticate, then re-run ./setup.sh"
    exit 1
  fi
  local expires_ts now
  expires_ts=$(jq -r '.expires_at // 0' "$creds" 2>/dev/null || echo 0)
  now=$(date +%s)
  if [[ "$expires_ts" -le "$now" ]]; then
    fail "codex credentials at $creds are expired — re-authenticate and re-run ./setup.sh"; exit 1
  fi
  local human
  human=$(date -r "$expires_ts" 2>/dev/null || date -d "@$expires_ts" 2>/dev/null || echo "ts=$expires_ts")
  ok "codex credentials valid (expires $human)"
}

if $USE_CLAUDE; then check_creds_claude; fi
if $USE_CODEX;  then check_creds_codex;  fi

# ─────────────────────────────────────────────────────────────────────────────
section "[3/8] Configuration..."
# ─────────────────────────────────────────────────────────────────────────────

# Ensure the fleet data dir exists — all runtime state lives here (.env,
# seed.json, generated docker-compose.yml, workspaces, memories, credentials).
if ! $DRY_RUN; then
  mkdir -p "$FLEET_BASE_DIR"
  ok "Fleet data dir: $FLEET_BASE_DIR"
else
  echo -e "  ${YELLOW}[dry-run]${NC} Would create fleet data dir: $FLEET_BASE_DIR"
fi

if [[ ! -f "$ENV_FILE" ]]; then
  echo "  No .env found — copying from .env.example"
  if ! $DRY_RUN; then
    cp "$ENV_EXAMPLE" "$ENV_FILE"
    chmod 600 "$ENV_FILE"
    ok ".env created at $ENV_FILE"
  else
    echo -e "  ${YELLOW}[dry-run]${NC} Would copy .env.example → $ENV_FILE"
  fi
else
  ok ".env already exists at $ENV_FILE"
  # Show blank/placeholder fields
  blank_fields=()
  for key in ORCHESTRATOR_AUTH_TOKEN; do
    val=$(read_env_var "$ENV_FILE" "$key")
    is_placeholder "$val" && blank_fields+=("$key")
  done
  if [[ ${#blank_fields[@]} -gt 0 ]]; then
    echo "  Blank/placeholder fields: ${blank_fields[*]}"
    echo "  You'll be prompted to fill them in."
  fi
fi

# Always write FLEET_BASE_DIR so compose can substitute ${FLEET_BASE_DIR} in
# bind-mount paths. Overrides any stale value from .env.example.
if ! $DRY_RUN; then
  write_env_var "$ENV_FILE" "FLEET_BASE_DIR" "$FLEET_BASE_DIR"
fi

echo
echo "  Fill in required configuration (press Enter to keep existing values):"
echo

# CTO agent name — used by docker-compose to wire FleetWorkflows__CtoAgent
# into fleet-temporal-bridge and fleet-bridge so seed workflows can resolve
# {{config.CtoAgent}} at runtime. Must be set BEFORE services start.
# In --full-setup mode, prompt for the CTO agent name now.
# Otherwise, write the placeholder 'phleet' silently so docker-compose substitution
# resolves. The real agent name is chosen when the user provisions their first agent.
if $PROMPT_TELEGRAM; then
  prompt_field "$ENV_FILE" "FLEET_CTO_AGENT" "CTO agent short name" \
    "Lowercase short name for your co-cto agent (used as routing key, e.g. 'myagent'). You'll create this agent in step 7." \
    "y" "n"
else
  _cto_val=$(read_env_var "$ENV_FILE" "FLEET_CTO_AGENT")
  if [[ -z "$_cto_val" || "$_cto_val" == "changeme" || "$_cto_val" == "phleet" ]]; then
    if ! $DRY_RUN; then
      write_env_var "$ENV_FILE" "FLEET_CTO_AGENT" "phleet"
    fi
  fi
fi

if $PROMPT_TELEGRAM; then
  # Optional Telegram group chat ID for group conversation visibility.
  prompt_field "$ENV_FILE" "FLEET_GROUP_CHAT_ID" "Fleet Telegram group chat ID" \
    "Optional. Create a Telegram group, add both bots as members, then forward any message from the group to https://t.me/userinfobot — it replies with the negative integer group ID. Agents use this group for status updates and cross-agent coordination. Leave 0 to skip groups." \
    "n" "n" "0"

  prompt_field "$ENV_FILE" "TELEGRAM_NOTIFIER_BOT_TOKEN" "Telegram notifier bot token" \
    "Create a bot at https://t.me/BotFather (send /newbot). This bot sends messages from every non-CTO agent and the fleet bridge." "y" "y"

  prompt_field "$ENV_FILE" "TELEGRAM_CTO_BOT_TOKEN" "Telegram CTO bot token" \
    "Same flow, second bot: https://t.me/BotFather → /newbot. This is the bot you DM your CTO agent through." "y" "y"
fi

if $PROMPT_GITHUB; then
  prompt_field "$ENV_FILE" "GITHUB_APP_ID" "GitHub App ID" \
    "Create a GitHub App at https://github.com/settings/apps/new. The App ID is shown on the app's settings page after creation." "y" "n"

  # GITHUB_APP_PEM needs special handling — pasting a ~1700-char base64 blob into
  # a masked `read -s` prompt is unreliable (terminal truncation, bracketed-paste).
  # Also `base64 -w0` is GNU-only and breaks on macOS BSD base64. So: ask for the
  # PEM file path and do the portable base64 encoding here.
  current_pem=$(read_env_var "$ENV_FILE" "GITHUB_APP_PEM")
  if is_placeholder "$current_pem"; then
    if $DRY_RUN; then
      echo -e "  ${YELLOW}[dry-run]${NC} Would prompt for GITHUB_APP_PEM (file path)"
    else
      echo -e "  ${YELLOW}→${NC} On your GitHub App settings page (https://github.com/settings/apps): 'Private keys' → 'Generate a private key' → downloads a .pem. Paste the file path here."
      while true; do
        read -rp "  GitHub App private key file path: " pem_path
        # expand leading ~ to $HOME
        pem_path="${pem_path/#\~/$HOME}"
        if [[ -z "$pem_path" ]]; then
          fail "  This field is required."
          continue
        fi
        if [[ ! -f "$pem_path" ]]; then
          fail "  File not found: $pem_path"
          continue
        fi
        # Portable base64 encoding without line wraps — works on both GNU and BSD base64
        pem_b64=$(base64 < "$pem_path" | tr -d '\n')
        if [[ -z "$pem_b64" ]]; then
          fail "  base64 encoding produced no output — is $pem_path a valid PEM?"
          continue
        fi
        write_env_var "$ENV_FILE" "GITHUB_APP_PEM" "$pem_b64"
        ok "GitHub App private key encoded and stored (${#pem_b64} chars)"
        break
      done
    fi
  fi
fi

# Note: no separate prompt for VITE_AUTH_TOKEN. The dashboard build arg is
# sourced directly from ${ORCHESTRATOR_AUTH_TOKEN} in docker-compose.example.yml
# and in the `docker build` call below, so the two values can't diverge.

# ── Local-only credentials ────────────────────────────────────────────────────
# These five values are internal-only — no external service needs to match them.
# By default we generate strong random values and skip the prompt (--prompt-local-creds
# reverts to the explicit prompt-driven flow for each field).
#
# Generation also fires when the field still holds a known weak default from
# .env.example (minioadmin / fleetroot / fleetpass / changeme / your-secret-token-here).
# Pre-existing real values are always preserved on re-run.

_local_cred_autogenned=false

# Returns 0 (needs generation) if val is empty, a standard placeholder, or a known weak default
_needs_local_gen() {
  local val="$1"
  is_placeholder "$val" && return 0
  [[ "$val" == "minioadmin" || "$val" == "fleetroot" || "$val" == "fleetpass" ]] && return 0
  return 1
}

_autogen_local_cred() {
  local file="$1" key="$2" gencmd="$3"
  local val
  val=$(read_env_var "$file" "$key")
  if _needs_local_gen "$val"; then
    if $DRY_RUN; then
      echo -e "  ${YELLOW}[dry-run]${NC} Would auto-generate $key"
      return
    fi
    local newval
    newval=$(eval "$gencmd") || { echo -e "  ${RED}✗${NC} Failed to generate value for $key — is openssl installed?" >&2; exit 1; }
    if [[ -z "$newval" ]]; then
      echo -e "  ${RED}✗${NC} Auto-generation of $key produced an empty value — is openssl installed?" >&2; exit 1
    fi
    write_env_var "$file" "$key" "$newval"
    _local_cred_autogenned=true
  fi
}

if $PROMPT_LOCAL_CREDS; then
  prompt_field "$ENV_FILE" "ORCHESTRATOR_AUTH_TOKEN"  "Orchestrator API auth token"  \
    "Protects mutating REST endpoints. To auto-generate: openssl rand -hex 32" "y" "y"
  prompt_field "$ENV_FILE" "MINIO_ACCESS_KEY"          "MinIO access key"          "" "n" "n" "minioadmin"
  prompt_field "$ENV_FILE" "MINIO_SECRET_KEY"          "MinIO secret key"          "" "n" "y" "minioadmin"
  prompt_field "$ENV_FILE" "FLEET_MYSQL_ROOT_PASSWORD"  "MySQL root password"       "" "n" "y" "fleetroot"
  prompt_field "$ENV_FILE" "FLEET_MYSQL_PASSWORD"       "MySQL fleet user password" "" "n" "y" "fleetpass"
else
  _autogen_local_cred "$ENV_FILE" "ORCHESTRATOR_AUTH_TOKEN"   "openssl rand -hex 32"
  _autogen_local_cred "$ENV_FILE" "MINIO_ACCESS_KEY"          "openssl rand -hex 12"
  _autogen_local_cred "$ENV_FILE" "MINIO_SECRET_KEY"          "openssl rand -hex 32"
  _autogen_local_cred "$ENV_FILE" "FLEET_MYSQL_ROOT_PASSWORD"  "openssl rand -hex 24"
  _autogen_local_cred "$ENV_FILE" "FLEET_MYSQL_PASSWORD"       "openssl rand -hex 24"
  if ! $DRY_RUN && $_local_cred_autogenned; then
    echo
    echo "  Auto-generated local credentials (written to $ENV_FILE):"
    echo "    MinIO access key + secret"
    echo "    MySQL root password + fleet-user password"
    echo "    Orchestrator API auth token"
    echo
    echo "  Read $ENV_FILE to see the values if you need them."
  fi
fi

# Embedding provider choice
echo
current_emb=$(read_env_var "$ENV_FILE" "FLEET_MEMORY_EMBEDDING_PROVIDER")
if [[ -z "$current_emb" || "$current_emb" == "onnx" ]]; then
  echo "  Memory embedding provider:"
  echo "    1) onnx (default — runs inside container, zero external deps)"
  echo "    2) ollama (requires ollama running on host)"
  if $DRY_RUN; then
    emb_choice="1"
    echo -e "  ${YELLOW}[dry-run]${NC} Defaulting to choice 1 (onnx)"
  else
    read -rp "  Choice [1]: " emb_choice
    emb_choice="${emb_choice:-1}"
  fi
  if [[ "$emb_choice" == "2" ]]; then
    write_env_var "$ENV_FILE" "FLEET_MEMORY_EMBEDDING_PROVIDER" "ollama"
    write_env_var "$ENV_FILE" "FLEET_MEMORY_EMBEDDING_DIMENSIONS" "768"
    prompt_field "$ENV_FILE" "FLEET_MEMORY_OLLAMA_URL" "Ollama URL" \
      "macOS default: http://host.docker.internal:11434 — Linux: use host IP" \
      "n" "n" "http://host.docker.internal:11434"
    prompt_field "$ENV_FILE" "FLEET_MEMORY_OLLAMA_MODEL" "Ollama embedding model" \
      "Ensure model is pulled: ollama pull nomic-embed-text" \
      "n" "n" "nomic-embed-text"
    ollama_url=$(read_env_var "$ENV_FILE" "FLEET_MEMORY_OLLAMA_URL")
    ollama_url="${ollama_url:-http://host.docker.internal:11434}"
    if curl -sf "$ollama_url/api/tags" &>/dev/null; then
      ok "Ollama reachable at $ollama_url"
    else
      warn "Ollama not reachable at $ollama_url — ensure it's running before fleet-memory starts"
    fi
  else
    write_env_var "$ENV_FILE" "FLEET_MEMORY_EMBEDDING_PROVIDER" "onnx"
    write_env_var "$ENV_FILE" "FLEET_MEMORY_EMBEDDING_DIMENSIONS" "384"
    ok "Embedding provider: onnx"
  fi
fi

echo
ok ".env configured"

# Create runtime directories
echo
echo "  Creating runtime directories under $FLEET_BASE_DIR..."
if ! $DRY_RUN; then
  mkdir -p "$FLEET_BASE_DIR/workspaces" "$FLEET_BASE_DIR/memories" \
           "$FLEET_BASE_DIR/mysql-backup" "$FLEET_BASE_DIR/projects"
  ok "Directories created"
else
  echo -e "  ${YELLOW}[dry-run]${NC} Would create: $FLEET_BASE_DIR/{workspaces,memories,mysql-backup,projects}"
fi

# Copy AI provider credentials (step 2 deferred until FLEET_BASE_DIR is known)
if $USE_CLAUDE; then
  if ! $DRY_RUN; then
    cp "$CLAUDE_CREDS_PATH" "$FLEET_BASE_DIR/.claude-credentials.json"
    chmod 600 "$FLEET_BASE_DIR/.claude-credentials.json"
    ok "Claude credentials → $FLEET_BASE_DIR/.claude-credentials.json"
  else
    echo -e "  ${YELLOW}[dry-run]${NC} Would copy claude credentials → $FLEET_BASE_DIR/.claude-credentials.json"
  fi
else
  # Placeholder so the compose bind-mount doesn't fail
  if ! $DRY_RUN && [[ ! -f "$FLEET_BASE_DIR/.claude-credentials.json" ]]; then
    printf '{}' > "$FLEET_BASE_DIR/.claude-credentials.json"
    chmod 600 "$FLEET_BASE_DIR/.claude-credentials.json"
  fi
fi
if $USE_CODEX; then
  if ! $DRY_RUN; then
    cp "$HOME/.codex/auth.json" "$FLEET_BASE_DIR/.codex-credentials.json"
    chmod 600 "$FLEET_BASE_DIR/.codex-credentials.json"
    ok "Codex credentials → $FLEET_BASE_DIR/.codex-credentials.json"
  else
    echo -e "  ${YELLOW}[dry-run]${NC} Would copy ~/.codex/auth.json → $FLEET_BASE_DIR/.codex-credentials.json"
  fi
else
  # Placeholder so the compose bind-mount doesn't fail
  if ! $DRY_RUN && [[ ! -f "$FLEET_BASE_DIR/.codex-credentials.json" ]]; then
    printf '{}' > "$FLEET_BASE_DIR/.codex-credentials.json"
    chmod 600 "$FLEET_BASE_DIR/.codex-credentials.json"
  fi
fi

# Copy seed.example.json → $FLEET_BASE_DIR/seed.json if missing
# (the generated compose file mounts ./seed.json from its own directory).
if [[ ! -f "$SEED_FILE" ]]; then
  if ! $DRY_RUN; then
    cp "$SEED_EXAMPLE" "$SEED_FILE"
    ok "seed.json created at $SEED_FILE"
  else
    echo -e "  ${YELLOW}[dry-run]${NC} Would copy seed.example.json → $SEED_FILE"
  fi
else
  ok "seed.json already exists"
fi

# Create Docker network
if docker network inspect fleet-net &>/dev/null; then
  ok "Docker network fleet-net already exists"
else
  if ! $DRY_RUN; then
    docker network create fleet-net
    ok "Docker network fleet-net created"
  else
    echo -e "  ${YELLOW}[dry-run]${NC} Would create Docker network fleet-net"
  fi
fi

# ─────────────────────────────────────────────────────────────────────────────
section "[4/8] Generating $FLEET_BASE_DIR/docker-compose.yml..."
# ─────────────────────────────────────────────────────────────────────────────

if [[ ! -f "$COMPOSE_EXAMPLE" ]]; then
  fail "docker-compose.example.yml not found — repository may be incomplete"; exit 1
fi

# Generate $FLEET_BASE_DIR/docker-compose.yml from the example by rewriting
# build contexts. The example uses `context: .` (repo root) for the five
# services that build locally; when the compose file lives one level deeper
# at $FLEET_BASE_DIR/docker-compose.yml, those contexts must climb back out
# with `context: ..`. `./seed.json` and `env_file: - .env` naturally resolve
# relative to the compose file's new location, so no substitution needed.
if ! $DRY_RUN; then
  if [[ "$OSTYPE" == "darwin"* ]]; then
    sed -E 's|^(      context: )\.$|\1..|' "$COMPOSE_EXAMPLE" > "$COMPOSE_FILE"
  else
    sed -E 's|^(      context: )\.$|\1..|' "$COMPOSE_EXAMPLE" > "$COMPOSE_FILE"
  fi
  ok "Generated $COMPOSE_FILE"
else
  echo -e "  ${YELLOW}[dry-run]${NC} Would generate $COMPOSE_FILE from $COMPOSE_EXAMPLE"
fi

# ─────────────────────────────────────────────────────────────────────────────
section "[5/8] Building Docker images..."
# ─────────────────────────────────────────────────────────────────────────────

if $SKIP_BUILD; then
  warn "Skipping image builds (--skip-build)"
else
  # VITE_AUTH_TOKEN is just the orchestrator auth token — baked into the
  # dashboard bundle at build time so it can call the API.
  VITE_TOKEN=$(read_env_var "$ENV_FILE" "ORCHESTRATOR_AUTH_TOKEN")

  # Download ONNX model + vocab into models/fleet-memory/ before building fleet:memory.
  # Fleet.Memory's Dockerfile bakes them in via `COPY models/fleet-memory/ /app/models/`.
  onnx_model="$SCRIPT_DIR/models/fleet-memory/all-MiniLM-L6-v2.onnx"
  if [[ ! -f "$onnx_model" ]]; then
    echo -n "  Downloading ONNX embedding model (~80MB) ... "
    if $DRY_RUN; then
      echo -e "${YELLOW}[dry-run]${NC}"
    else
      if bash "$SCRIPT_DIR/scripts/download-onnx-model.sh" >/dev/null 2>&1; then
        ok "done"
      else
        fail "ONNX model download failed — run scripts/download-onnx-model.sh manually"
        exit 1
      fi
    fi
  fi

  build_image() {
    local tag="$1" dockerfile="$2" context="$3" extra_args="${4:-}"
    echo -n "  Building $tag ... "
    if $DRY_RUN; then
      echo -e "${YELLOW}[dry-run]${NC}"; return
    fi
    local tmplog
    tmplog=$(mktemp)
    # shellcheck disable=SC2086
    if docker build -f "$dockerfile" $extra_args -t "$tag" "$context" >"$tmplog" 2>&1; then
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
  build_image "fleet:telegram"        "$SCRIPT_DIR/src/Fleet.Telegram/Dockerfile"      "$SCRIPT_DIR"
  # No quotes around $VITE_TOKEN — the `docker build $extra_args` call in
  # build_image() relies on word-splitting, and escaped quotes here would
  # become literal characters inside the baked-in dashboard token → 401s.
  build_image "fleet:dashboard"       "$SCRIPT_DIR/src/fleet-dashboard/Dockerfile"    "$SCRIPT_DIR" \
    "--build-arg VITE_AUTH_TOKEN=$VITE_TOKEN"

  ok "All images built"
fi

# ─────────────────────────────────────────────────────────────────────────────
section "[6/8] Starting services..."
# ─────────────────────────────────────────────────────────────────────────────

if $SKIP_SERVICES; then
  warn "Skipping services (--skip-services)"
else
  if ! $DRY_RUN; then
    (cd "$FLEET_BASE_DIR" && docker compose -p "$COMPOSE_PROJECT" -f "$COMPOSE_FILE" --env-file .env up -d)

    poll_health "fleet-mysql"           30  "fleet-mysql"
    poll_health "rabbitmq"              30  "rabbitmq"
    poll_health "qdrant"                20  "qdrant"
    poll_health "temporal-postgresql"   30  "temporal-postgresql"
    poll_health "temporal-server"       120 "temporal-server"
    poll_health "fleet-memory"          20  "fleet-memory"
    poll_health "fleet-bridge"          20  "fleet-bridge"
    poll_health "fleet-playwright"      30  "fleet-playwright"
    poll_health "fleet-orchestrator"    30  "fleet-orchestrator"
    poll_health "fleet-temporal-bridge" 20  "fleet-temporal-bridge"
    poll_health "fleet-telegram"        20  "fleet-telegram"
    poll_health "fleet-minio"           15  "fleet-minio"
  else
    echo -e "  ${YELLOW}[dry-run]${NC} Would run: docker compose -p $COMPOSE_PROJECT -f $COMPOSE_FILE --env-file .env up -d"
    echo -e "  ${YELLOW}[dry-run]${NC} Would poll health for core services"
  fi

  ok "All services started"
fi

# ─────────────────────────────────────────────────────────────────────────────
section "[7/8] Provision Assistant agent..."
# ─────────────────────────────────────────────────────────────────────────────

if $SKIP_SERVICES; then
  warn "Skipping agent provisioning (--skip-services)"
else
  if $DRY_RUN; then
    do_provision="y"
    echo -e "  ${YELLOW}[dry-run]${NC} Would prompt for agent provisioning (defaulting to yes)"
  elif $PROMPT_TELEGRAM; then
    # --full-setup mode: existing prompt with yes default
    read -rp "  Would you like to provision your Assistant (CTO) agent now? (y/n) [y]: " do_provision
    do_provision="${do_provision:-y}"
  else
    # Default mode: opt-in with N default; dashboard is the recommended path
    echo "  Stack is up. You can provision your first agent now, or do it later from"
    echo "  the dashboard at http://localhost:3700."
    echo
    read -rp "  Provision an agent now? [y/N] " do_provision
    do_provision="${do_provision:-n}"
  fi

  if [[ "$do_provision" =~ ^[Yy]$ ]]; then
    ORCH_URL="http://localhost:3600"
    ORCH_TOKEN=$(read_env_var "$ENV_FILE" "ORCHESTRATOR_AUTH_TOKEN")

    # Drift guard: if the orchestrator was started before the current .env was
    # written (e.g. setup.sh re-run against a pre-existing stack, or ${...}
    # substitution picked up a shell env var that diverged from .env), the
    # running Orchestrator__AuthToken will not match ORCH_TOKEN and every API
    # call below will 401. Detect and force-recreate the affected services.
    if ! $DRY_RUN; then
      running_token=$(docker exec fleet-orchestrator printenv Orchestrator__AuthToken 2>/dev/null || true)
      if [[ -n "$running_token" && "$running_token" != "$ORCH_TOKEN" ]]; then
        warn "  fleet-orchestrator is running with a stale auth token — recreating"
        (cd "$FLEET_BASE_DIR" && docker compose -p "$COMPOSE_PROJECT" -f "$COMPOSE_FILE" --env-file .env up -d --force-recreate fleet-orchestrator fleet-temporal-bridge)
        poll_health "fleet-orchestrator"    30 "fleet-orchestrator"
        poll_health "fleet-temporal-bridge" 20 "fleet-temporal-bridge"
      fi
    fi

    # Agent name was set in step [3/8] as FLEET_CTO_AGENT and is the source of truth.
    agent_name=$(read_env_var "$ENV_FILE" "FLEET_CTO_AGENT")
    if [[ -z "$agent_name" ]]; then
      fail "FLEET_CTO_AGENT is not set in .env — re-run setup.sh from step 3."; exit 1
    fi

    # Group chat ID was set in step [3/8] as FLEET_GROUP_CHAT_ID (0 if disabled).
    group_chat_id=$(read_env_var "$ENV_FILE" "FLEET_GROUP_CHAT_ID")
    group_chat_id="${group_chat_id:-0}"
    tg_group_id=""
    [[ "$group_chat_id" != "0" ]] && tg_group_id="$group_chat_id"

    # Capitalize first letter for display name default (portable across bash 3.2 / 4+)
    default_display="$(printf '%s' "${agent_name:0:1}" | tr '[:lower:]' '[:upper:]')${agent_name:1}"

    if $DRY_RUN; then
      display_name="$default_display"
      model="claude-opus-4-6"
      memory_mb="4096"
      tg_user_id="0"
      echo -e "  ${YELLOW}[dry-run]${NC} Using defaults: name=$agent_name model=$model memory=${memory_mb}MB"
    else
      read -rp "  Display name [$default_display]: " display_name; display_name="${display_name:-$default_display}"
      echo "  Model options: claude-opus-4-6 / claude-sonnet-4-6"
      read -rp "  Model [claude-opus-4-6]: "         model;        model="${model:-claude-opus-4-6}"
      read -rp "  Memory limit MB [4096]: "          memory_mb;    memory_mb="${memory_mb:-4096}"

      tg_user_id=""
      echo -e "  ${YELLOW}→${NC} DM https://t.me/userinfobot — it replies with your user ID as soon as you send any message. This whitelists you as the only Telegram user allowed to talk to your agent."
      while true; do
        read -rp "  Your Telegram user ID (integer): " tg_user_id
        [[ "$tg_user_id" =~ ^[0-9]+$ ]] && break
        fail "  Must be a positive integer."
      done
    fi

    # Instructions are hardcoded — every setup.sh-provisioned agent is a co-cto, so the
    # canonical "base + co-cto" pair is the only valid combination here. Users can edit
    # the agent's instruction set later via the dashboard or MCP tools.
    instr1="base"
    instr2="co-cto"

    # Determine provider value based on user's earlier choice.
    # For "both", default the agent to claude — user can edit later.
    agent_provider="claude"
    if $USE_CODEX && ! $USE_CLAUDE; then
      agent_provider="codex"
    fi

    if $DRY_RUN; then
      echo -e "  ${YELLOW}[dry-run]${NC} Would POST $ORCH_URL/api/agents (name=$agent_name)"
      echo -e "  ${YELLOW}[dry-run]${NC} Would PUT $ORCH_URL/api/agents/$agent_name/config (instructions)"
      echo -e "  ${YELLOW}[dry-run]${NC} Would POST $ORCH_URL/api/agents/$agent_name/reprovision"
      if $USE_CLAUDE; then
        echo -e "  ${YELLOW}[dry-run]${NC} Would POST $ORCH_URL/api/schedules (auth-token-refresh-claude-30m)"
      fi
      if $USE_CODEX; then
        echo -e "  ${YELLOW}[dry-run]${NC} Would POST $ORCH_URL/api/schedules (auth-token-refresh-codex-30m)"
      fi
    else
      # Build TelegramGroups field only if provided
      tg_groups_field=""
      if [[ -n "$tg_group_id" && "$tg_group_id" =~ ^-?[0-9]+$ ]]; then
        tg_groups_field="\"TelegramGroups\": [$tg_group_id],"
      fi

      # Write agent create payload to temp file (avoids shell quoting issues with large JSON)
      payload_file=$(mktemp)
      cat > "$payload_file" <<PAYLOAD
{
  "Name": "$agent_name",
  "ShortName": "$agent_name",
  "DisplayName": "$display_name",
  "Role": "co-cto",
  "Model": "$model",
  "MemoryLimitMb": $memory_mb,
  "Provider": "$agent_provider",
  "PermissionMode": "acceptEdits",
  "MaxTurns": 50,
  "AutoMemoryEnabled": true,
  "ShowStats": false,
  "GroupListenMode": "all",
  "GroupDebounceSeconds": 15,
  "Networks": ["fleet-net"],
  "EnvRefs": ["TELEGRAM_CTO_BOT_TOKEN", "GITHUB_APP_ID", "GITHUB_APP_PEM"],
  "TelegramUsers": [$tg_user_id],
  $tg_groups_field
  "Tools": [
    "Read", "Glob", "Grep", "WebFetch", "WebSearch",
    "mcp__fleet-memory__memory_delete", "mcp__fleet-memory__memory_get",
    "mcp__fleet-memory__memory_list", "mcp__fleet-memory__memory_search",
    "mcp__fleet-memory__memory_stats", "mcp__fleet-memory__memory_store",
    "mcp__fleet-memory__memory_update",
    "mcp__fleet-playwright__browser_click", "mcp__fleet-playwright__browser_close",
    "mcp__fleet-playwright__browser_console_messages", "mcp__fleet-playwright__browser_evaluate",
    "mcp__fleet-playwright__browser_fill_form", "mcp__fleet-playwright__browser_hover",
    "mcp__fleet-playwright__browser_navigate", "mcp__fleet-playwright__browser_navigate_back",
    "mcp__fleet-playwright__browser_network_requests", "mcp__fleet-playwright__browser_press_key",
    "mcp__fleet-playwright__browser_resize", "mcp__fleet-playwright__browser_run_code",
    "mcp__fleet-playwright__browser_select_option", "mcp__fleet-playwright__browser_snapshot",
    "mcp__fleet-playwright__browser_tabs", "mcp__fleet-playwright__browser_take_screenshot",
    "mcp__fleet-playwright__browser_type", "mcp__fleet-playwright__browser_wait_for",
    "mcp__fleet-temporal__request_memory_store",
    "mcp__fleet-temporal__temporal_cancel_workflow", "mcp__fleet-temporal__temporal_create_schedule",
    "mcp__fleet-temporal__temporal_delete_schedule", "mcp__fleet-temporal__temporal_describe_schedule",
    "mcp__fleet-temporal__temporal_get_workflow_result", "mcp__fleet-temporal__temporal_get_workflow_status",
    "mcp__fleet-temporal__temporal_list_schedules", "mcp__fleet-temporal__temporal_list_workflow_types",
    "mcp__fleet-temporal__temporal_list_workflows", "mcp__fleet-temporal__temporal_signal_workflow",
    "mcp__fleet-temporal__temporal_start_workflow", "mcp__fleet-temporal__temporal_terminate_workflow",
    "mcp__fleet-orchestrator__agent_logs", "mcp__fleet-orchestrator__create_agent",
    "mcp__fleet-orchestrator__create_instruction", "mcp__fleet-orchestrator__create_project_context",
    "mcp__fleet-orchestrator__create_workflow_definition", "mcp__fleet-orchestrator__deprovision_agent",
    "mcp__fleet-orchestrator__diff_instruction_versions", "mcp__fleet-orchestrator__ensure_networks_exist",
    "mcp__fleet-orchestrator__get_agent_config", "mcp__fleet-orchestrator__get_agent_history",
    "mcp__fleet-orchestrator__get_agent_status", "mcp__fleet-orchestrator__get_project_context",
    "mcp__fleet-orchestrator__get_uwe_reference", "mcp__fleet-orchestrator__get_workflow_definition",
    "mcp__fleet-orchestrator__list_agent_configs", "mcp__fleet-orchestrator__list_agents",
    "mcp__fleet-orchestrator__list_instruction_versions", "mcp__fleet-orchestrator__list_project_contexts",
    "mcp__fleet-orchestrator__list_repositories", "mcp__fleet-orchestrator__list_workflow_definitions",
    "mcp__fleet-orchestrator__manage_agent_env_refs", "mcp__fleet-orchestrator__manage_agent_instructions",
    "mcp__fleet-orchestrator__manage_agent_mcp_endpoints", "mcp__fleet-orchestrator__manage_agent_networks",
    "mcp__fleet-orchestrator__manage_agent_telegram_groups", "mcp__fleet-orchestrator__manage_agent_telegram_users",
    "mcp__fleet-orchestrator__manage_repository", "mcp__fleet-orchestrator__preview_agent_provision",
    "mcp__fleet-orchestrator__provision_agent", "mcp__fleet-orchestrator__reprovision_agent",
    "mcp__fleet-orchestrator__restart_agent", "mcp__fleet-orchestrator__restart_agent_with_version",
    "mcp__fleet-orchestrator__rollback_instruction", "mcp__fleet-orchestrator__rollback_project_context",
    "mcp__fleet-orchestrator__start_agent", "mcp__fleet-orchestrator__stop_agent",
    "mcp__fleet-orchestrator__system_health", "mcp__fleet-orchestrator__update_agent_config",
    "mcp__fleet-orchestrator__update_instruction", "mcp__fleet-orchestrator__update_project_context",
    "mcp__fleet-orchestrator__update_workflow_definition",
    "mcp__fleet-telegram__send_message", "mcp__fleet-telegram__get_chat_info"
  ],
  "Projects": [],
  "McpEndpoints": [
    {"McpName": "fleet-memory",      "Url": "http://fleet-memory:3100",              "TransportType": "http"},
    {"McpName": "fleet-playwright",  "Url": "http://fleet-playwright:3200/mcp",      "TransportType": "http"},
    {"McpName": "fleet-temporal",    "Url": "http://fleet-temporal-bridge:3001",     "TransportType": "http"},
    {"McpName": "fleet-orchestrator","Url": "http://fleet-orchestrator:3600/mcp",    "TransportType": "http"},
    {"McpName": "fleet-telegram",    "Url": "http://fleet-telegram:3800",            "TransportType": "http"}
  ],
  "Provision": false
}
PAYLOAD

      # 7b. Create agent
      echo "  Creating agent '$agent_name'..."
      response=$(curl -s -w "\n%{http_code}" \
        -H "Authorization: Bearer $ORCH_TOKEN" \
        -H "Content-Type: application/json" \
        -X POST "$ORCH_URL/api/agents" \
        --data-binary "@$payload_file" 2>/dev/null)
      rm -f "$payload_file"

      http_code="${response##*$'\n'}"
      body="${response%$'\n'*}"
      if [[ "$http_code" -lt 200 || "$http_code" -ge 300 ]]; then
        fail "POST /api/agents failed (HTTP $http_code): $body"; exit 1
      fi
      ok "Agent '$agent_name' created"

      # 7c. Assign instructions
      echo "  Assigning instructions [$instr1, $instr2]..."
      response=$(curl -s -w "\n%{http_code}" \
        -H "Authorization: Bearer $ORCH_TOKEN" \
        -H "Content-Type: application/json" \
        -X PUT "$ORCH_URL/api/agents/$agent_name/config" \
        -d "{\"Instructions\":[{\"InstructionName\":\"$instr1\",\"LoadOrder\":0},{\"InstructionName\":\"$instr2\",\"LoadOrder\":1}]}" \
        2>/dev/null)
      http_code="${response##*$'\n'}"
      body="${response%$'\n'*}"
      if [[ "$http_code" -lt 200 || "$http_code" -ge 300 ]]; then
        fail "PUT /api/agents/$agent_name/config failed (HTTP $http_code): $body"; exit 1
      fi
      ok "Instructions assigned"

      # 7d. Reprovision
      echo "  Provisioning container..."
      response=$(curl -s -w "\n%{http_code}" \
        -H "Authorization: Bearer $ORCH_TOKEN" \
        -X POST "$ORCH_URL/api/agents/$agent_name/reprovision" 2>/dev/null)
      http_code="${response##*$'\n'}"
      body="${response%$'\n'*}"
      if [[ "$http_code" -lt 200 || "$http_code" -ge 300 ]]; then
        fail "POST /api/agents/$agent_name/reprovision failed (HTTP $http_code): $body"; exit 1
      fi
      ok "Agent container provisioned"

      # 7e. Auth-token refresh schedules (provider-gated)
      create_refresh_schedule() {
        local provider="$1"     # "claude" or "codex"
        local schedule_id="auth-token-refresh-${provider}-30m"
        local payload
        payload=$(cat <<EOF
{
  "scheduleId": "$schedule_id",
  "namespace": "fleet",
  "workflowType": "AuthTokenRefreshWorkflow",
  "taskQueue": "fleet",
  "cronExpression": "*/30 * * * *",
  "inputJson": "{\"Providers\":[\"$provider\"]}",
  "memo": "Refreshes $provider OAuth tokens every 30 minutes",
  "paused": false
}
EOF
)
        echo "  Creating schedule: $schedule_id..."
        response=$(curl -s -w "\n%{http_code}" \
          -H "Authorization: Bearer $ORCH_TOKEN" \
          -H "Content-Type: application/json" \
          -X POST "$ORCH_URL/api/schedules" \
          -d "$payload" 2>/dev/null)
        http_code="${response##*$'\n'}"
        body="${response%$'\n'*}"
        if [[ "$http_code" -ge 200 && "$http_code" -lt 300 ]]; then
          ok "Schedule '$schedule_id' created"
        elif echo "$body" | grep -qi "already exists\|ALREADY_EXISTS"; then
          ok "Schedule '$schedule_id' already exists"
        else
          warn "Schedule '$schedule_id' failed (HTTP $http_code): $body"
        fi
      }

      if $USE_CLAUDE; then create_refresh_schedule "claude"; fi
      if $USE_CODEX;  then create_refresh_schedule "codex";  fi
    fi

    # ─────────────────────────────────────────────────────────────────────────
    section "[8/8] Verifying agent..."
    # ─────────────────────────────────────────────────────────────────────────

    if $DRY_RUN; then
      echo -e "  ${YELLOW}[dry-run]${NC} Would poll GET $ORCH_URL/api/agents/$agent_name until status=idle|running"
    else
      elapsed=0; timeout=60
      echo -n "  Waiting for $agent_name"
      while [[ $elapsed -lt $timeout ]]; do
        status=$(curl -sf "$ORCH_URL/api/agents/$agent_name" 2>/dev/null \
          | jq -r '.reportedStatus // .status // empty' 2>/dev/null || echo "")
        if [[ "$status" == "idle" || "$status" == "running" ]]; then
          echo; ok "Agent '$agent_name' is up!"; break
        fi
        echo -n "."; sleep 2; elapsed=$((elapsed + 2))
      done
      if [[ $elapsed -ge $timeout ]]; then
        echo
        warn "Agent '$agent_name' not yet idle/running after ${timeout}s — check: docker logs fleet-$agent_name"
      fi
    fi

    echo
    echo -e "${GREEN}══════════════════════════════════════════════════════${NC}"
    echo -e "${GREEN}  Fleet setup complete!${NC}"
    echo -e "${GREEN}══════════════════════════════════════════════════════${NC}"
    echo
    echo "  → Dashboard: http://localhost:3700  (or FLEET_DASHBOARD_PORT if overridden)"
    echo
    echo "  Runtime data lives under: $FLEET_BASE_DIR"
    echo "  To manage services:   cd fleet && docker compose (up|down|logs)"
    echo

  else
    warn "Skipping agent provisioning."
    section "[8/8] Setup complete (services running, no agent provisioned)"
    echo
  fi
fi
