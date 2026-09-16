#!/usr/bin/env bash
set +x
set -Eeuo pipefail
umask 077

setup_dir=/opt/goblin/setup
install_dir=/var/lib/goblin/install
private_dir="$install_dir/private"
state() { python3 "$setup_dir/goblin-setup.pyz" state "$@"; }
stage() { printf '[Goblin] %s\n' "$1"; state detail "$1"; }
step() { state start "$1"; }
done_step() { state complete; }
download() { curl --fail --silent --show-error --location --retry 5 --connect-timeout 15 --max-time 300 "$1" --output "$2"; }

K3S_VERSION='v1.36.4+k3s1'
K3S_INSTALL_SHA256='46177d4c99440b4c0311b67233823a8e8a2fc09693f6c89af1a7161e152fbfad'
SANDBOX_VERSION='v1.0.2'
SANDBOX_MANIFEST_SHA256='5daf76bba85ba656a8877c9bcce1c9598bd124a61875b59b4256095fdbf1fcdb'
CERT_MANAGER_VERSION='v1.21.2'
CERT_MANAGER_SHA256='e03b668ec8675214af6b0a671699d088f2601fa3878e0dbe1b41d3feafd1879f'
traefik_config=/var/lib/rancher/k3s/server/manifests/goblin-traefik-config.yaml

# Serialize attempts, recovery and bootstrap configuration changes.
exec 9>"$install_dir/installer.lock"
flock -n 9 || exit 0

write_ingress_mode() {
  install -d -m 0755 "$(dirname "$traefik_config")"
  # K3s owns traefik.yaml; a HelmChartConfig is the supported customization point.
  # Rename atomically so the K3s manifest watcher never sees partial YAML.
  cat > "$private_dir/traefik-config.yaml" <<YAML
apiVersion: helm.cattle.io/v1
kind: HelmChartConfig
metadata:
  name: traefik
  namespace: kube-system
spec:
  valuesContent: |-
    service:
      spec:
        type: $1
YAML
  install -m 0600 "$private_dir/traefik-config.yaml" "${traefik_config}.tmp"
  mv "${traefik_config}.tmp" "$traefik_config"
}

wait_ingress_mode() {
  local actual pods attempt
  for ((attempt=0; attempt<60; attempt++)); do
    actual=$(k3s kubectl get service traefik -n kube-system -o jsonpath='{.spec.type}' --request-timeout=5s 2>/dev/null) || actual=''
    if [[ "$actual" == "$1" ]]; then
      if [[ "$1" == LoadBalancer ]]; then return 0; fi
      # ServiceLB uses host ports/iptables. Wait for its pods to disappear
      # before restoring the status UI on the same public port.
      if pods=$(k3s kubectl get pods -n kube-system -o name --request-timeout=5s 2>/dev/null) && \
          ! printf '%s\n' "$pods" | grep -q '^pod/svclb-traefik-'; then return 0; fi
    fi
    sleep 3
  done
  printf 'Traefik did not switch to %s.\n' "$1" >&2
  return 1
}

set_ingress_mode() {
  # Let K3s's manifest watcher be the only writer of this resource. Mixing its
  # updates with server-side apply creates an ownership race during handoff.
  write_ingress_mode "$1" || return 1
  wait_ingress_mode "$1"
}

recover_handoff() {
  # The marker survives power loss and a killed worker. Keep it if rollback
  # fails so the next attempt knows it must restore public access.
  if [[ -e "$private_dir/handoff" ]]; then
    printf '[Goblin] Restoring installation status access.\n'
    if set_ingress_mode ClusterIP; then
      rm -f "$private_dir/handoff"
    else
      printf '[Goblin] Ingress recovery needs attention. Inspect Traefik and ServiceLB from the VM.\n' >&2
    fi
  fi
  systemctl enable --now goblin-setup.service
}

if [[ "${1:-}" == recover ]]; then
  if [[ "${SERVICE_RESULT:-success}" != success ]]; then
    state failed
    recover_handoff
  fi
  exit 0
fi

# A reboot between recording completion and disabling the unit is harmless.
if python3 - "$install_dir/status.json" <<'PYTHON'
import json, sys
sys.exit(0 if json.load(open(sys.argv[1]))['status'] == 'ready' else 1)
PYTHON
then
  systemctl disable goblin-setup.service goblin-installer.service
  systemctl stop goblin-setup.service
  exit 0
fi

