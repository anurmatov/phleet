#!/usr/bin/env bash
# What the fleet relies on in Google's Antigravity CLI (`agy`), checked against the real pinned
# binary. A gemini agent runs that CLI, and it is closed: nothing below can be read from source.
#
#   bash tests/antigravity-cli/run.sh
#
# The binary runs only inside a container started with `--network none`. In there a stand-in
# answers for the Gemini API (fake_gemini.py, scripted, no model), a recording MCP server answers
# 401 to anything without its header (mcp_recorder.py), and a watcher records every host the CLI
# looks up or connects to (netwatch.py). No Google account, no real key, no real model.
# checks.py drives the CLI the way GeminiExecutor does and asserts: a turn, the key in
# X-Goog-Api-Key and nowhere else, resume by id with history, what an unknown id does, a killed
# process, the model's shell and write tools, MCP tools with the header on every request and the
# 401 on OAuth discovery, a message and instructions at the fleet's limits arriving whole (and the
# CLI cutting what is over its own), the model list, and that the pinned binary neither reaches
# its updater nor changes.
#
#   AGENT_IMAGE    run the checks in this agent image instead: its own /usr/local/bin/agy, its own
#                  `prepare config` writing the MCP file. Default: the release named in the
#                  Dockerfile, downloaded for this machine's architecture and checked against the
#                  Dockerfile's sha256, in RUNTIME_IMAGE, with the MCP file written by the agent
#                  build on this machine (dotnet build src/Fleet.Agent -c Release, or AGENT_DLL).
#   RUNTIME_IMAGE  image of the default run; needs python3 and bash (default python:3.12-slim)
#   AGY_TARBALL    a release archive already on disk, instead of downloading it (still checked)
#   PLATFORM       linux/amd64 or linux/arm64: the other architecture's release, under emulation
#                  (slow; for checking the x64 archive from an arm64 machine)
#   TRANSCRIPT     copy what was sent and received to this file
#   KEEP=1         leave the work directory behind and print where
#
# Exit 0: pass, 1: assertion, 2: infrastructure.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
fail_assert() { echo "FAIL $*" >&2; exit 1; }
fail_infra() { echo "INFRA $*" >&2; exit 2; }
command -v docker > /dev/null || fail_infra "missing docker"

# The pin is the Dockerfile's: one place holds the version and the two checksums.
VERSION="$(sed -n 's/^ARG AGY_CLI_VERSION=//p' "$REPO_ROOT/Dockerfile")"
[ -n "$VERSION" ] || fail_infra "no ARG AGY_CLI_VERSION in the Dockerfile"
pinned() { sed -n "s/^ *$1) AGY_ASSET=\([^;]*\); AGY_SHA256=\([0-9a-f]\{64\}\) ;;.*/\\$2/p" "$REPO_ROOT/Dockerfile"; }

ID="fleet-agy-$$"
WORK="$(mktemp -d)"
chmod 755 "$WORK"
mkdir -p "$WORK/in" "$WORK/out"
RUNTIME_IMAGE="${RUNTIME_IMAGE:-python:3.12-slim}"
cleanup() {
    docker rm -f "$ID" > /dev/null 2>&1 || true
    if [ -n "${KEEP:-}" ]; then echo "kept: $WORK" >&2; return; fi
    # Files the container created are root-owned on Linux.
    docker run --rm -v "$WORK:/work" "${AGENT_IMAGE:-$RUNTIME_IMAGE}" sh -c 'rm -rf /work/out /work/tree' > /dev/null 2>&1 || true
    rm -rf "$WORK"
}
trap cleanup EXIT

if [ -n "${AGENT_IMAGE:-}" ]; then
    echo "Checks run in: $AGENT_IMAGE (its own agy and its own prepare)"
    # The image must carry the switch itself: a shell in the container runs agy too.
    [ "$(docker run --rm --network none --entrypoint sh "$AGENT_IMAGE" -c 'printf %s "${AGY_CLI_DISABLE_AUTO_UPDATE:-}"')" = true ] \
        || fail_assert "the image does not set AGY_CLI_DISABLE_AUTO_UPDATE=true"
    RC=0
    docker run --rm --network none --name "$ID" --entrypoint python3 \
        -e HOME=/root -e HARNESS_PREPARE=image -e AGY_EXPECTED_VERSION="$VERSION" \
        -v "$HERE:/harness:ro" -v "$WORK/out:/out" \
        -v "$HERE/fixtures/appsettings.json:/app/appsettings.json:ro" -v "$HERE/fixtures/mcp.json:/workspace/.mcp.json:ro" \
        "$AGENT_IMAGE" /harness/checks.py || RC=$?
