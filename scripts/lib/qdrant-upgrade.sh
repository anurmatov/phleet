#!/usr/bin/env bash
# The Qdrant release check setup.sh and upgrade.sh run before they recreate the stack.
#
# The compose file pins Qdrant, and a pin moves a host to the pinned release on its next start.
# Qdrant supports moving one minor release at a time, and never backwards, so the scripts refuse
# any other move while the old container is still there to read. See
# docs/upgrading/internal-ports-loopback.md.

# The release the next start will run, ASKED OF COMPOSE rather than worked out here.
#
# Compose decides which image runs: it reads the env file its own way (the last of two assignments
# wins, a leading blank or an `export` prefix is accepted, quotes and comments are stripped) and
# applies the compose file's default. A second reading of .env in this script would agree with it
# only for the simple cases, and the check below would then approve one release while Compose
# started another. So the caller passes exactly the directory, project, compose file and profile
# arguments it is about to give `docker compose ... up`, and this prints the tag of the qdrant
# image that command resolves. `config` starts nothing.
#
# Prints nothing when Compose cannot be asked, fails, or does not name exactly one
# `qdrant/qdrant:<tag>` image (a digest reference included). The decision treats that as unknown.
qdrant_target_version() { # <directory to run in> <compose-project> <compose-file> [profile args...]
  local directory="$1" project="$2" file="$3" images line found="" count=0
  shift 3
  images=$(cd "$directory" 2>/dev/null \
    && docker compose -p "$project" -f "$file" --env-file .env "$@" config --images qdrant 2>/dev/null) \
    || return 0
  while IFS= read -r line; do
    line=${line%$'\r'}
    case "$line" in
      qdrant/qdrant:*)
        [[ "$line" == "$found" ]] || { found=$line; count=$((count + 1)); }
        ;;
    esac
  done <<< "$images"
  [[ "$count" -eq 1 ]] || return 0
  printf '%s' "${found#qdrant/qdrant:}"
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
#   no_target   Compose did not say which release it would run, and this host has Qdrant state
#   bad_target  the target is not a full release (vX.Y.Z)
#   unreadable  a container exists and its release cannot be read
#   volume      storage exists and no container says which release wrote it
#   too_old     more than one minor below the target, or another major
#   newer       newer than the target, by a patch or more: starting the target would downgrade
# An unknown target follows the rule for the other undecidable cases: a fresh host is not blocked,
# a host with a container or storage is refused.
qdrant_upgrade_decision() {
  local observed="$1" target="$2" running t r
  if [[ -z "$target" ]]; then
    if [[ "$observed" == fresh ]]; then printf 'fresh'; return 0; fi
    printf 'no_target'; return 1
  fi
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
    no_target)
      echo "This host has a Qdrant container or storage, and 'docker compose config' did not say which"
      echo "Qdrant release the next start would run, so the move cannot be checked."
      echo "Run it yourself to see why:  docker compose -p $project -f fleet/docker-compose.yml config --images qdrant"
      ;;
    bad_target)
      echo "The next start would run qdrant/qdrant:$target, which is not one full release."
      echo "FLEET_QDRANT_VERSION in .env must name one release, such as v1.19.2."
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
