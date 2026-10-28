#!/bin/bash
# Invoked only after the managed enrollment gate; never runs the legacy root/cron setup.
set -euo pipefail
if [ "$(id -u)" = 0 ] || [ -z "${HOME:-}" ] || [ "$HOME" = /root ] || [[ "$HOME" != /* ]]; then
    echo stock_native_nonroot_home_required >&2
    exit 1
fi
# Managed staged launch (#492): create/start runs this as the explicit main process. Every boot
# clears any earlier activation and marks itself staged; the orchestrator transfers inputs while
# no daemon or writer runs, then writes a fresh activation for this boot. Without that activation
# the daemon never starts. The manual profile (no FLEET_STOCK_STAGED) is unchanged.
if [ "${FLEET_STOCK_STAGED:-}" = 1 ]; then
    rm -f "$HOME/.fleet-activate"
    : > "$HOME/.fleet-staged"
    waited=0
    while [ ! -f "$HOME/.fleet-activate" ]; do
        sleep 1
        waited=$((waited + 1))
        if [ "$waited" -ge "${FLEET_STOCK_STAGE_SECONDS:-1800}" ]; then
            echo stock_activation_timeout >&2
            exit 78
        fi
    done
    rm -f "$HOME/.fleet-activate" "$HOME/.fleet-staged"
fi
# No environment discovery, provider substitution, account login or trust installation.
# The same validator is used by shell bootstrap and direct dotnet startup. It creates only
# the native profile and returns the already-validated workspace, never credentials.
WORKDIR=$(dotnet /app/Fleet.Agent.dll --contentRoot /app --prepare-stock-native)
# Do not inherit an alternate native config root. The generated home is the only profile here.
unset CODEX_HOME
cd "$WORKDIR"
exec dotnet /app/Fleet.Agent.dll --contentRoot /app
