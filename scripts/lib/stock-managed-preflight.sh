#!/usr/bin/env bash
# #492: sourced by setup.sh and upgrade.sh. Only when FLEET_STOCK_MANAGED_ENABLED=true, refuse an
# install whose managed-runtime trust is incomplete: HTTPS broker management with the operator CA,
# resolvable broker credential refs, the stock CLI's mTLS client cert/key and python3 sqlite3.
# Default-off installs return 0 without any check. Usage: stock_managed_preflight <env-file> <client-dir>
_stock_env_get() {
  [[ -f "$1" ]] || return 0
  local line
  while IFS= read -r line || [[ -n "$line" ]]; do
    case "$line" in "$2="*) printf '%s' "${line#*=}"; return 0 ;; esac
  done < "$1"
}

stock_managed_preflight() {
  local env_file="$1" client_dir="$2" faults=() url ref key value mtls found=false
  [[ "$(_stock_env_get "$env_file" FLEET_STOCK_MANAGED_ENABLED)" == true ]] || return 0
  url=$(_stock_env_get "$env_file" FLEET_STOCK_BROKER_MANAGEMENT_URL)
  [[ "$url" =~ ^https://[^/[:space:]]+ ]] \
    || faults+=("FLEET_STOCK_BROKER_MANAGEMENT_URL must be an https:// broker management endpoint")
  [[ -f "$client_dir/broker-ca.pem" && -r "$client_dir/broker-ca.pem" && -s "$client_dir/broker-ca.pem" ]] \
    || faults+=("operator broker CA $client_dir/broker-ca.pem is missing, empty or unreadable")
  # The *_REF values name other .env keys; each must name a present, non-empty key.
  for ref in FLEET_STOCK_BROKER_ADMIN_USER_REF FLEET_STOCK_BROKER_ADMIN_PASSWORD_REF; do
    key=$(_stock_env_get "$env_file" "$ref")
    value=""
    [[ "$key" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]] && value=$(_stock_env_get "$env_file" "$key")
    [[ -n "$value" ]] || faults+=("$ref must name a .env key that holds the broker admin credential")
  done
  # Stock CLI client trust under XDG_CONFIG_HOME=<client-dir>: openshell/gateways/<name>/mtls/.
  for mtls in "$client_dir"/openshell/gateways/*/mtls; do
    [[ -f "$mtls/tls.crt" && -r "$mtls/tls.crt" && -s "$mtls/tls.crt" \
      && -f "$mtls/tls.key" && -r "$mtls/tls.key" && -s "$mtls/tls.key" ]] && found=true
  done
  $found || faults+=("stock CLI mTLS client cert/key (openshell/gateways/<name>/mtls/tls.crt and tls.key) not readable under $client_dir")
  python3 -c 'import sqlite3' >/dev/null 2>&1 \
    || faults+=("python3 with the sqlite3 module is required (gateway backup)")
  ((${#faults[@]} == 0)) && return 0
  printf 'managed runtime preflight: %s\n' "${faults[@]}" >&2
  return 1
}

# #492 §16: only when FLEET_STOCK_MANAGED_ENABLED=true (upgrade.sh), refuse while the stock gateway
# backup is missing, failed or stale: FLEET_STOCK_BACKUP_DIR/last-success must be newer than
# last-failure and younger than FLEET_STOCK_BACKUP_MAX_AGE_HOURS (default 26).
# Usage: stock_backup_freshness <env-file> <repo-dir>
stock_backup_freshness() {
  local env_file="$1" repo_dir="$2" dir out
  [[ "$(_stock_env_get "$env_file" FLEET_STOCK_MANAGED_ENABLED)" == true ]] || return 0
  dir=$(_stock_env_get "$env_file" FLEET_STOCK_BACKUP_DIR)
  if [[ -z "$dir" ]]; then
    echo "managed runtime preflight: FLEET_STOCK_BACKUP_DIR must name the scripts/backup-stock-gateway.sh backup dir" >&2
    return 1
  fi
  if out=$(bash "$repo_dir/scripts/check-stock-gateway-backup.sh" "$dir" \
      "$(_stock_env_get "$env_file" FLEET_STOCK_BACKUP_MAX_AGE_HOURS)" 2>&1); then
    printf '%s\n' "$out"
    return 0
  fi
  printf 'managed runtime preflight: gateway backup %s\n' "$out" >&2
  return 1
}
