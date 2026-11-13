#!/bin/bash
# What has to happen in the shell before the agent process exists: the hosted-key hand-off, CA
# trust, credential files, GitHub auth, and waiting for the broker. Everything that reads the
# generated appsettings.json is in the agent binary (`dotnet Fleet.Agent.dll prepare`,
# src/Fleet.Agent/Prepare/): this script holds no option name, provider list or config-file format
# of its own, and a configuration that cannot be read stops the container instead of starting it
# as a claude agent.
set -u

# The agent binary's one-shot mode. `facts` prints NAME=value lines; `config` writes the provider
# CLI's own configuration file. It reads appsettings.json through the options the host binds.
fleet_prepare() { dotnet Fleet.Agent.dll prepare "$@"; }

# Unconditionally, for every agent and provider: these names are reserved for hosted routing and
# must not reach PID 1 or anything it starts, `fleet_prepare` included. So they leave the
# environment first, before any process is started, and the values wait in shell variables that
# are never exported. A non-hosted agent that carries one loses it here.
# The list must equal HostedModelProviders.KeyEnvVars; EntrypointReservedKeysTests reads the line
# directly below the marker, so keep it one line and in this exact form.
_RESERVED_NAMES=""
# phleet:reserved-key-names
for _HOSTED_VAR in ZAI_CODING_PLAN_API_KEY; do
    printf -v "_RESERVED_VALUE_${_HOSTED_VAR}" '%s' "${!_HOSTED_VAR:-}"
    unset "$_HOSTED_VAR"
    _RESERVED_NAMES="${_RESERVED_NAMES} ${_HOSTED_VAR}"
done
unset _HOSTED_VAR

# Facts from the generated configuration. A missing or empty Agent.Provider means claude; an unknown
# one (PV1), an unreadable file, or a hosted provider without a usable key name is refused there.
if ! _PREPARE_FACTS="$(fleet_prepare facts)"; then
    echo "ERROR: the agent configuration was refused (see above); not starting." >&2
    exit 1
fi
PROVIDER="" HOSTED_PROVIDER="" HOSTED_KEY_ENV="" HOSTED_KEY_FILE="" HOSTED_KEY_MISSING=""
CLAUDE_LOCAL_MODEL="" GH_TOKEN_PLACEHOLDER=""
# The names are EntrypointPrepare.FactNames; EntrypointPrepareScriptTests keeps the two equal.
# phleet:prepare-facts
while IFS='=' read -r _FACT_NAME _FACT_VALUE; do
    case "$_FACT_NAME" in
        PROVIDER) PROVIDER="$_FACT_VALUE" ;;
        HOSTED_PROVIDER) HOSTED_PROVIDER="$_FACT_VALUE" ;;
        HOSTED_KEY_ENV) HOSTED_KEY_ENV="$_FACT_VALUE" ;;
        HOSTED_KEY_FILE) HOSTED_KEY_FILE="$_FACT_VALUE" ;;
        HOSTED_KEY_MISSING) HOSTED_KEY_MISSING="$_FACT_VALUE" ;;
        CLAUDE_LOCAL_MODEL) CLAUDE_LOCAL_MODEL="$_FACT_VALUE" ;;
        GH_TOKEN_PLACEHOLDER) GH_TOKEN_PLACEHOLDER="$_FACT_VALUE" ;;
    esac
done <<< "$_PREPARE_FACTS"
unset _PREPARE_FACTS _FACT_NAME _FACT_VALUE
if [ -z "$PROVIDER" ] || [ -z "$HOSTED_PROVIDER" ] || [ -z "$HOSTED_KEY_FILE" ] || [ -z "$HOSTED_KEY_MISSING" ] \
    || [ -z "$CLAUDE_LOCAL_MODEL" ] || [ -z "$GH_TOKEN_PLACEHOLDER" ]; then
    echo "ERROR: the agent binary did not report its configuration; not starting." >&2
    exit 1
fi

