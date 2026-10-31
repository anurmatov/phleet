#!/usr/bin/env bash
# #503 W6/W7: the opt-in orchestrator image carries the external tools the pinned stock CLI spawns for
# `sandbox upload`: `git rev-parse` for its default .gitignore filtering (openshell-cli run.rs
# discover_git_repo_root, run even outside a repository) and `ssh` for the transport (ssh.rs
# ssh_upload_command, openshell-core forward.rs sandbox_ssh_args; OpenShell bdb956f2).
#
# The build uses the real pinned CLI from the public v0.1.5 release (archive and binary digests checked),
# served on loopback. The planning check runs that CLI's default upload (filtering on, no --no-git-ignore) for
# the adapter's source shapes (a regular directory and a single file) against an unreachable gateway.
# The transport check runs the pinned ssh invocation into a throwaway sshd on a private network.
# Local image tags only; no gateway session, ssh-proxy, credential or native upload is exercised.
#
#   test-stock-upload-transport.sh [DEFAULT_IMAGE]   # DEFAULT_IMAGE: the image built without the opt-in args
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEFAULT_IMAGE="${1:-orchestrator-test:pr}"
OPTIN_IMAGE="orchestrator-test:optin-upload"
SSHD_IMAGE="stock-upload-sshd:test"
RUN_ID="stock-upload-$$"
WORK="$(mktemp -d)"
HTTP_PID=""

cleanup() {
  docker rm -f "$RUN_ID-sshd" >/dev/null 2>&1 || true
  docker network rm "$RUN_ID-net" >/dev/null 2>&1 || true
  [[ -n "$HTTP_PID" ]] && kill "$HTTP_PID" 2>/dev/null || true
  rm -rf "$WORK"
}
trap cleanup EXIT

fail() { echo "FAIL: $*" >&2; exit 1; }
pass() { echo "ok - $*"; }

# The pinned stock CLI 0.1.5: release archive and the binary inside it.
CLI_ARCHIVE_URL="https://github.com/NVIDIA/OpenShell/releases/download/v0.1.5/openshell-x86_64-unknown-linux-musl.tar.gz"
CLI_ARCHIVE_SHA="ca1ad6e6fbb4a6cd407e95763391b8ecff02912e7d9c467e7ca22696fe80af69"
CLI_SHA="446f766842d2bf03abc37b56a3f5a3e46b96140b5b6c305d3f168d67921fd271"

# 1. Build the opt-in image. The Dockerfile itself checks the CLI digest and version, then installs
#    openssh-client and git and fails the build without either.
mkdir -p "$WORK/cli"
curl -fsSL "$CLI_ARCHIVE_URL" -o "$WORK/cli.tar.gz"
echo "$CLI_ARCHIVE_SHA  $WORK/cli.tar.gz" | sha256sum -c - >/dev/null || fail "CLI archive digest mismatch"
tar -xzf "$WORK/cli.tar.gz" -C "$WORK/cli" openshell
echo "$CLI_SHA  $WORK/cli/openshell" | sha256sum -c - >/dev/null || fail "CLI binary digest mismatch"
PORT=$((20000 + RANDOM % 20000))
python3 -m http.server "$PORT" --bind 127.0.0.1 --directory "$WORK/cli" >/dev/null 2>&1 &
HTTP_PID=$!
for _ in $(seq 1 50); do curl -fsS -o /dev/null "http://127.0.0.1:$PORT/openshell" && break; sleep 0.1; done
docker build --network host -f "$ROOT/src/Fleet.Orchestrator/Dockerfile" \
  --build-arg "OPENSHELL_CLI_URL=http://127.0.0.1:$PORT/openshell" --build-arg "OPENSHELL_CLI_SHA256=$CLI_SHA" \
  -t "$OPTIN_IMAGE" "$ROOT"
pass "opt-in image builds with the pinned CLI, openssh-client and git"

# 2. Dependency: ssh and git only in the opt-in image.
docker run --rm --entrypoint sh "$OPTIN_IMAGE" -c 'command -v ssh && ssh -V && command -v git && git --version' \
  || fail "opt-in image lacks a working ssh or git"
pass "opt-in image has ssh and git"
for tool in ssh git; do
  if docker run --rm --entrypoint sh "$DEFAULT_IMAGE" -c "command -v $tool" >/dev/null 2>&1; then
    fail "default image unexpectedly has $tool"
  fi
