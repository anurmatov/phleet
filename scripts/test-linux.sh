#!/usr/bin/env bash
# Runs `dotnet test` in a Linux container, for the tests a Mac cannot run natively.
#
# A few tests are skipped off Linux because the code under test is Linux-only (the memory_stats
# directory scan) or needs a curve Apple's cryptography does not have (two push-key tests). CI runs
# them; this runs them from a Mac before pushing, and is also how to loop a timing-sensitive test on
# the platform CI uses.
#
#   scripts/test-linux.sh                                  # the solution, with CI's filter
#   scripts/test-linux.sh tests/Fleet.Memory.Tests         # one project
#   scripts/test-linux.sh tests/Fleet.Agent.Tests --filter "FullyQualifiedName~JournalReplyCapture"
#   FLEET_TEST_CPUS=1 scripts/test-linux.sh tests/Fleet.Comms.Tests   # starve it, to surface a race
#
# The container gets a copy of the working tree (tracked files plus untracked, unignored ones), so
# nothing it builds lands in your bin/ and obj/. Needs Docker. Tests that need MySQL or an object
# store are not started here; CI's filter leaves them out and so does the default below.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
IMAGE="${FLEET_TEST_SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}"
CI_FILTER='FullyQualifiedName!~Fleet.Conversations.Tests&FullyQualifiedName!~Fleet.Orchestrator.Tests.Migrations'

target="Fleet.sln"
if [ $# -gt 0 ] && [ "${1#-}" = "$1" ]; then target="$1"; shift; fi
if [ $# -eq 0 ] && [ "$target" = "Fleet.sln" ]; then set -- --filter "$CI_FILTER"; fi

command -v docker >/dev/null || { echo "test-linux: docker is not installed" >&2; exit 2; }

cpus=()
if [ -n "${FLEET_TEST_CPUS:-}" ]; then cpus=(--cpus "$FLEET_TEST_CPUS"); fi

cd "$ROOT"
# -e: a tracked file deleted in the working tree is listed by git and must not reach tar.
git ls-files -co --exclude-standard -z \
  | perl -0 -ne 'chomp; print "$_\0" if -e $_' \
  | COPYFILE_DISABLE=1 tar --null -T - -cf - \
  | docker run --rm -i ${cpus[@]+"${cpus[@]}"} -v fleet-test-nuget:/root/.nuget/packages "$IMAGE" \
      bash -c 'set -e; mkdir /work; tar -xf - -C /work; cd /work; exec dotnet test "$@"' \
      test-linux "$target" -c Release --nologo "$@"