# Hosted model provider (#335 D5). The orchestrator computes both values from the shared registry;
# this script holds no prefix list of its own. When set, the key moves from the shell variable above
# into a 0400 file that the agent reads once and deletes, so PID 1 never carries it.
if [ "$HOSTED_PROVIDER" = "true" ]; then
    # Never print the value — only the variable name.
    if ! [[ "$HOSTED_KEY_ENV" =~ ^[A-Z_][A-Z0-9_]*$ ]]; then
        echo "ERROR: hosted provider enabled but Agent.HostedProviderKeyEnv is missing or invalid." >&2
        exit 1
    fi
    case " $_RESERVED_NAMES " in
        *" $HOSTED_KEY_ENV "*)
            _HOSTED_STASH="_RESERVED_VALUE_${HOSTED_KEY_ENV}"
            _HOSTED_KEY_VALUE="${!_HOSTED_STASH:-}"
            ;;
        *)
            # Not a reserved name: the orchestrator and this image disagree about the registry. The
            # agent's own parity check refuses to start; the value is handed over as it always was.
            _HOSTED_KEY_VALUE="${!HOSTED_KEY_ENV:-}"
            ;;
    esac
    _HOSTED_KEY_TRIMMED="$(printf '%s' "$_HOSTED_KEY_VALUE" | tr -d '[:space:]')"
    if [ -z "$_HOSTED_KEY_TRIMMED" ] || [ "$_HOSTED_KEY_TRIMMED" = "$HOSTED_KEY_MISSING" ]; then
        echo "ERROR: ${HOSTED_KEY_ENV} is unset, blank or '${HOSTED_KEY_MISSING}'. Put the key in .env, attach ${HOSTED_KEY_ENV} as an Env Ref, then reprovision." >&2
        exit 1
    fi
    if ! ( umask 077 && rm -f "$HOSTED_KEY_FILE" && printf '%s' "$_HOSTED_KEY_VALUE" > "$HOSTED_KEY_FILE" && chmod 0400 "$HOSTED_KEY_FILE" ); then
        rm -f "$HOSTED_KEY_FILE" 2>/dev/null
        echo "ERROR: could not write the ${HOSTED_KEY_ENV} key file ${HOSTED_KEY_FILE}." >&2
        exit 1
    fi
    unset _HOSTED_KEY_VALUE _HOSTED_KEY_TRIMMED _HOSTED_STASH
fi

for _HOSTED_VAR in $_RESERVED_NAMES; do
    _HOSTED_STASH="_RESERVED_VALUE_${_HOSTED_VAR}"
    if [ -n "${!_HOSTED_STASH:-}" ] && [ "$_HOSTED_VAR" != "$HOSTED_KEY_ENV" ]; then
        echo "NOTE: unsetting ${_HOSTED_VAR}; it is reserved for hosted-provider routing and this agent does not use it." >&2
    fi
    unset "$_HOSTED_STASH"
done
unset _HOSTED_VAR _HOSTED_STASH _RESERVED_NAMES

# Secret placeholders. When this agent holds a bound secret, the orchestrator mounts the fleet-secrets
# CA here and points each bound host at fleet-secrets in /etc/hosts. Trust the CA before anything
# talks to one of those hosts. A failure is fatal: every bound host would fail TLS, and the agent
# would look healthy while none of its credentials worked.
FLEET_SECRETS_CA=/usr/local/share/ca-certificates/fleet-secrets.crt
if [ -e "$FLEET_SECRETS_CA" ]; then
    if [ ! -f "$FLEET_SECRETS_CA" ] || [ ! -s "$FLEET_SECRETS_CA" ]; then
        echo "ERROR: $FLEET_SECRETS_CA is not a certificate file. Start fleet-secrets so it creates its CA, then reprovision." >&2
        exit 1
    fi
    if ! update-ca-certificates > /dev/null 2>&1; then
        echo "ERROR: could not add the fleet-secrets CA to the system trust store." >&2
        exit 1
    fi
    echo "Secret placeholders: trusting the fleet-secrets CA."
fi

# Bound SSH keys. The key stays in fleet-secrets: this directory holds only a signing socket
# (SSH_AUTH_SOCK already points at it) and the host keys of the hosts the key is bound to. ssh reads
# those as its system-wide known hosts, so a bound host is verified from the first connection.
FLEET_SECRETS_SSH=/run/fleet-secrets/ssh
if [ -d "$FLEET_SECRETS_SSH" ]; then
    mkdir -p /etc/ssh
    ln -sfn "$FLEET_SECRETS_SSH/known_hosts" /etc/ssh/ssh_known_hosts
    echo "Secret placeholders: SSH keys are used through $FLEET_SECRETS_SSH/agent.sock."
fi

