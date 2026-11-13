#!/usr/bin/env bash
# The Qdrant release check setup.sh and upgrade.sh run before they recreate the stack.
#
# The compose file pins Qdrant, and a pin moves a host to the pinned release on its next start.
# Qdrant supports moving one minor release at a time, and never backwards, so the scripts refuse
# any other move while the old container is still there to read. See
# docs/upgrading/internal-ports-loopback.md.

# The release the compose file falls back to: the default inside
# `image: qdrant/qdrant:${FLEET_QDRANT_VERSION:-vX.Y.Z}`. The compose file is the only place the pin
# is written. Prints nothing when the line is not there.
qdrant_default_version() {
  local line
  while IFS= read -r line || [[ -n "$line" ]]; do
    case "$line" in
      *'image: qdrant/qdrant:${FLEET_QDRANT_VERSION:-'*'}'*)
        line=${line#*'${FLEET_QDRANT_VERSION:-'}
        printf '%s' "${line%%\}*}"
        return 0
        ;;
    esac
  done < "$1"
}

# The release the next start will run: FLEET_QDRANT_VERSION from the env FILE, never the invoking
# shell, else the compose default. Blanks and one pair of quotes are dropped, as Compose drops them.
qdrant_target_version() { # <env-file> <compose-file>
  local line value=""
  if [[ -f "$1" ]]; then
    while IFS= read -r line || [[ -n "$line" ]]; do
      case "$line" in FLEET_QDRANT_VERSION=*) value=${line#*=}; break ;; esac
    done < "$1"
  fi
  value=${value%$'\r'}
  value="${value#"${value%%[![:space:]]*}"}"
  value="${value%"${value##*[![:space:]]}"}"
  if [[ ${#value} -ge 2 ]]; then
    case "$value" in
      \"*\"|\'*\') value=${value:1:${#value}-2} ;;
    esac
  fi
  [[ -n "$value" ]] || value=$(qdrant_default_version "$2")
  printf '%s' "$value"
}

# What this host runs now. Prints one of:
#   container <label>  a qdrant container exists (running or stopped); <label> may be empty
#   volume             no container, but the data volume exists
#   fresh              neither: nothing was ever started here
qdrant_observe() { # <compose-project>
  local label
  if label=$(docker container inspect qdrant \
      --format '{{ index .Config.Labels "org.opencontainers.image.version" }}' 2>/dev/null); then
    printf 'container %s' "$label"
  elif docker volume inspect "${1}_qdrant_data" >/dev/null 2>&1; then
    printf 'volume'
  else
    printf 'fresh'
  fi
}

# "v1.20.0" or "1.20.0" as "1 20 0". Anything else, a floating tag included, returns 1.
_qdrant_version_numbers() {
  local value=${1#v}
  [[ "$value" =~ ^([0-9]+)\.([0-9]+)\.([0-9]+)$ ]] || return 1
  printf '%d %d %d' "$((10#${BASH_REMATCH[1]}))" "$((10#${BASH_REMATCH[2]}))" "$((10#${BASH_REMATCH[3]}))"
}

# The decision, with no Docker call: <observation from qdrant_observe> <target release>.
# Prints one word and returns 0 only for the two that may proceed:
#   ok          the running release is the target, an older patch of its minor, or one minor below
#   fresh       nothing to move
#   bad_target  the target is not a full release (vX.Y.Z)
#   unreadable  a container exists and its release cannot be read
#   volume      storage exists and no container says which release wrote it
#   too_old     more than one minor below the target, or another major
#   newer       newer than the target, by a patch or more: starting the target would downgrade
qdrant_upgrade_decision() {
  local observed="$1" target="$2" running t r
  t=$(_qdrant_version_numbers "$target") || { printf 'bad_target'; return 1; }
  case "$observed" in
    fresh) printf 'fresh'; return 0 ;;
    volume) printf 'volume'; return 1 ;;
    'container '*|container) running=${observed#container}; running=${running# } ;;
    *) printf 'unreadable'; return 1 ;;
  esac
  r=$(_qdrant_version_numbers "$running") || { printf 'unreadable'; return 1; }

  local t_major t_minor t_patch r_major r_minor r_patch
  read -r t_major t_minor t_patch <<< "$t"
  read -r r_major r_minor r_patch <<< "$r"

  if (( r_major > t_major )); then printf 'newer'; return 1; fi
  if (( r_major < t_major )); then printf 'too_old'; return 1; fi
  if (( r_minor > t_minor )); then printf 'newer'; return 1; fi
  if (( r_minor == t_minor )); then
    if (( r_patch > t_patch )); then printf 'newer'; return 1; fi
    printf 'ok'; return 0
  fi
  if (( r_minor == t_minor - 1 )); then printf 'ok'; return 0; fi
  printf 'too_old'; return 1
}

# What to tell the operator for a decision that refuses: <decision> <observation> <target> <project>.
qdrant_refusal_message() {
  local decision="$1" running="${2#container}" target="$3" project="$4"
  running=${running# }
  case "$decision" in
    bad_target)
      echo "FLEET_QDRANT_VERSION in .env is '$target'. It must name one release, such as v1.19.2."
      ;;
    too_old)
      echo "Qdrant on this host is $running and the next start would run $target."
      echo "Qdrant moves one minor release at a time. Set FLEET_QDRANT_VERSION in .env to the latest"
      echo "patch of the next minor after $running, run this again, and repeat until it reaches the pin."
      ;;
    newer)
      echo "Qdrant on this host is $running and the next start would run $target, which is older."
      echo "Qdrant does not move backwards. Set FLEET_QDRANT_VERSION=$running in .env and run this again."
      ;;
    unreadable)
      echo "A qdrant container exists, but its image does not say which release it is"
      echo "(label org.opencontainers.image.version: '${running}'), and the next start would run $target."
      ;;
    volume)
      echo "Qdrant storage exists (volume ${project}_qdrant_data) but no qdrant container, so the release"
      echo "that wrote it cannot be read, and the next start would run $target."
      echo "Start it on the release that wrote the storage so it can be read, then run this again."
      echo "If fleet/docker-compose.yml no longer names that release, set FLEET_QDRANT_VERSION to it first:"
      echo "  docker compose -p $project -f fleet/docker-compose.yml up -d qdrant"
      ;;
  esac
  case "$decision" in
    unreadable|volume)
      echo "Or drop the index and let fleet-memory rebuild it from fleet/memories on its next start:"
      echo "  docker rm -f qdrant; docker volume rm ${project}_qdrant_data"
      ;;
  esac
  echo "Nothing was stopped or started. See docs/upgrading/internal-ports-loopback.md."
}
