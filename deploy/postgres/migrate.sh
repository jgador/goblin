#!/usr/bin/env bash
set +x
set -euo pipefail
goblinctl=${GOBLINCTL:-goblinctl}
goblin_migration_image=${1:?Pass the built Goblin image}
if command -v kubectl >/dev/null 2>&1; then goblin_migration_kubectl=(kubectl); else goblin_migration_kubectl=(k3s kubectl); fi
goblin_migration_job=$("$goblinctl" internal migration-job "$goblin_migration_image"
)
goblin_migration_name=$(printf '%s' "$goblin_migration_job" | "${goblin_migration_kubectl[@]}" create -f - -o name)
if ! "${goblin_migration_kubectl[@]}" wait --for=condition=Complete "$goblin_migration_name" -n goblin --timeout=180s; then
  "${goblin_migration_kubectl[@]}" logs "$goblin_migration_name" -n goblin
  exit 1
fi
"${goblin_migration_kubectl[@]}" logs "$goblin_migration_name" -n goblin
"${goblin_migration_kubectl[@]}" delete "$goblin_migration_name" -n goblin --wait=true
