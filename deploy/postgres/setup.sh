#!/usr/bin/env bash
set +x
set -euo pipefail
cd "$(dirname "$0")/../.."
umask 077
port=5432
if [[ $# == 2 && "$1" == --port && "$2" =~ ^[0-9]+$ && "$2" -ge 1 && "$2" -le 65535 ]]; then
  port=$2
elif [[ $# != 0 ]]; then
  printf 'Usage: bash deploy/postgres/setup.sh [--port LOCAL_DATABASE_PORT]\n' >&2
  exit 1
fi

if command -v kubectl >/dev/null 2>&1; then
  goblin_kubectl=(kubectl)
else
  goblin_kubectl=(k3s kubectl)
fi
# Only enable host access automatically when this checkout owns the local runner
# and the selected kubectl context refers to that same cluster.
local_port=''
if [[ -f deploy/local/install.py && -f /var/lib/goblin/local-test/config.json ]]; then
  local_port=$(python3 - <<'PYTHON'
import json
from pathlib import Path
config = json.loads(Path('/var/lib/goblin/local-test/config.json').read_text())
if config.get('mode') == 'direct' and config.get('repo') == str(Path.cwd()):
    print(config.get('postgres_port', 55432))
PYTHON
  )
  if [[ -n "$local_port" ]]; then
    if selected_uid=$("${goblin_kubectl[@]}" get namespace kube-system -o 'jsonpath={.metadata.uid}' --request-timeout=10s) && \
       local_uid=$(k3s kubectl --kubeconfig=/etc/rancher/k3s/k3s.yaml get namespace kube-system -o 'jsonpath={.metadata.uid}' --request-timeout=10s) && \
       [[ -n "$selected_uid" && "$selected_uid" == "$local_uid" ]]; then
      if [[ $# == 0 ]]; then port=$local_port; fi
    else
      local_port=''
    fi
  fi
fi
# Fail before changing a database if certificate issuance is unavailable.
for deployment in cert-manager cert-manager-cainjector cert-manager-webhook; do
  "${goblin_kubectl[@]}" rollout status "deployment/$deployment" -n cert-manager --timeout=120s
done
"${goblin_kubectl[@]}" create namespace goblin --dry-run=client -o json | "${goblin_kubectl[@]}" apply -f -
existing_data=$("${goblin_kubectl[@]}" get pvc goblin-postgres-data -n goblin --ignore-not-found -o name)
existing_ca=$("${goblin_kubectl[@]}" get secret goblin-postgres-ca -n goblin --ignore-not-found -o name)
existing_tls=$("${goblin_kubectl[@]}" get secret goblin-postgres-tls goblin-postgres-app-tls goblin-postgres-admin-tls -n goblin --ignore-not-found -o name)
existing_admin=$("${goblin_kubectl[@]}" get secret goblin-postgres-admin -n goblin --ignore-not-found -o name)
if [[ -z "$existing_ca" && -n "$existing_tls" ]]; then
  printf 'The PostgreSQL CA Secret is missing. Restore goblin-postgres-ca before rerunning setup; refusing to replace the trust root.\n' >&2
  exit 1
fi
if [[ -z "$existing_admin" && -n "$existing_data" ]]; then
  printf 'Missing goblin-postgres-admin for an existing PostgreSQL PVC. Restore its initialization Secret before rerunning setup.\n' >&2
  exit 1
fi

"${goblin_kubectl[@]}" apply -f deploy/postgres/ca.yaml
"${goblin_kubectl[@]}" wait --for=condition=Ready certificate/goblin-postgres-ca -n goblin --timeout=180s
"${goblin_kubectl[@]}" apply -f deploy/postgres/certificates.yaml
for certificate in server app admin; do
  "${goblin_kubectl[@]}" wait --for=condition=Ready "certificate/goblin-postgres-$certificate" -n goblin --timeout=180s
done
if [[ -z "$existing_admin" ]]; then
  # The image requires this only during initdb. Client connections never use it.
  python3 deploy/postgres/write-appsettings.py --create-secret admin | "${goblin_kubectl[@]}" create -f -
fi

"${goblin_kubectl[@]}" apply -k deploy/postgres
"${goblin_kubectl[@]}" rollout status statefulset/goblin-postgres -n goblin --timeout=300s
verification_job=$("${goblin_kubectl[@]}" create -f deploy/postgres/verify.yaml -o name)
if ! "${goblin_kubectl[@]}" wait --for=condition=Complete "$verification_job" -n goblin --timeout=150s; then
  "${goblin_kubectl[@]}" logs "$verification_job" -n goblin --all-containers=true || true
  exit 1
fi
"${goblin_kubectl[@]}" logs "$verification_job" -n goblin
"${goblin_kubectl[@]}" delete "$verification_job" -n goblin --ignore-not-found=true --wait=true
"${goblin_kubectl[@]}" get secret goblin-postgres-app-tls goblin-postgres-admin-tls -n goblin -o json | \
  python3 deploy/postgres/write-appsettings.py --port "$port"

# Patch just the database configuration of an existing Goblin deployment, without
# replacing its image, owner password, origin, storage, or other custom settings.
sandbox=$("${goblin_kubectl[@]}" get sandbox goblin-auth -n goblin --ignore-not-found -o name)
if [[ -n "$sandbox" ]]; then
  patch=$(mktemp)
  trap 'rm -f "$patch"' EXIT
  "${goblin_kubectl[@]}" get sandbox goblin-auth -n goblin -o json | python3 deploy/postgres/configure-app.py > "$patch"
  if [[ -s "$patch" ]]; then
    "${goblin_kubectl[@]}" patch sandbox goblin-auth -n goblin --type=merge --patch-file "$patch"
    "${goblin_kubectl[@]}" delete pod -n goblin -l app=goblin-auth --ignore-not-found=true --wait=true
    "${goblin_kubectl[@]}" wait --for=condition=Ready sandbox/goblin-auth -n goblin --timeout=300s
  fi
fi
if [[ -n "$local_port" ]]; then
  python3 deploy/local/install.py database --port "$port"
else
  printf 'Tooling expects a localhost:%s tunnel to PostgreSQL.\n' "$port"
fi
printf 'PostgreSQL certificate authentication is configured. Apply SQL migrations using docs/database.md.\n'