if [ "$PROVIDER" = "gemini" ]; then
    # Gemini auth — OAuth credentials mounted writable directly as ~/.gemini/oauth_creds.json.
    # Two-level refresh: (1) the CLI's google-auth-library refreshes tokens in-place on expiry;
    # (2) AuthTokenRefreshWorkflow provides a host-side safety net every 30 min, propagating
    # refreshed tokens back to the host to prevent stale-token failures on container restart.
    if [ ! -f "${HOME}/.gemini/oauth_creds.json" ]; then
        echo "ERROR: ~/.gemini/oauth_creds.json not mounted." >&2
        echo "Run setup.sh on the host to complete Gemini OAuth setup, then reprovision the agent." >&2
        exit 1
    fi
    chmod 0600 "${HOME}/.gemini/oauth_creds.json"

    # Enforce OAuth-only auth (issue #132 MUST NOT #2). The gemini CLI auto-detects
    # GEMINI_API_KEY / GOOGLE_API_KEY and prefers them over oauth_creds.json. A leftover
    # API key in the cluster .env (e.g. from the PR #129 SDK era) would silently route
    # traffic through the API-key billing tier instead of OAuth, surfacing as confusing
    # 429 "prepayment credits depleted" errors mid-task. Fail fast with a clear message
    # so the operator can clean up .env before the agent starts taking traffic.
    if [ -n "${GEMINI_API_KEY:-}" ] || [ -n "${GOOGLE_API_KEY:-}" ]; then
        echo "ERROR: GEMINI_API_KEY or GOOGLE_API_KEY is set but provider=gemini uses OAuth." >&2
        echo "Remove the API key from ./fleet/.env (and the agent's env refs), then reprovision." >&2
        echo "OAuth credentials at ~/.gemini/oauth_creds.json are the single source of truth." >&2
        exit 1
    fi

    # Trust the workspace directory so --yolo is not silently downgraded to "default"
    # approval mode. Without this the CLI prompts for every tool call approval, which
    # hangs indefinitely in headless mode (no human present to approve).
    export GEMINI_CLI_TRUST_WORKSPACE=true

    # ~/.gemini/settings.json: /workspace/.mcp.json translated into the CLI's mcpServers block and
    # the OAuth auth selector, merged into whatever the file already holds. The CLI reads MCP
    # servers only from there and does not discover .mcp.json, so without this step a gemini agent
    # has no MCP tools (memory, temporal, orchestrator, playwright, ...). stdio servers are skipped:
    # the CLI speaks HTTP/SSE only. Without .mcp.json only the auth selector is written. A file that
    # cannot be written is a warning; the agent starts without MCP tools.
    fleet_prepare config || echo "WARN: gemini settings.json not written: prepare exited non-zero" >&2

elif [ "$PROVIDER" = "codex" ]; then
    if [ "$HOSTED_PROVIDER" = "true" ]; then
        # A hosted-provider agent holds no OpenAI credential (#335 D6). /root/.codex persists in the
        # workspace volume, so an auth.json from an earlier model must be removed, not just skipped.
        rm -f /root/.codex/auth.json
    elif [ -f /root/.codex-host/auth.json ]; then
        # Codex auth — always overwrite from host mount (source of truth, kept fresh by AuthTokenRefreshWorkflow)
        mkdir -p /root/.codex
        cp /root/.codex-host/auth.json /root/.codex/auth.json
    fi
    # Codex app-server threads are process-lifetime state only in phleet. Clear any stale
    # persisted sessions on cold start so a reprovision cannot accidentally inherit old rollout state.
    rm -rf /root/.codex/sessions/* 2>/dev/null || true
    # ~/.codex/config.toml (#460): the orchestrator-generated root settings first, then one
    # [mcp_servers.<name>] table per URL server with its enabled_tools whitelist. Root keys and their
    # [analytics]/[history] tables must come before any [mcp_servers.*] table — a key written after a
    # table header belongs to that table. A missing, unreadable or directory /app/codex-root.toml
    # (provisioning mounts it read-only for Codex agents) prints one WARN and the MCP tables are still
    # written, so the agent never loses its MCP tools; the CLI then uses its built-in root defaults.
    # Written even without .mcp.json, so a stale file from an earlier start never survives.
    mkdir -p /root/.codex
    fleet_prepare config || echo "WARN: codex config.toml not written: prepare exited non-zero" >&2
elif [ "$PROVIDER" != "claude" ]; then
    # PV1 (#386): an unknown provider fails closed. `prepare facts` refuses one before this point;
    # this is the branch's own default, so a name this script has no case for never falls through
    # to the Claude branch below and its credentials.
    echo "ERROR: Unknown provider '${PROVIDER}'; this image has no credential handling for it." >&2
    exit 1
elif [ "$CLAUDE_LOCAL_MODEL" = "true" ]; then
    # A local-model agent holds no Claude OAuth credential (#340 D3). /root/.claude persists in the
    # workspace volume, so a credentials file from an earlier life must be removed, not just skipped.
    rm -f /root/.claude/.credentials.json
    if [ -e /root/.claude/.credentials.json ] || [ -L /root/.claude/.credentials.json ]; then
        echo "ERROR: Claude local model mode, but /root/.claude/.credentials.json could not be removed (bind-mounted?). Remove the mount, then reprovision." >&2
        exit 1
    fi
else
    # Provider claude. Claude auth — always overwrite from host mount (source of truth, kept fresh by AuthTokenRefreshWorkflow)
    if [ -f /root/.claude-host.json ]; then
        cp /root/.claude-host.json /root/.claude.json
    fi
    if [ -f /root/.claude-host/.credentials.json ]; then
        mkdir -p /root/.claude
        cp /root/.claude-host/.credentials.json /root/.claude/.credentials.json
    fi
fi
# Git identity
git config --global user.name "${GIT_USER_NAME:-Fleet Agent}"
git config --global user.email "${GIT_USER_EMAIL:-fleet@example.com}"
git config --global safe.directory '*'

# GitHub App auth (opt-in: only runs if GITHUB_APP_ID / GITHUB_APP_ID_OVERRIDE + PEM available)
# Prefer GITHUB_APP_PEM_OVERRIDE over GITHUB_APP_PEM so per-agent override takes effect at startup.
_GITHUB_PEM_B64="${GITHUB_APP_PEM_OVERRIDE:-${GITHUB_APP_PEM:-}}"
if [ "$GH_TOKEN_PLACEHOLDER" = "true" ]; then
    # The App key is a bound secret: this container holds a placeholder, not a key or a token.
    # fleet-secrets mints installation tokens outside and swaps one in on the way to GitHub.
    /app/gh-auth.sh --placeholder
elif [ -n "${_GITHUB_PEM_B64:-}" ]; then
    echo "$_GITHUB_PEM_B64" | base64 -d > /tmp/github-app-key.pem
    chmod 600 /tmp/github-app-key.pem
fi
if [ "$GH_TOKEN_PLACEHOLDER" = "true" ]; then
    :
elif [ -n "${GITHUB_APP_ID_OVERRIDE:-${GITHUB_APP_ID:-}}" ] && [ -f /tmp/github-app-key.pem ]; then
    /app/gh-auth.sh
    # Refresh token every 45min (installation tokens expire after 1h).
    # Pass override vars so cron refreshes honour the same App identity as startup.
    echo "*/45 * * * * GITHUB_APP_ID=${GITHUB_APP_ID:-} GITHUB_APP_ID_OVERRIDE=${GITHUB_APP_ID_OVERRIDE:-} /app/gh-auth.sh >> /var/log/gh-auth.log 2>&1" | crontab -
    cron
