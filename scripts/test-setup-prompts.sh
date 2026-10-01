#!/usr/bin/env bash
# Exercise the real setup helpers with errexit enabled, without running setup.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
DIR=$(mktemp -d)
trap 'rm -rf "$DIR"' EXIT
sed -n '/^read_env_var() {/,/^# Poll a container/p' "$ROOT/setup.sh" > "$DIR/helpers.sh"
cat > "$DIR/run.sh" <<'FIXTURE'
#!/usr/bin/env bash
set -euo pipefail
DRY_RUN=false
YELLOW='' NC=''
fail() { printf '%s\n' "$*"; }
source "$1"
# Direct invocation: a conditional call would suppress errexit in the helper.
prompt_field "$2" SYNTHETIC_FIELD 'Fixture value' '' "$3" "$4" "${5:-}"
echo CONTINUED
FIXTURE

for masked in n y; do
  printf 'SYNTHETIC_FIELD=\n' > "$DIR/env"
  cp "$DIR/env" "$DIR/before"
  printf '\n' | bash "$DIR/run.sh" "$DIR/helpers.sh" "$DIR/env" n "$masked" > "$DIR/output"
  grep -qx CONTINUED "$DIR/output"
  cmp "$DIR/before" "$DIR/env"
  echo "PASS optional empty input continues under set -e without a key (masked=$masked)"
done

printf '\nfixture-required-value\n' | bash "$DIR/run.sh" "$DIR/helpers.sh" "$DIR/env" y n > "$DIR/output"
grep -q 'This field is required.' "$DIR/output"
grep -qx CONTINUED "$DIR/output"
grep -qx 'SYNTHETIC_FIELD=fixture-required-value' "$DIR/env"
echo 'PASS required empty input is rejected before accepting a nonempty value'

: > "$DIR/env"
rc=0
bash "$DIR/run.sh" "$DIR/helpers.sh" "$DIR/env" y n < /dev/null > "$DIR/output" || rc=$?
[[ "$rc" != 0 && ! -s "$DIR/env" ]]
! grep -qx CONTINUED "$DIR/output"
echo 'PASS required EOF fails without continuation or configuration writes'

printf '\n' | bash "$DIR/run.sh" "$DIR/helpers.sh" "$DIR/env" n n fixture-default > "$DIR/output"
grep -qx CONTINUED "$DIR/output"
grep -qx 'SYNTHETIC_FIELD=fixture-default' "$DIR/env"
echo 'PASS optional empty input still writes an explicit default'
