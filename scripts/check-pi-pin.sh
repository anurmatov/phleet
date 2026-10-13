#!/bin/bash
# Fail when the installed Pi (#386) is not exactly the pinned release (§5, A26).
#
#   scripts/check-pi-pin.sh install <version> <pi-bin> <node_modules-dir>...
#       `pi --version` prints <version>; node is >= 22.19; `pi --help` lists every flag in
#       PI_FLAGS; and every installed @earendil-works/* package under each dir is exactly <version>.
#   scripts/check-pi-pin.sh tree <version> <node_modules-dir>...
#       Only the package walk. CI also runs it against a copy with one package forced to another
#       version, which must fail.
#   scripts/check-pi-pin.sh mcp-sdk <version> <node_modules-dir>
#       Every installed @modelcontextprotocol/sdk under the dir is exactly <version>, and the
#       top-level one exists.
#
# Used by the agent image build (Dockerfile) and by the CI `pi` job. Exit 0 = pinned, 1 = not.
#
# WHY EVERY PACKAGE. Pi declares its own packages as `^<version>` (coding-agent/package.json), so
# a correct `pi --version` says nothing about pi-ai, pi-agent-core or the rest. Only installed
# packages count: a package.json directly under `node_modules/@earendil-works/<name>/`. Pi also
# ships example manifests (examples/plugins/pi-example-plugin is `@earendil-works/pi-example-plugin`
# 1.0.0), which npm never installs and which a name-based search would wrongly report.
#
# Pi writes `~/.pi/agent/auth.json` on `--version` when it has a writable home. Every Pi call here
# runs with a throwaway HOME and agent dir, so the image never ships that file.
set -euo pipefail

PI_FLAGS=(--mode --offline --no-approve --no-mcp --no-prompt-templates)
MIN_NODE_MAJOR=22
MIN_NODE_MINOR=19

usage() {
    sed -n '2,13p' "$0" >&2
    exit 2
}

fail() {
    echo "ERROR: $*" >&2
    exit 1
}

# Prints "<path> <name> <version>" for every installed package matching <scope>/<name-pattern>
# under the dir, one per line. <name-pattern> is an ERE for the directory name.
installed_packages() {
    local dir=$1 scope=$2 name_re=$3 abs
    [ -d "$dir" ] || fail "$dir is not a directory."
    abs=$(cd "$dir" && pwd)
    find "$abs" -path "*/node_modules/${scope}/*/package.json" -type f 2>/dev/null \
        | { grep -E "/node_modules/${scope}/${name_re}/package\.json$" || true; } \
        | sort \
        | while IFS= read -r manifest; do
            # shellcheck disable=SC2016 # a JavaScript template literal, not a shell expansion.
            node -e '
                const m = require(process.argv[1]);
                console.log(`${process.argv[1]} ${m.name ?? "<no name>"} ${m.version ?? "<no version>"}`);
            ' "$manifest"
        done
}

# Every installed package must carry exactly <version> under the name its directory says.
check_tree() {
    local version=$1 scope=$2 name_re=$3 dir=$4 lines path name actual expected_name bad=0 count=0
    lines=$(installed_packages "$dir" "$scope" "$name_re")
    while IFS=' ' read -r path name actual; do
        [ -n "$path" ] || continue
        count=$((count + 1))
        expected_name="${scope}/$(basename "$(dirname "$path")")"
        if [ "$name" != "$expected_name" ]; then
            echo "ERROR: ${path} is installed as ${expected_name} but its package.json names ${name}." >&2
            bad=1
        elif [ "$actual" != "$version" ]; then
            echo "ERROR: ${name} is ${actual}, expected exactly ${version} (${path})." >&2
            bad=1
        else
            echo "ok ${name} ${actual}"
        fi
    done <<< "$lines"
    [ "$bad" -eq 0 ] || fail "${scope} packages under ${dir} are not all pinned to ${version}."
    if [ "$count" -eq 0 ]; then
        echo "no ${scope} package under ${dir}."
    else
        echo "${count} ${scope} package(s) under ${dir}, all ${version}."
    fi
    CHECKED_COUNT=$count
}

check_pi_binary() {
    local version=$1 pi_bin=$2 tmp actual help flag node_version
    [ -x "$pi_bin" ] || fail "Pi binary ${pi_bin} is missing or not executable."

    node_version=$(node -p 'process.versions.node')
    node -e '
        const [major, minor] = process.versions.node.split(".").map(Number);
        const [needMajor, needMinor] = process.argv.slice(1).map(Number);
        process.exit(major > needMajor || (major === needMajor && minor >= needMinor) ? 0 : 1);
    ' "$MIN_NODE_MAJOR" "$MIN_NODE_MINOR" \
        || fail "node ${node_version} is older than ${MIN_NODE_MAJOR}.${MIN_NODE_MINOR}, which Pi ${version} requires."
    echo "ok node ${node_version} (>= ${MIN_NODE_MAJOR}.${MIN_NODE_MINOR})"

    tmp=$(mktemp -d)
    PI_TMP=$tmp
    actual=$(HOME="$tmp" PI_CODING_AGENT_DIR="$tmp/agent" PI_OFFLINE=1 "$pi_bin" --version 2>&1 | tr -d '[:space:]') \
        || fail "'pi --version' failed: ${actual}"
    [ "$actual" = "$version" ] || fail "expected pi ${version}, got '${actual}'."
    echo "ok pi --version ${actual}"

    help=$(HOME="$tmp" PI_CODING_AGENT_DIR="$tmp/agent" PI_OFFLINE=1 "$pi_bin" --help 2>&1) \
        || fail "'pi --help' failed."
    for flag in "${PI_FLAGS[@]}"; do
        grep -qE -- "(^|[[:space:],])${flag}([[:space:],=]|$)" <<< "$help" \
            || fail "'pi --help' does not list ${flag}, which Fleet passes to every Pi process."
        echo "ok pi --help lists ${flag}"
    done
}

[ "$#" -ge 1 ] || usage
mode=$1
shift
CHECKED_COUNT=0
PI_TMP=""
trap 'if [ -n "$PI_TMP" ]; then rm -rf "$PI_TMP"; fi' EXIT

case "$mode" in
    install)
        [ "$#" -ge 3 ] || usage
        version=$1 pi_bin=$2
        shift 2
        check_pi_binary "$version" "$pi_bin"
        total=0
        for dir in "$@"; do
            check_tree "$version" "@earendil-works" "[^/]+" "$dir"
            total=$((total + CHECKED_COUNT))
        done
        [ "$total" -gt 0 ] || fail "no @earendil-works package found under: $*"
        ;;
    tree)
        [ "$#" -ge 2 ] || usage
        version=$1
        shift
        for dir in "$@"; do
            check_tree "$version" "@earendil-works" "[^/]+" "$dir"
        done
        ;;
    mcp-sdk)
        [ "$#" -eq 2 ] || usage
        version=$1 dir=$2
        [ -f "$dir/@modelcontextprotocol/sdk/package.json" ] \
            || fail "$dir/@modelcontextprotocol/sdk is not installed."
        check_tree "$version" "@modelcontextprotocol" "sdk" "$dir"
        ;;
    *)
        usage
        ;;
esac
