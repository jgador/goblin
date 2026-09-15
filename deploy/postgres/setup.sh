#!/usr/bin/env bash
set +x
set -euo pipefail
cd "$(dirname "$0")/../.."

# Reuse k3s on the VM, or the operator's selected kubectl context elsewhere.
if command -v kubectl >/dev/null 2>&1; then
  goblin_kubectl=(kubectl)
else
  goblin_kubectl=(k3s kubectl)
fi
"${goblin_kubectl[@]}" create namespace goblin --dry-run=client -o json | "${goblin_kubectl[@]}" apply -f -
existing_data=$("${goblin_kubectl[@]}" get pvc goblin-postgres-data -n goblin --ignore-not-found -o name)

for role in admin app; do
  secret="goblin-postgres-$role"
  existing=$("${goblin_kubectl[@]}" get secret "$secret" -n goblin --ignore-not-found -o name)
  if [[ -z "$existing" ]]; then
    if [[ -n "$existing_data" ]]; then
      printf 'Missing %s for an existing PostgreSQL PVC. Restore its credentials before rerunning setup.\n' "$secret" >&2
      exit 1
    fi
    # Never rotate a password just because setup runs again: PostgreSQL only
    # consumes its initialization passwords when the data directory is empty.
    python3 deploy/postgres/write-appsettings.py --create-secret "$role" | \
      "${goblin_kubectl[@]}" create -f -
  fi
done

# Materialize normal application configuration using the database's actual
# credentials. Existing Secrets remain authoritative on subsequent runs.
"${goblin_kubectl[@]}" get secret goblin-postgres-app goblin-postgres-admin -n goblin -o json | \
  python3 deploy/postgres/write-appsettings.py

"${goblin_kubectl[@]}" apply -k deploy/postgres
"${goblin_kubectl[@]}" rollout status statefulset/goblin-postgres -n goblin --timeout=300s
printf 'PostgreSQL is ready. appsettings.json files are configured. Apply backend/database/migrations using the database guide.\n'
