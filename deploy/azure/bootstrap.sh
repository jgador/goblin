#!/usr/bin/env bash
# Azure Custom Script launches scripts through /bin/sh; ensure Bash semantics.
if [ -z "${BASH_VERSION:-}" ]; then
  exec /bin/bash "$0" "$@"
fi
set +x
set -Eeuo pipefail
umask 077

install -d -m 0750 /var/lib/goblin
exec > >(tee -a /var/log/goblin-bootstrap.log) 2>&1
printf 'running\n' > /var/lib/goblin/bootstrap-status
bootstrap_dir=''
current_stage='Preparing installation'
stage() {
  current_stage=$1
  printf '[Goblin] %s\n' "$current_stage"
}
finish() {
  local result=$?
  if [[ "$result" != 0 ]]; then
    printf 'failed\n' > /var/lib/goblin/bootstrap-status
    printf '[Goblin] Installation failed during: %s (exit code %s).\n' "$current_stage" "$result" >&2
    printf '[Goblin] Review the error above in the goblin-bootstrap deployment step.\n' >&2
  fi
  if [[ -n "$bootstrap_dir" ]]; then
    rm -rf "$bootstrap_dir"
  fi
  exit "$result"
}
trap finish EXIT

K3S_VERSION='v1.36.4+k3s1'
K3S_INSTALL_SHA256='46177d4c99440b4c0311b67233823a8e8a2fc09693f6c89af1a7161e152fbfad'
SANDBOX_VERSION='v1.0.2'
SANDBOX_MANIFEST_SHA256='5daf76bba85ba656a8877c9bcce1c9598bd124a61875b59b4256095fdbf1fcdb'

# The extension can run while cloud-init is finishing the base image setup.
# cloud-init exit code 2 reports recoverable warnings, rather than failure.
stage 'Preparing the server'
cloud_init_result=0
cloud-init status --wait || cloud_init_result=$?
if [[ "$cloud_init_result" != 0 && "$cloud_init_result" != 2 ]]; then
  printf 'cloud-init failed with status %s\n' "$cloud_init_result"
  exit "$cloud_init_result"
fi

if ! command -v curl >/dev/null || ! command -v python3 >/dev/null; then
  apt-get -o DPkg::Lock::Timeout=300 update
  DEBIAN_FRONTEND=noninteractive apt-get -o DPkg::Lock::Timeout=300 install -y ca-certificates curl python3
fi

bootstrap_dir=$(mktemp -d /var/lib/goblin/bootstrap.XXXXXX)

stage 'Preparing the Goblin password'
# Bicep embeds the helper and a base64-encoded password in protectedSettings.
# The password travels on stdin, never in a subprocess argument or log message.
cat > "$bootstrap_dir/hash-password.py" <<'PYTHON'
__GOBLIN_PASSWORD_HASHER__
PYTHON
printf '%s' '__GOBLIN_PASSWORD_BASE64__' | base64 --decode | \
  python3 "$bootstrap_dir/hash-password.py" > "$bootstrap_dir/owner-password"

stage 'Downloading Kubernetes'
curl --fail --silent --show-error --location --retry 5 --connect-timeout 15 --max-time 180 \
  "https://raw.githubusercontent.com/k3s-io/k3s/${K3S_VERSION}/install.sh" \
  --output "$bootstrap_dir/install-k3s.sh"
stage 'Verifying the Kubernetes download'
printf '%s  %s\n' "$K3S_INSTALL_SHA256" "$bootstrap_dir/install-k3s.sh" | sha256sum --check

stage 'Installing Kubernetes'
install -d -m 0750 /etc/rancher/k3s
if [[ ! -e /etc/rancher/k3s/config.yaml ]]; then
  cat > /etc/rancher/k3s/config.yaml <<'CONFIG'
write-kubeconfig-mode: "0600"
secrets-encryption: true
CONFIG
fi

INSTALL_K3S_VERSION="$K3S_VERSION" INSTALL_K3S_EXEC=server sh "$bootstrap_dir/install-k3s.sh"

# Wait for the API first: a running systemd service does not imply a ready API.
stage 'Checking Kubernetes readiness'
api_ready=false
for ((attempt = 0; attempt < 120; attempt++)); do
  if k3s kubectl get --raw=/readyz --request-timeout=5s >/dev/null 2>&1; then
    api_ready=true
    break
  fi
  sleep 5
done
if [[ "$api_ready" != true ]]; then
  printf 'Kubernetes API did not become ready. Inspect journalctl -u k3s.\n'
  exit 1
fi
k3s kubectl wait --for=condition=Ready node --all --timeout=300s

stage 'Configuring the Goblin password'
k3s kubectl create namespace goblin-preview --dry-run=client -o json | \
  k3s kubectl apply --server-side --field-manager=goblin-bootstrap -f -
k3s kubectl create secret generic goblin-owner-password -n goblin-preview \
  --from-file="owner-password=$bootstrap_dir/owner-password" --dry-run=client -o json | \
  k3s kubectl apply --server-side --field-manager=goblin-bootstrap -f -
rm -f "$bootstrap_dir/owner-password"

stage 'Downloading Agent Sandbox'
curl --fail --silent --show-error --location --retry 5 --connect-timeout 15 --max-time 180 \
  "https://github.com/kubernetes-sigs/agent-sandbox/releases/download/${SANDBOX_VERSION}/sandbox.yaml" \
  --output "$bootstrap_dir/sandbox.yaml"
stage 'Verifying the Agent Sandbox download'
printf '%s  %s\n' "$SANDBOX_MANIFEST_SHA256" "$bootstrap_dir/sandbox.yaml" | sha256sum --check
stage 'Installing Agent Sandbox'
k3s kubectl apply --server-side --field-manager=goblin-bootstrap -f "$bootstrap_dir/sandbox.yaml"
stage 'Checking Agent Sandbox readiness'
k3s kubectl wait --for=condition=Established crd/sandboxes.agents.x-k8s.io --timeout=120s
k3s kubectl rollout status deployment/agent-sandbox-controller -n agent-sandbox-system --timeout=300s

printf 'ready\n' > /var/lib/goblin/bootstrap-status
printf 'Infrastructure ready: K3s %s and Agent Sandbox %s.\n' "$K3S_VERSION" "$SANDBOX_VERSION"
printf 'Goblin application, HTTPS onboarding and hardened sandbox runtime are not installed.\n'