bootstrap_dir="$private_dir/work"
finish() {
  local result=$?
  trap - EXIT TERM INT
  if [[ "$result" != 0 ]]; then
    printf '[Goblin] Installation failed (exit code %s). See the preceding local diagnostics.\n' "$result" >&2
    state failed || true
    recover_handoff || true
  fi
  exit "$result"
}
trap finish EXIT
trap 'exit 143' TERM
trap 'exit 130' INT

state begin
step prepare
goblin_hostname=$(cat "$private_dir/hostname")
goblin_source_ref=$(cat "$private_dir/source-ref")
if [[ "$goblin_hostname" != localhost && ! "$goblin_hostname" =~ ^([a-z0-9]([a-z0-9-]*[a-z0-9])?\.)+[a-z]{2,}$ ]] || \
   [[ ! "$goblin_source_ref" =~ ^[A-Za-z0-9][A-Za-z0-9._/-]{0,127}$ ]]; then
  printf 'Invalid public hostname or Goblin source ref.\n' >&2
  exit 1
fi
# Local tests forward a browser port to the installation's HTTP port 80.
# Azure keeps its default origin; a root-owned systemd override supplies the
# browser origin when testing behind such a forwarder.
goblin_origin="${GOBLIN_PUBLIC_ORIGIN:-http://${goblin_hostname}}"
goblin_authority=$(python3 - "$goblin_origin" "$goblin_hostname" <<'PYTHON'
import sys
from urllib.parse import urlsplit
try:
    origin = urlsplit(sys.argv[1])
    if (origin.scheme != 'http' or origin.hostname != sys.argv[2] or origin.username or origin.password
            or sys.argv[1] != 'http://' + origin.netloc
            or origin.path or origin.query or origin.fragment or (origin.port is not None and origin.port < 1)
            or origin.netloc != sys.argv[2] + ((':' + str(origin.port)) if origin.port is not None else '')):
        raise ValueError()
except ValueError:
    sys.exit('Invalid public origin. Use the configured HTTP hostname and an optional port.')
print(origin.netloc)
PYTHON
)
printf '%s\n' "$goblin_origin" > /var/lib/goblin/public-url
state public-url "$goblin_origin"
install -d -m 0700 "$bootstrap_dir"
write_ingress_mode ClusterIP
done_step

step k3s
stage 'Installing Kubernetes'
if [[ ! -f /etc/systemd/system/k3s.service ]]; then
  download "https://raw.githubusercontent.com/k3s-io/k3s/${K3S_VERSION}/install.sh" "$bootstrap_dir/install-k3s.sh"
  printf '%s  %s\n' "$K3S_INSTALL_SHA256" "$bootstrap_dir/install-k3s.sh" | sha256sum --check
  install -d -m 0750 /etc/rancher/k3s
  if [[ ! -e /etc/rancher/k3s/config.yaml ]]; then
    cat > /etc/rancher/k3s/config.yaml <<'CONFIG'
write-kubeconfig-mode: "0600"
secrets-encryption: true
CONFIG
  fi
  INSTALL_K3S_VERSION="$K3S_VERSION" INSTALL_K3S_EXEC=server sh "$bootstrap_dir/install-k3s.sh"
else
  systemctl start k3s
fi
stage 'Checking Kubernetes API readiness'
api_ready=false
for ((attempt=0; attempt<120; attempt++)); do
  if k3s kubectl get --raw=/readyz --request-timeout=5s >/dev/null 2>&1; then api_ready=true; break; fi
  sleep 5
done
if [[ "$api_ready" != true ]]; then printf 'Kubernetes API did not become ready. Inspect journalctl -u k3s.\n' >&2; exit 1; fi
stage 'Waiting for Kubernetes node registration'
node_registered=false
for ((attempt=0; attempt<60; attempt++)); do
  if node_names=$(k3s kubectl get nodes -o name --request-timeout=5s 2>/dev/null) && [[ -n "$node_names" ]]; then node_registered=true; break; fi
  sleep 5
done
if [[ "$node_registered" != true ]]; then printf 'Kubernetes node did not register. Inspect journalctl -u k3s.\n' >&2; exit 1; fi
stage 'Checking Kubernetes node readiness'
k3s kubectl wait --for=condition=Ready node --all --timeout=300s
stage 'Preparing internal application routing'
touch "$private_dir/handoff"
set_ingress_mode ClusterIP
rm -f "$private_dir/handoff"
systemctl enable --now goblin-setup.service
done_step