done
pass "default image has neither ssh nor git (opt-in only)"
# #507: both orchestrator images carry the exact public workspace copy helper (no extra dependency).
for image in "$DEFAULT_IMAGE" "$OPTIN_IMAGE"; do
  docker run --rm --entrypoint sh "$image" -c 'test -f /app/stock-workspace-copy.py && test ! -L /app/stock-workspace-copy.py && cat /app/stock-workspace-copy.py' \
    | cmp -s - "$ROOT/scripts/stock-workspace-copy.py" || fail "$image lacks the exact workspace copy helper"
done
pass "both orchestrator images carry the workspace copy helper"

# 3. Real CLI default local planning (Git filtering on, as the adapter calls it) for a regular directory
#    and a single file. Planning succeeds when the CLI warns that the source is outside a Git work tree
#    and starts the upload; only then does the unreachable gateway end the command. The control without
#    git on PATH must stop at planning.
plan() { # $1: an environment prefix applied to the CLI invocation only
  printf '%s\n' 'mkdir -p /tmp/bundle/app/sub && echo synthetic > /tmp/bundle/app/sub/file && echo {} > /tmp/bundle/enrollment.json' \
    "for source in /tmp/bundle/app /tmp/bundle/enrollment.json; do $1 openshell --gateway-endpoint http://127.0.0.1:9 sandbox upload probe \"\$source\" /sandbox/dst 2>&1 || true; done"
}
planned="$(docker run --rm --entrypoint sh "$OPTIN_IMAGE" -c "$(plan '')")"
for source in /tmp/bundle/app /tmp/bundle/enrollment.json; do
  grep -qF "Warning: $source is outside a Git work tree; uploading without Git filtering" <<<"$planned" \
    || fail "real CLI planning did not resolve $source as outside a Git work tree: $planned"
  grep -qF "Uploading $source -> sandbox:/sandbox/dst" <<<"$planned" || fail "real CLI did not start uploading $source: $planned"
done
! grep -qF "Git filtering failed" <<<"$planned" || fail "real CLI Git filtering failed in the opt-in image: $planned"
pass "real CLI default planning passes for a regular directory and a single file"
control="$(docker run --rm --entrypoint sh "$OPTIN_IMAGE" -c "$(plan 'env PATH=/usr/local/bin')")"
grep -qF "failed to run git rev-parse" <<<"$control" || fail "control without git did not stop at planning: $control"
pass "control: without git the real CLI stops at planning (failed to run git rev-parse)"

# #507 review F1: the real CLI's Git planning in a staged user repository whose own config runs a canary through
# core.fsmonitor. Without the adapter's command-scope settings (ProcessStockCli.NeutralizeGit) the canary runs (positive
# control); with them it never runs and ignore filtering still plans the upload.
canary_plan() { # $1: an environment prefix applied to the CLI invocation only
  printf '%s\n' 'git init -q /tmp/repo && printf "#!/bin/sh\ntouch /tmp/canary\n" > /tmp/fsmonitor.sh && chmod 755 /tmp/fsmonitor.sh' \
    'git -C /tmp/repo config core.fsmonitor /tmp/fsmonitor.sh && echo ignored.log > /tmp/repo/.gitignore && echo k > /tmp/repo/kept.txt && echo i > /tmp/repo/ignored.log' \
    "$1 openshell --gateway-endpoint http://127.0.0.1:9 sandbox upload probe /tmp/repo /sandbox/dst 2>&1 || true" \
    'if [ -e /tmp/canary ]; then echo CANARY_RAN; else echo CANARY_ABSENT; fi'
}
NEUTRAL='env GIT_CONFIG_COUNT=2 GIT_CONFIG_KEY_0=core.fsmonitor GIT_CONFIG_VALUE_0=false GIT_CONFIG_KEY_1=core.hooksPath GIT_CONFIG_VALUE_1=/dev/null'
grep -qF 'GitCommandScope = [("core.fsmonitor", "false"), ("core.hooksPath", "/dev/null")]' "$ROOT/src/Fleet.Orchestrator/Services/StockOpenShellRuntime.cs" \
  || fail "NEUTRAL no longer matches the adapter's GitCommandScope"