fi

# Workspace
mkdir -p /workspace/repos /workspace/.fleet/scheduled /workspace/share

# MinIO file sharing — configure mc alias if credentials are present
if [ -n "${MINIO_ACCESS_KEY:-}" ] && [ -n "${MINIO_SECRET_KEY:-}" ]; then
    mc alias set fleet "http://fleet-minio:9000" "$MINIO_ACCESS_KEY" "$MINIO_SECRET_KEY" --api S3v4 > /dev/null 2>&1 || true
fi

# Wait for infrastructure dependencies before starting the agent.
# Prevents crash-restart cycles on macstudio reboot when RabbitMQ/orchestrator
# aren't ready yet. RabbitMqConnectionHelper retries internally too, but waiting
# here avoids noisy crash loops before dotnet even starts.

wait_for_tcp() {
    local host=$1 port=$2 label=$3 deadline=$4
    local delay=2
    echo "Waiting for $label ($host:$port)..."
    while ! (echo > /dev/tcp/$host/$port) 2>/dev/null; do
        if [ "$(date +%s)" -ge "$deadline" ]; then
            echo "ERROR: Timed out waiting for $label after 2 minutes" >&2
            exit 1
        fi
        sleep "$delay"
        delay=$(( delay < 30 ? delay * 2 : 30 ))
    done
    echo "$label is ready."
}

wait_for_http() {
    local url=$1 label=$2 deadline=$3
    local delay=2
    echo "Waiting for $label ($url)..."
    while ! curl -sf "$url" > /dev/null 2>&1; do
        if [ "$(date +%s)" -ge "$deadline" ]; then
            echo "ERROR: Timed out waiting for $label after 2 minutes" >&2
            exit 1
        fi
        sleep "$delay"
        delay=$(( delay < 30 ? delay * 2 : 30 ))
    done
    echo "$label is ready."
}

DEADLINE=$(( $(date +%s) + 120 ))

wait_for_tcp  "rabbitmq"        5672 "RabbitMQ"    "$DEADLINE"
wait_for_http "http://fleet-orchestrator:3600/health" "Orchestrator" "$DEADLINE"

exec dotnet Fleet.Agent.dll