step cert-manager
stage 'Installing certificate manager'
download "https://github.com/cert-manager/cert-manager/releases/download/${CERT_MANAGER_VERSION}/cert-manager.yaml" "$bootstrap_dir/cert-manager.yaml"
printf '%s  %s\n' "$CERT_MANAGER_SHA256" "$bootstrap_dir/cert-manager.yaml" | sha256sum --check
k3s kubectl apply --server-side --field-manager=goblin-installer -f "$bootstrap_dir/cert-manager.yaml"
for crd in certificates.cert-manager.io certificaterequests.cert-manager.io issuers.cert-manager.io clusterissuers.cert-manager.io orders.acme.cert-manager.io challenges.acme.cert-manager.io; do
  k3s kubectl wait --for=condition=Established "crd/$crd" --timeout=120s
done
for deployment in cert-manager cert-manager-cainjector cert-manager-webhook; do
  k3s kubectl rollout status "deployment/$deployment" -n cert-manager --timeout=300s
done
# Exercise admission with a server-side dry-run. No Issuer, Certificate, CA or
# database Secret is persisted. Rollout readiness alone does not verify webhook TLS.
cat > "$bootstrap_dir/cert-manager-probe.yaml" <<'YAML'
apiVersion: cert-manager.io/v1
kind: Certificate
metadata:
  name: goblin-installation-probe
  namespace: cert-manager
spec:
  secretName: goblin-installation-probe
  dnsNames: [installation-probe.invalid]
  issuerRef:
    name: nonexistent-installation-probe
YAML
webhook_ready=false
for ((attempt=0; attempt<60; attempt++)); do
  if k3s kubectl create --dry-run=server -f "$bootstrap_dir/cert-manager-probe.yaml" >/dev/null 2>&1; then webhook_ready=true; break; fi
  sleep 3
done
if [[ "$webhook_ready" != true ]]; then printf 'Certificate manager webhook did not become ready.\n' >&2; exit 1; fi
done_step

step sandbox
stage 'Configuring the Goblin password'
k3s kubectl create namespace goblin --dry-run=client -o json | k3s kubectl apply --server-side --field-manager=goblin-bootstrap -f -
# Reuse the persisted verifier on retries; no fresh password or database credentials.
k3s kubectl create secret generic goblin-owner-password -n goblin \
  --from-file="owner-password=$private_dir/owner-password" --dry-run=client -o json | \
  k3s kubectl apply --server-side --field-manager=goblin-bootstrap -f -
stage 'Installing Agent Sandbox'
download "https://github.com/kubernetes-sigs/agent-sandbox/releases/download/${SANDBOX_VERSION}/sandbox.yaml" "$bootstrap_dir/sandbox.yaml"
printf '%s  %s\n' "$SANDBOX_MANIFEST_SHA256" "$bootstrap_dir/sandbox.yaml" | sha256sum --check
k3s kubectl apply --server-side --field-manager=goblin-bootstrap -f "$bootstrap_dir/sandbox.yaml"
k3s kubectl wait --for=condition=Established crd/sandboxes.agents.x-k8s.io --timeout=120s
k3s kubectl rollout status deployment/agent-sandbox-controller -n agent-sandbox-system --timeout=300s
done_step

# shellcheck source=deploy/azure/install-app.sh
source "$setup_dir/install-app.sh"

step activate
stage 'Activating public access'
touch "$private_dir/handoff"
state handoff
systemctl stop goblin-setup.service
set_ingress_mode LoadBalancer
goblin_node_ip=$(k3s kubectl get nodes -o json | python3 -c '
import ipaddress, json, sys
addresses = json.load(sys.stdin)["items"][0]["status"]["addresses"]
address = next((item["address"] for item in addresses if item["type"] == "InternalIP" and ipaddress.ip_address(item["address"]).version == 4), None)
if not address:
    sys.exit("The Kubernetes node has no IPv4 InternalIP for the Azure public route.")
print(address)
')
check_app "$goblin_node_ip"
# Also require the assigned public hostname to resolve and serve the application.
check_app ''
done_step
# Clean only transient downloads; retain the bundle, verifier, state and log.
rm -rf "$bootstrap_dir"
rm -f "$private_dir/handoff"
state ready
systemctl disable goblin-setup.service goblin-installer.service || printf '[Goblin] Could not disable setup units; the completion guard will stop them on next start.\n' >&2
printf 'Goblin ready: %s\n' "$goblin_origin"
