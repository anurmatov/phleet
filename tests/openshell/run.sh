#!/usr/bin/env bash
# Source tests are not agent-path, gateway, native CLI or rollback qualification.
set -euo pipefail
cd "$(dirname "$0")/../.."
case "${1:-unit}" in
  unit)
    dotnet test tests/Fleet.Orchestrator.Tests --filter \
      'FullyQualifiedName~OpenShell&FullyQualifiedName!~.Migrations.'
    ;;
  config)
    python3 tests/openshell/config-test.py
    ;;
  memory-identity)
    dotnet test tests/Fleet.Memory.Tests --filter 'FullyQualifiedName~MemoryIdentityTests'
    ;;
  memory-acl)
    dotnet test tests/Fleet.Memory.Tests --filter 'FullyQualifiedName~VerifiedAclTests'
    ;;
  migration)
    if [ -z "${FLEET_ORCHESTRATOR_MIGRATION_CONNECTION:-}" ]; then
      echo 'BLOCKED: approved throwaway MySQL target is absent; migration acceptance UNRUN' >&2
      exit 3
    fi
    dotnet test tests/Fleet.Orchestrator.Tests --filter \
      'FullyQualifiedName~OpenShellExecutionMigrationTests'
    ;;
  *) echo 'INVALID: available source suites: unit, config, memory-acl, memory-identity, migration' >&2; exit 2 ;;
esac
