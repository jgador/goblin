#!/usr/bin/env bash
set +x
set -euo pipefail
goblinctl=${GOBLINCTL:-goblinctl}
goblin_migration_image=${1:?Pass the built Goblin image}
if command -v kubectl >/dev/null 2>&1; then goblin_migration_kubectl=(kubectl); else goblin_migration_kubectl=(k3s kubectl); fi
goblin_migration_name=job/goblin-schema
existing=$("${goblin_migration_kubectl[@]}" get job goblin-schema -n goblin --ignore-not-found -o json)
if [[ -n "$existing" ]]; then
  migration_state=$(printf '%s' "$existing" | "$goblinctl" internal migration-state "$goblin_migration_image")
  if [[ "$migration_state" == failed ]]; then
    # This command was explicitly invoked again. Replace only a confirmed
    # terminal job owned by this CLI; an active/uncertain job is never replaced.
    "${goblin_migration_kubectl[@]}" logs "$goblin_migration_name" -n goblin
    "${goblin_migration_kubectl[@]}" delete "$goblin_migration_name" -n goblin --wait=true
    existing=''
  fi
fi
if [[ -z "$existing" ]]; then
  goblin_migration_job=$("$goblinctl" internal migration-job "$goblin_migration_image")
  goblin_migration_name=$(printf '%s' "$goblin_migration_job" | "${goblin_migration_kubectl[@]}" create -f - -o name)
fi
if ! "${goblin_migration_kubectl[@]}" wait --for=condition=Complete "$goblin_migration_name" -n goblin --timeout=180s; then
  "${goblin_migration_kubectl[@]}" logs "$goblin_migration_name" -n goblin
  exit 1
fi
"${goblin_migration_kubectl[@]}" logs "$goblin_migration_name" -n goblin
"${goblin_migration_kubectl[@]}" delete "$goblin_migration_name" -n goblin --wait=true
