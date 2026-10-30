#!/usr/bin/env bash
# #503 W6: the opt-in orchestrator image carries the external `ssh` that the pinned stock CLI spawns for
# `sandbox upload`, and that ssh carries a subsystem upload with the pinned option set
# (OpenShell bdb956f2: openshell-cli ssh.rs ssh_upload_command, openshell-core forward.rs sandbox_ssh_args).
#
# Synthetic only: a stub CLI served on loopback for the build, a throwaway sshd container on a private
# network and local image tags. The stub stands in for the real CLI, so this proves the image's transport
# prerequisite, not a gateway session, an ssh-proxy or a native upload.
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

# 1. Build the opt-in image. The Dockerfile itself checks the CLI digest and version, then installs
#    openssh-client and fails the build without a working ssh.
mkdir -p "$WORK/cli"
printf '#!/bin/sh\necho "openshell 0.1.5"\n' > "$WORK/cli/openshell"
CLI_SHA="$(sha256sum "$WORK/cli/openshell" | cut -d' ' -f1)"
PORT=$((20000 + RANDOM % 20000))
python3 -m http.server "$PORT" --bind 127.0.0.1 --directory "$WORK/cli" >/dev/null 2>&1 &
HTTP_PID=$!
for _ in $(seq 1 50); do curl -fsS -o /dev/null "http://127.0.0.1:$PORT/openshell" && break; sleep 0.1; done
docker build --network host -f "$ROOT/src/Fleet.Orchestrator/Dockerfile" \
  --build-arg "OPENSHELL_CLI_URL=http://127.0.0.1:$PORT/openshell" --build-arg "OPENSHELL_CLI_SHA256=$CLI_SHA" \
  -t "$OPTIN_IMAGE" "$ROOT"
pass "opt-in image builds with the verified CLI and openssh-client"

# 2. Dependency: ssh only in the opt-in image.
docker run --rm --entrypoint sh "$OPTIN_IMAGE" -c 'command -v ssh && ssh -V' || fail "opt-in image has no working ssh"
pass "opt-in image has ssh"
if docker run --rm --entrypoint sh "$DEFAULT_IMAGE" -c 'command -v ssh' >/dev/null 2>&1; then
  fail "default image unexpectedly has ssh"
fi
pass "default image has no ssh (opt-in only)"

# 3. Supported upload: the pinned upload invocation from the opt-in image, through a ProxyCommand,
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