else
    case "${PLATFORM:-$(docker version --format '{{.Server.Arch}}')}" in
        amd64 | x86_64 | linux/amd64) ARCH=amd64 ;;
        arm64 | aarch64 | linux/arm64) ARCH=arm64 ;;
        *) fail_infra "no pinned release for this architecture" ;;
    esac
    ASSET="$(pinned "$ARCH" 1)"; SHA256="$(pinned "$ARCH" 2)"
    [ -n "$ASSET" ] && [ -n "$SHA256" ] || fail_infra "the Dockerfile names no agy asset and sha256 for $ARCH"
    TARBALL="${AGY_TARBALL:-$WORK/$ASSET}"
    if [ -z "${AGY_TARBALL:-}" ]; then
        curl -fsSL "https://github.com/google-antigravity/antigravity-cli/releases/download/$VERSION/$ASSET" -o "$TARBALL" \
            || fail_infra "could not download $ASSET $VERSION"
    fi
    actual="$( (sha256sum "$TARBALL" 2> /dev/null || shasum -a 256 "$TARBALL") | cut -d' ' -f1)"
    [ "$actual" = "$SHA256" ] || fail_assert "$ASSET is not the pinned release (sha256 $actual, the Dockerfile says $SHA256)"
    tar xzf "$TARBALL" -C "$WORK/in" antigravity || fail_infra "the archive holds no file named antigravity"
    echo "Checks run in: $RUNTIME_IMAGE with agy $VERSION ($ASSET, sha256 verified)"

    # The MCP file is written by the agent's own `prepare config`, not by a copy of it here.
    AGENT_DLL="${AGENT_DLL:-$REPO_ROOT/src/Fleet.Agent/bin/Release/net10.0/Fleet.Agent.dll}"
    [ -f "$AGENT_DLL" ] || fail_infra "no agent build at $AGENT_DLL (dotnet build src/Fleet.Agent -c Release, or set AGENT_DLL)"
    mkdir -p "$WORK/tree/app" "$WORK/tree/workspace"
    cp "$HERE/fixtures/appsettings.json" "$WORK/tree/app/appsettings.json"
    cp "$HERE/fixtures/mcp.json" "$WORK/tree/workspace/.mcp.json"
    HOME=/root dotnet "$AGENT_DLL" prepare config --root "$WORK/tree" > "$WORK/prepare.out" 2>&1 \
        || { cat "$WORK/prepare.out" >&2; fail_infra "prepare config failed"; }
    cp "$WORK/tree/root/.gemini/config/mcp_config.json" "$WORK/in/mcp_config.json" \
        || { cat "$WORK/prepare.out" >&2; fail_assert "prepare config wrote no mcp_config.json for a gemini agent"; }
    chmod 644 "$WORK/in/mcp_config.json" "$WORK/in/antigravity"

    RC=0
    docker run --rm --network none --name "$ID" --platform "linux/$ARCH" -e HOME=/root -e AGY_EXPECTED_VERSION="$VERSION" \
        -v "$HERE:/harness:ro" -v "$WORK/in:/in:ro" -v "$WORK/out:/out" \
        "$RUNTIME_IMAGE" bash -c 'install -m 0755 /in/antigravity /usr/local/bin/agy && exec python3 /harness/checks.py' || RC=$?
fi

if [ -n "${TRANSCRIPT:-}" ] && [ -f "$WORK/out/transcript.txt" ]; then
    cp "$WORK/out/transcript.txt" "$TRANSCRIPT"
fi
case "$RC" in
    0) echo "PASS antigravity cli $VERSION" ;;
    1) exit 1 ;;
    *) fail_infra "the checks did not run to the end (exit $RC)" ;;
esac
