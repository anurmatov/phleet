#!/usr/bin/env bash
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
# Empty process environment: exported COMPOSE_PROFILES must not select extra services.
compose() { env -i PATH="$PATH" HOME="$HOME" FLEET_BASE_DIR="$ROOT" docker compose -f "$ROOT/docker-compose.example.yml" --env-file "$ROOT/.env.example" "$@"; }
base=$(compose config --services)
images=$(compose config --images)
if printf '%s\n' "$base" | grep -Eq '^(fleet-comms|comms-mysql|fleet-comms-ops|comms-minio|comms-minio-init|comms-seaweedfs|comms-seaweedfs-init)$'; then exit 1; fi
if printf '%s\n' "$images" | grep -Eq 'chrislusf/seaweedfs|^fleet:comms$'; then exit 1; fi
echo 'PASS no Comms services or images without profiles'
for profile in comms-media comms-media-seaweedfs; do
  selected=$(compose --profile "$profile" config --services)
  added=$(comm -13 <(printf '%s\n' "$base" | sort) <(printf '%s\n' "$selected" | sort))
  case "$profile" in
    comms-media) expected=$'comms-minio\ncomms-minio-init' ;;
    comms-media-seaweedfs) expected=$'comms-seaweedfs\ncomms-seaweedfs-init' ;;
  esac
  [[ "$added" == "$expected" ]]
  echo "PASS $profile adds only its two services"
done
