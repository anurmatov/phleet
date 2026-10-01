#!/usr/bin/env bash
# Install-time choices are read from the file, never the invoking shell.
_comms_env_get() {
  [[ -f "$1" ]] || return 0
  local line
  while IFS= read -r line || [[ -n "$line" ]]; do
    case "$line" in "$2="*) printf '%s' "${line#*=}"; return 0 ;; esac
  done < "$1"
}

comms_env_set() {
  local file="$1" key="$2" value="$3" tmp
  if [[ "${COMMS_DRY_RUN:-false}" == true ]]; then
    printf 'Would write %s=<value>\n' "$key"; return 0
  fi
  # Values cannot inject another line or a replacement expression.
  [[ "$key" =~ ^[A-Z][A-Z0-9_]*$ && "$value" != *$'\n'* && "$value" != *$'\r'* ]] || return 1
  tmp=$(mktemp "${file}.XXXXXX") || return 1
  if ! COMMS_SET_KEY="$key" COMMS_SET_VALUE="$value" awk '
    BEGIN { key=ENVIRON["COMMS_SET_KEY"]; value=ENVIRON["COMMS_SET_VALUE"]; found=0 }
    index($0, key "=")==1 { if (!found) print key "=" value; found=1; next }
    { print }
    END { if (!found) print key "=" value }
  ' "$file" > "$tmp"; then rm -f "$tmp"; return 1; fi
  # Write in place: keep permissions and an existing bind mount's inode.
  if ! cat "$tmp" > "$file"; then rm -f "$tmp"; return 1; fi
  rm -f "$tmp"
}

comms_backfill_store_key() {
  local file="$1"
  if ! grep -q '^FLEET_COMMS_MEDIA_STORE=' "$file" &&
     [[ -n "$(_comms_env_get "$file" FLEET_COMMS_MEDIA_ENDPOINT)" ||
        -n "$(_comms_env_get "$file" FLEET_COMMS_MINIO_ROOT_USER)" ]]; then
    comms_env_set "$file" FLEET_COMMS_MEDIA_STORE minio || return
    printf 'recorded FLEET_COMMS_MEDIA_STORE=minio\n'
  fi
}

comms_enable_media() {
  local file="$1" store endpoint
  store=$(_comms_env_get "$file" FLEET_COMMS_MEDIA_STORE)
  case "$store" in
    '')
      if grep -q '^FLEET_COMMS_MEDIA_STORE=' "$file"; then
        printf 'invalid FLEET_COMMS_MEDIA_STORE=; expected seaweedfs or minio\n' >&2; return 2
      fi
      store=seaweedfs ;;
    minio|seaweedfs) ;;
    *) printf 'invalid FLEET_COMMS_MEDIA_STORE=%s; expected seaweedfs or minio\n' "$store" >&2; return 2 ;;
  esac
  [[ -n "$(_comms_env_get "$file" FLEET_COMMS_MEDIA_STORE)" ]] ||
    comms_env_set "$file" FLEET_COMMS_MEDIA_STORE "$store" || return
  case "$store" in minio) endpoint=http://comms-minio:9000 ;; seaweedfs) endpoint=http://comms-seaweedfs:8333 ;; esac
  [[ -n "$(_comms_env_get "$file" FLEET_COMMS_MEDIA_ENDPOINT)" ]] ||
    comms_env_set "$file" FLEET_COMMS_MEDIA_ENDPOINT "$endpoint" || return
  if ! grep -q '^FLEET_COMMS_MEDIA_BUCKET=' "$file"; then
    comms_env_set "$file" FLEET_COMMS_MEDIA_BUCKET comms-journal || return
  fi
}

comms_resolve_profiles() {
  local file="$1" store
  [[ "$(_comms_env_get "$file" FLEET_COMMS_ENABLED)" == true ]] || return 0
  if [[ -z "$(_comms_env_get "$file" FLEET_COMMS_MEDIA_ENDPOINT)" ]]; then
    printf '%s\n' '--profile comms'; return 0
  fi
  store=$(_comms_env_get "$file" FLEET_COMMS_MEDIA_STORE)
  case "$store" in
    seaweedfs) printf '%s\n' '--profile comms --profile comms-media-seaweedfs' ;;
    minio) printf '%s\n' '--profile comms --profile comms-media' ;;
    *) printf 'invalid FLEET_COMMS_MEDIA_STORE=%s; expected seaweedfs or minio\n' "$store" >&2; return 2 ;;
  esac
}
