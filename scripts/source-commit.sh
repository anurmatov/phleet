#!/usr/bin/env bash
# Print the source commit an image built from <repo-dir> may claim, or `unknown` (#476).
#
# The temporal bridge stamps this value (Dockerfile.temporal GIT_COMMIT -> FLEET_BUILD_COMMIT) as
# its Temporal worker build id, and a compiled workflow classifier rule matches a run only when
# every workflow task recorded exactly the rule's reviewed commit. So the commit is printed only
# when it is true of the build context: a git checkout whose HEAD is a 40-hex commit and whose
# tree has no modified, staged or untracked files. Anything else prints `unknown`, says why on
# stderr and still exits 0; the bridge ignores `unknown`, so compiled runs stay source-unverified
# instead of claiming a commit the image was not built from.
#
# Usage: scripts/source-commit.sh [repo-dir]   (default: the repository holding this script)
set -euo pipefail

dir="${1:-$(cd "$(dirname "$0")/.." && pwd)}"

unknown() {
  echo "source commit unknown: $*" >&2
  echo unknown
  exit 0
}

command -v git >/dev/null 2>&1 || unknown "git is not installed"
commit=$(git -C "$dir" rev-parse --verify --quiet 'HEAD^{commit}' 2>/dev/null) \
  || unknown "$dir is not a git checkout with a commit"
[[ "$commit" =~ ^[0-9a-f]{40}$ ]] || unknown "HEAD is not a 40-hex commit"
status=$(git -C "$dir" status --porcelain --untracked-files=normal 2>/dev/null) \
  || unknown "git status failed in $dir"
[[ -z "$status" ]] || unknown "$dir has modified, staged or untracked files"
echo "$commit"
