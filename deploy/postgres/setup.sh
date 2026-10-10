#!/usr/bin/env bash
set +x
set -euo pipefail
cd "$(dirname "$0")/../.."
umask 077
goblinctl=${GOBLINCTL:-goblinctl}
progress() {
  if [[ "${GOBLIN_INSTALL_PROGRESS:-false}" == true ]]; then
    "$goblinctl" internal state detail "$1" --step database --path "${GOBLIN_INSTALL_STATE:-/var/lib/goblin/install/status.json}"
  fi
}
if [[ $# != 0 ]]; then
  printf 'Usage: bash deploy/postgres/setup.sh\n' >&2
  exit 1
fi

if command -v kubectl >/dev/null 2>&1; then
  goblin_kubectl=(kubectl)
else
  goblin_kubectl=(k3s kubectl)
fi
# Fail before changing a database if certificate issuance is unavailable.
progress 'Checking certificate manager before PostgreSQL setup'
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

progress 'Issuing and checking PostgreSQL certificates'
"${goblin_kubectl[@]}" apply -f deploy/postgres/ca.yaml
"${goblin_kubectl[@]}" wait --for=condition=Ready certificate/goblin-postgres-ca -n goblin --timeout=180s
"${goblin_kubectl[@]}" apply -f deploy/postgres/certificates.yaml
for certificate in server app admin; do
  "${goblin_kubectl[@]}" wait --for=condition=Ready "certificate/goblin-postgres-$certificate" -n goblin --timeout=180s
done
if [[ -z "$existing_admin" ]]; then
  # The image requires this only during initdb. Client connections never use it.
  "$goblinctl" internal admin-secret | "${goblin_kubectl[@]}" create -f -
fi

progress 'Starting PostgreSQL with its retained data volume'
"${goblin_kubectl[@]}" apply -k deploy/postgres
"${goblin_kubectl[@]}" rollout status statefulset/goblin-postgres -n goblin --timeout=300s
progress 'Verifying PostgreSQL TLS and client certificate authentication'
verification_job=$("${goblin_kubectl[@]}" create -f deploy/postgres/verify.yaml -o name)
if ! "${goblin_kubectl[@]}" wait --for=condition=Complete "$verification_job" -n goblin --timeout=150s; then
  "${goblin_kubectl[@]}" logs "$verification_job" -n goblin --all-containers=true || true
  exit 1
fi
"${goblin_kubectl[@]}" logs "$verification_job" -n goblin
"${goblin_kubectl[@]}" delete "$verification_job" -n goblin --ignore-not-found=true --wait=true
printf 'PostgreSQL certificate authentication is configured. See docs/postgres-workflow.md for client access.\n'
