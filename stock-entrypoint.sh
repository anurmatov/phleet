#!/bin/bash
# Invoked only after the managed enrollment gate; never runs the legacy root/cron setup.
set -euo pipefail
if [ "$(id -u)" = 0 ] || [ -z "${HOME:-}" ] || [ "$HOME" = /root ] || [[ "$HOME" != /* ]]; then
    echo stock_native_nonroot_home_required >&2
    exit 1
fi
# No environment discovery, provider substitution, account login or trust installation.
# The same validator is used by shell bootstrap and direct dotnet startup. It creates only
# the native profile and returns the already-validated workspace, never credentials.
WORKDIR=$(dotnet /app/Fleet.Agent.dll --contentRoot /app --prepare-stock-native)
# Do not inherit an alternate native config root. The generated home is the only profile here.
unset CODEX_HOME
cd "$WORKDIR"
exec dotnet /app/Fleet.Agent.dll --contentRoot /app