unguarded="$(docker run --rm --entrypoint sh "$OPTIN_IMAGE" -c "$(canary_plan '')")"
grep -qx CANARY_RAN <<<"$unguarded" || fail "control: the canary repository did not exercise core.fsmonitor: $unguarded"
guarded="$(docker run --rm --entrypoint sh "$OPTIN_IMAGE" -c "$(canary_plan "$NEUTRAL")")"
grep -qx CANARY_ABSENT <<<"$guarded" || fail "the real CLI ran repository config despite command-scope settings: $guarded"
grep -qF "Uploading /tmp/repo -> sandbox:/sandbox/dst" <<<"$guarded" || fail "guarded Git planning did not start the upload: $guarded"
! grep -qF "Git filtering failed" <<<"$guarded" || fail "guarded Git filtering failed: $guarded"
pass "real CLI Git planning never runs repository config under the adapter's settings (control ran it)"

# 4. Supported upload: the pinned upload invocation from the opt-in image, through a ProxyCommand,
#    into a throwaway sshd whose openshell-upload-v1 subsystem stores stdin.
mkdir -p "$WORK/keys" "$WORK/client"
ssh-keygen -q -t ed25519 -N '' -f "$WORK/client/id" >/dev/null
cp "$WORK/client/id.pub" "$WORK/keys/authorized_keys"
docker build -t "$SSHD_IMAGE" - <<'EOF'
FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends openssh-server && rm -rf /var/lib/apt/lists/* \
  && ssh-keygen -A && mkdir -p /run/sshd /received \
  && printf '#!/bin/sh\ncat > /received/archive\n' > /usr/local/bin/receive-upload && chmod 0755 /usr/local/bin/receive-upload \
  && printf '%s\n' 'Port 2222' 'PermitRootLogin prohibit-password' 'PubkeyAuthentication yes' \
       'PasswordAuthentication no' 'KbdInteractiveAuthentication no' 'UsePAM no' 'StrictModes no' \
       'AuthorizedKeysFile /keys/authorized_keys' 'Subsystem openshell-upload-v1 /usr/local/bin/receive-upload' \
       > /etc/ssh/sshd_config
ENTRYPOINT ["/usr/sbin/sshd", "-D", "-e"]
EOF
docker network create --internal "$RUN_ID-net" >/dev/null
docker run -d --name "$RUN_ID-sshd" --network "$RUN_ID-net" --network-alias sshd -v "$WORK/keys:/keys:ro" "$SSHD_IMAGE" >/dev/null

head -c 65536 /dev/urandom > "$WORK/client/payload"
EXPECTED="$(sha256sum "$WORK/client/payload" | cut -d' ' -f1)"
# The pinned option set, verbatim except the ProxyCommand target (a TCP bridge instead of `openshell ssh-proxy`)
# and the test identity/user. An option this ssh did not support would fail the upload.
UPLOAD='ssh -i /client/id -l root \
  -o "ProxyCommand=bash -c '"'"'exec 3<>/dev/tcp/sshd/2222; cat <&3 & exec cat >&3'"'"'" \
  -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o GlobalKnownHostsFile=/dev/null -o LogLevel=ERROR \
  -o ControlMaster=no -o ControlPath=none -o ControlPersist=no \
  -o ServerAliveInterval=15 -o ServerAliveCountMax=3 \
  -o ForkAfterAuthentication=no -T -o RequestTTY=no -s sandbox openshell-upload-v1 < /client/payload'
uploaded=""
for _ in $(seq 1 20); do
  if docker run --rm --network "$RUN_ID-net" -v "$WORK/client:/client:ro" \
       --entrypoint sh "$OPTIN_IMAGE" -c "$UPLOAD"; then uploaded=1; break; fi
  sleep 1 # sshd may still be starting
done
[[ -n "$uploaded" ]] || fail "pinned subsystem upload from the opt-in image failed"
ACTUAL="$(docker exec "$RUN_ID-sshd" sha256sum /received/archive | cut -d' ' -f1)"
[[ "$ACTUAL" == "$EXPECTED" ]] || fail "uploaded bytes differ ($ACTUAL != $EXPECTED)"
pass "pinned subsystem upload from the opt-in image delivers the exact bytes"
