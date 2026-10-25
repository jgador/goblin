#!/usr/bin/env bash
set +x
set -Eeuo pipefail
umask 077

setup_dir=/opt/goblin/setup
install_dir=/var/lib/goblin/install
private_dir="$install_dir/private"
export GOBLINCTL=/opt/goblin/bin/goblinctl
# Installation and recovery target this host's k3s cluster, including embedded
# database helpers that may invoke a separately installed kubectl.
export KUBECONFIG=/etc/rancher/k3s/k3s.yaml
state() { "$GOBLINCTL" internal state "$@"; }
stage() { printf '[Goblin] %s\n' "$1"; state detail "$1" --step "${current_step:-prepare}"; }
step() { current_step=$1; state start "$1"; }
done_step() { state complete "$current_step"; }
megabytes() { printf '%d.%d' $(($1 / 1048576)) $(($1 % 1048576 * 10 / 1048576)); }

# Downloads $1 to $2 and publishes only complete files. A transfer fails only when
# it stalls (under 1 KiB/s for two minutes), then resumes from its partial file.
# Permanent HTTP errors stop immediately; other failures have bounded attempts.
download() {
  local url=$1 destination=$2 attempt code result=0
  for ((attempt=1; attempt<=6; attempt++)); do
    result=0
    code=$(curl --fail --silent --show-error --location --connect-timeout 15 \
      --speed-limit 1024 --speed-time 120 --continue-at - --write-out '%{http_code}' \
      "$url" --output "$destination.part") || result=$?
    if ((result == 0)); then
      mv "$destination.part" "$destination"
      return 0
    fi
    # 33: the server cannot resume this file, so restart it from the beginning.
    if ((result == 33)); then rm -f "$destination.part"; fi
    if ((result == 22)) && [[ "$code" =~ ^4[0-9][0-9]$ && "$code" != 408 && "$code" != 429 ]]; then break; fi
    printf '[Goblin] Download attempt %s of 6 stopped (curl exit %s); resuming.\n' "$attempt" "$result" >&2
    if ((attempt < 6)); then sleep $((attempt * 5)); fi
  done
  rm -f "$destination.part"
  return "$result"
}

# Verified release downloads live outside k3s and Goblin state, so retries and a
# local reset reuse them. Every use checks the pinned checksum again.
download_cache=/var/cache/goblin/downloads
cached_download() {
  local url=$1 path="$download_cache/$2" checksum=$3
  install -d -m 0755 "$download_cache"
  if [[ -f "$path" ]] && printf '%s  %s\n' "$checksum" "$path" | sha256sum --check --status; then return 0; fi
  rm -f "$path"
  download "$url" "$path"
  if ! printf '%s  %s\n' "$checksum" "$path" | sha256sum --check --status; then
    rm -f "$path"
    printf 'The download of %s did not match its pinned checksum.\n' "$2" >&2
    return 1
  fi
}

# Replaces the step detail with combined size and rate every five seconds. These
# updates do not add log entries. Arguments: label, then pairs of file and size.
report_download() {
  local label=$1 previous=-1 current idle=0 total=0 index rate
  local -a files
  shift
  files=("$@")
  for ((index=1; index<${#files[@]}; index+=2)); do total=$((total + files[index])); done
  while :; do
    current=0
    for ((index=0; index<${#files[@]}; index+=2)); do
      if [[ -f "${files[index]}" ]]; then current=$((current + $(stat -c %s "${files[index]}")))
      elif [[ -f "${files[index]}.part" ]]; then current=$((current + $(stat -c %s "${files[index]}.part")))
      fi
    done
    if ((current != previous)); then
      rate=''
      if ((previous >= 0)); then rate=", $(((current - previous) / 5120)) KB/s"; fi
      state progress "Downloading $label: $(megabytes "$current") of $(megabytes "$total") MB$rate" --step "$current_step" || true
      previous=$current idle=0
    elif ((++idle == 3)); then
      state progress "Downloading $label: $(megabytes "$current") of $(megabytes "$total") MB, waiting for data" --step "$current_step" || true
    fi
    sleep 5
  done
}

# Pulls images into k3s with up to $1 parallel workers. Containerd cancels a pull
# that stops receiving data; the next attempt resumes its partial layers. The
# outer limit only ends a pull that hangs without containerd noticing.
pull_images() {
  local workers=$1 worker finished result failure=0 index image attempt
  shift
  local -a images=("$@") pids=()
  for ((worker=0; worker<workers && worker<${#images[@]}; worker++)); do
    (
      for ((index=worker; index<${#images[@]}; index+=workers)); do
        image=${images[$index]}
        if k3s crictl inspecti "$image" >/dev/null 2>&1; then
          stage "Image $((index + 1))/${#images[@]} is already cached: ${image%%@*}"
          continue
        fi
        for ((attempt=1; ; attempt++)); do
          stage "Pulling image $((index + 1))/${#images[@]}: ${image%%@*}"
          if timeout --foreground --kill-after=5s 7200s k3s crictl pull "$image"; then break; fi
          if ((attempt == 3)); then exit 1; fi
          stage "Image $((index + 1))/${#images[@]} stopped downloading; resuming: ${image%%@*}"
          sleep 10
        done
        stage "Downloaded image $((index + 1))/${#images[@]}: ${image%%@*}"
      done
    ) &
    pids+=("$!")
  done
  # A failed image does not interrupt the other downloads; report it afterward.
  while ((${#pids[@]})); do
    result=0
    wait_for_worker "${pids[@]}" || result=$?
    if ((result != 0 && failure == 0)); then failure=$result; fi
    local -a remaining=()
    for worker in "${pids[@]}"; do [[ "$worker" == "$finished" ]] || remaining+=("$worker"); done
    pids=("${remaining[@]}")
  done
  return "$failure"
}

K3S_VERSION='v1.36.5+k3s1'
K3S_INSTALL_SHA256='46177d4c99440b4c0311b67233823a8e8a2fc09693f6c89af1a7161e152fbfad'
# Release assets for linux/amd64. The airgap archive holds k3s's own system images
# (CoreDNS, Helm jobs, pause; bundled Traefik is disabled), so the cluster starts without pulling them.
K3S_SHA256='d73847bcd3c5fccef0115b372e2f9a91f3032dc84bbf71518a4617565294d313'
K3S_SIZE=79077538
K3S_IMAGES_SHA256='7d939471907df2d4e537a9c308bf94173e453fff4a2e9e77e9ae92a64be3007f'
K3S_IMAGES_SIZE=194444189
# Core only: Goblin uses Sandbox directly, without the optional extensions.
SANDBOX_VERSION='v1.0.4'
SANDBOX_MANIFEST_SHA256='c4f6344b6f04e450b61c433ce6275f2a45bc30f746b55ce47ade454ffa2a1aa7'
CERT_MANAGER_VERSION='v1.21.3'
CERT_MANAGER_SHA256='c04a51295adda2b1a775146751f973e787190778c7f35180861d9678876e5c74'
bootstrap_dir="$private_dir/work"
goblin_source_dir="$bootstrap_dir/source"
# shellcheck source=deploy/azure/setup/nginx.sh
source "$setup_dir/nginx.sh"
# shellcheck source=deploy/azure/setup/https.sh
source "$setup_dir/https.sh"

recover_handoff() {
  # The marker survives power loss and a killed worker. Keep it if rollback
  # fails so the next attempt knows it must restore public access.
  if [[ -e "$private_dir/handoff" ]]; then
    printf '[Goblin] Restoring installation status access.\n'
    if set_ingress_mode ClusterIP; then
      if [[ "$(cat "$private_dir/hostname")" != localhost ]]; then
        rm -f /etc/systemd/system/goblin-setup.service.d/listener.conf
      fi
      systemctl daemon-reload || return 1
      systemctl restart goblin-setup.service || return 1
      rm -f "$private_dir/handoff"
    else
      printf '[Goblin] Ingress recovery needs attention. Inspect NGINX and ServiceLB from the VM.\n' >&2
    fi
  fi
  systemctl enable --now goblin-setup.service
}

k3s_task() {
local release k3s_name images_name reporter result=0 binary_pid images_pid
stage 'Installing Kubernetes'
if [[ ! -f /etc/systemd/system/k3s.service ]]; then
  download "https://raw.githubusercontent.com/k3s-io/k3s/${K3S_VERSION}/install.sh" "$bootstrap_dir/install-k3s.sh"
  printf '%s  %s\n' "$K3S_INSTALL_SHA256" "$bootstrap_dir/install-k3s.sh" | sha256sum --check
  # The upstream script downloads silently with no resume. Fetch the pinned binary
  # and system images here, then let it install the service without downloading.
  release="https://github.com/k3s-io/k3s/releases/download/${K3S_VERSION/+/%2B}"
  k3s_name="k3s-${K3S_VERSION}"
  images_name="k3s-airgap-images-amd64-${K3S_VERSION}.tar.zst"
  report_download 'Kubernetes' "$download_cache/$k3s_name" "$K3S_SIZE" \
    "$download_cache/$images_name" "$K3S_IMAGES_SIZE" &
  reporter=$!
  cached_download "$release/k3s" "$k3s_name" "$K3S_SHA256" &
  binary_pid=$!
  cached_download "$release/k3s-airgap-images-amd64.tar.zst" "$images_name" "$K3S_IMAGES_SHA256" &
  images_pid=$!
  wait "$binary_pid" || result=$?
  wait "$images_pid" || result=$?
  kill "$reporter" 2>/dev/null || true
  wait "$reporter" 2>/dev/null || true
  if ((result != 0)); then return "$result"; fi
  find "$download_cache" -maxdepth 1 -type f -name 'k3s-*' ! -name "$k3s_name" ! -name "$images_name" -delete
  stage 'Kubernetes downloads verified'
  install -m 0755 "$download_cache/$k3s_name" /usr/local/bin/k3s
  install -d -m 0755 /var/lib/rancher/k3s/agent/images
  # K3s imports this archive at startup. A hard link avoids a second copy.
  ln -f "$download_cache/$images_name" /var/lib/rancher/k3s/agent/images/k3s-airgap-images-amd64.tar.zst 2>/dev/null || \
    install -m 0644 "$download_cache/$images_name" /var/lib/rancher/k3s/agent/images/k3s-airgap-images-amd64.tar.zst
  install -d -m 0750 /etc/rancher/k3s
  if [[ ! -e /etc/rancher/k3s/config.yaml ]]; then
    cat > /etc/rancher/k3s/config.yaml <<'CONFIG'
write-kubeconfig-mode: "0600"
secrets-encryption: true
disable:
  - traefik
CONFIG
  fi
  INSTALL_K3S_SKIP_DOWNLOAD=true INSTALL_K3S_VERSION="$K3S_VERSION" INSTALL_K3S_EXEC=server sh "$bootstrap_dir/install-k3s.sh"
else
  if [[ "$(k3s --version)" != "k3s version ${K3S_VERSION} "* ]] || \
     ! grep -qx '  - traefik' /etc/rancher/k3s/config.yaml; then
    printf 'This k3s installation uses a superseded configuration. Recreate the disposable installation with the current installer.\n' >&2
    return 1
  fi
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

}

cert_manager_task() {
# Pull before applying manifests, so the rollout waits below cover startup only.
stage 'Downloading certificate manager images'
pull_images 4 \
  "quay.io/jetstack/cert-manager-cainjector:${CERT_MANAGER_VERSION}" \
  "quay.io/jetstack/cert-manager-controller:${CERT_MANAGER_VERSION}" \
  "quay.io/jetstack/cert-manager-webhook:${CERT_MANAGER_VERSION}" \
  "quay.io/jetstack/cert-manager-acmesolver:${CERT_MANAGER_VERSION}"
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
}

sandbox_task() {
stage 'Configuring the Goblin password'
k3s kubectl create namespace goblin --dry-run=client -o json | k3s kubectl apply --server-side --field-manager=goblin-bootstrap -f -
# Reuse the persisted verifier on retries; no fresh password or database credentials.
k3s kubectl create secret generic goblin-owner-password -n goblin \
  --from-file="owner-password=$private_dir/owner-password" --dry-run=client -o json | \
  k3s kubectl apply --server-side --field-manager=goblin-bootstrap -f -
stage 'Downloading the Agent Sandbox controller image'
pull_images 1 "registry.k8s.io/agent-sandbox/agent-sandbox-controller:${SANDBOX_VERSION}"
stage 'Installing Agent Sandbox'
download "https://github.com/kubernetes-sigs/agent-sandbox/releases/download/${SANDBOX_VERSION}/sandbox.yaml" "$bootstrap_dir/sandbox.yaml"
printf '%s  %s\n' "$SANDBOX_MANIFEST_SHA256" "$bootstrap_dir/sandbox.yaml" | sha256sum --check
# Keep upstream image paths, CRD names and RBAC names intact. Kustomize moves
# namespaced resources and service-account references, and shortens the Deployment.
cp "$setup_dir/sandbox-kustomization.yaml" "$bootstrap_dir/kustomization.yaml"
k3s kubectl apply --server-side --field-manager=goblin-bootstrap -k "$bootstrap_dir"
k3s kubectl wait --for=condition=Established crd/sandboxes.agents.x-k8s.io --timeout=120s
k3s kubectl rollout status deployment/sandbox -n sandbox --timeout=300s
}

# shellcheck source=deploy/azure/install-platform.sh
source "$setup_dir/install-platform.sh"

wait_for_worker() {
  local pid result=0
  (($#)) || return 127
  # wait -n ignores children Bash has already reaped. Explicit wait retrieves
  # their saved exit status, including a failure from a very fast cached task.
  for pid in "$@"; do
    if ! kill -0 "$pid" 2>/dev/null; then
      finished=$pid
      wait "$pid" || result=$?
      return "$result"
    fi
  done
  wait -n -p finished "$@" 2>/dev/null || result=$?
  if [[ -z "${finished:-}" ]]; then
    # All remaining children may finish between the check and wait -n.
    finished=$1
    result=0
    wait "$finished" || result=$?
  fi
  return "$result"
}

# Runs after k3s, alongside the add-ons. PostgreSQL and the deployed services need
# these images; downloading them first keeps later rollout waits short.
prefetch_task() {
  local -a images
  mapfile -t images < "$bootstrap_dir/prefetch-images"
  stage "Downloading ${#images[@]} service images with ${GOBLIN_IMAGE_PULL_WORKERS:-4} workers"
  pull_images "${GOBLIN_IMAGE_PULL_WORKERS:-4}" "${images[@]}"
}

# Each worker has its own process group. Its installer-lock descriptor stays
# inherited until it exits; recovery never races a background build or pull.
if [[ "${1:-}" == worker ]]; then
  current_step=${2:?Missing worker step}
  case "$current_step" in
    k3s|nginx|cert-manager|sandbox|image|prefetch|database|import|migrate|deploy|verify) ;;
    *) exit 2 ;;
  esac
  # Called by EXIT/signal traps, including errors inside a pipeline.
  # shellcheck disable=SC2317
  worker_exit() {
    local result=$?
    trap - EXIT TERM INT
    if ((result != 0)); then
      # Persist the cause before the supervisor can cancel this process group.
      state fail-step "$current_step" || true
      touch "$bootstrap_dir/pipeline.failed"
    fi
    exit "$result"
  }
  trap worker_exit EXIT
  trap 'exit 143' TERM
  trap 'exit 130' INT
  step "$current_step"
  "${current_step//-/_}_task"
  done_step
  exit 0
fi

# Serialize attempts, recovery and bootstrap configuration changes.
exec 9>"$install_dir/installer.lock"
flock -n 9 || exit 0

if [[ "${1:-}" == recover ]]; then
  if [[ "${SERVICE_RESULT:-success}" != success ]]; then
    state failed
    systemctl disable goblin-installer.service || true
    recover_handoff
  fi
  exit 0
fi
if "$GOBLINCTL" internal json-test "$install_dir/status.json" status ready; then
  systemctl disable goblin-setup.service goblin-installer.service
  systemctl stop goblin-setup.service
  exit 0
fi

declare -A worker_steps=() completed=()
stop_workers() {
  local pid attempt alive
  for pid in "${!worker_steps[@]}"; do kill -TERM -- "-$pid" 2>/dev/null || true; done
  for ((attempt=0; attempt<50; attempt++)); do
    alive=false
    for pid in "${!worker_steps[@]}"; do
      if kill -0 -- "-$pid" 2>/dev/null; then alive=true; fi
    done
    if [[ "$alive" == false ]]; then break; fi
    sleep 0.1
  done
  for pid in "${!worker_steps[@]}"; do
    kill -KILL -- "-$pid" 2>/dev/null || true
    wait "$pid" 2>/dev/null || true
  done
}
finish() {
  local result=$?
  trap - EXIT TERM INT
  if ((result != 0)); then
    stop_workers
    printf '[Goblin] Installation failed (exit code %s). See the preceding local diagnostics.\n' "$result" >&2
    state failed || true
    systemctl disable goblin-installer.service || true
    recover_handoff || true
  fi
  exit "$result"
}
trap finish EXIT
trap 'exit 143' TERM
trap 'exit 130' INT

run_pipeline() {
  local task dependency ready pid finished result failure=0
  local -a tasks=(k3s nginx image prefetch cert-manager sandbox database import migrate deploy verify)
  local -A started=() dependencies=(
    [nginx]='k3s' [prefetch]='k3s' [cert-manager]='k3s' [sandbox]='k3s'
    [database]='cert-manager sandbox prefetch' [import]='k3s image'
    [migrate]='database import' [deploy]='migrate prefetch'
    [verify]='deploy nginx'
  )
  while :; do
    # A failed worker marks the pipeline before the supervisor reaps it.
    if ((failure == 0)) && [[ -e "$bootstrap_dir/pipeline.failed" ]]; then failure=1; fi
    if ((failure == 0)); then
      for task in "${tasks[@]}"; do
        [[ -z "${started[$task]:-}" ]] || continue
        ready=true
        for dependency in ${dependencies[$task]:-}; do
          [[ "${completed[$dependency]:-}" == true ]] || ready=false
        done
        [[ "$ready" == true ]] || continue
        setsid "$setup_dir/installer.sh" worker "$task" &
        pid=$!
        worker_steps[$pid]=$task
        started[$task]=true
      done
    fi
    if ((${#worker_steps[@]} == 0)); then
      if ((failure == 0 && ${#completed[@]} == ${#tasks[@]})); then return 0; fi
      return $((failure ? failure : 1))
    fi
    result=0
    wait_for_worker "${!worker_steps[@]}" || result=$?
    if ((result == 0)); then
      completed[${worker_steps[$finished]}]=true
    else
      # Clean up anything the failed worker left in its process group.
      kill -TERM -- "-$finished" 2>/dev/null || true
      if ((failure == 0)); then failure=$result; fi
    fi
    unset 'worker_steps[$finished]'
    # Running tasks finish, so their downloads and builds are kept for a retry;
    # nothing new starts after a failure.
    if ((failure != 0 && ${#worker_steps[@]} > 0)) && [[ ! -e "$bootstrap_dir/pipeline.draining" ]]; then
      touch "$bootstrap_dir/pipeline.draining"
      state detail "Finishing ${#worker_steps[@]} running tasks before stopping. No new tasks will start." || true
    fi
  done
}

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
if [[ "$goblin_hostname" == localhost ]]; then default_origin="http://${goblin_hostname}"; else default_origin="https://${goblin_hostname}"; fi
goblin_origin="${GOBLIN_PUBLIC_ORIGIN:-$default_origin}"
goblin_authority=$("$GOBLINCTL" internal origin "$goblin_origin" "$goblin_hostname"
)
printf '%s\n' "$goblin_origin" > /var/lib/goblin/public-url
state public-url "$goblin_origin"
install -d -m 0700 "$bootstrap_dir"
if [[ ! "${GOBLIN_IMAGE_PULL_WORKERS:-4}" =~ ^[1-8]$ ]]; then
  printf 'GOBLIN_IMAGE_PULL_WORKERS must be between 1 and 8.\n' >&2
  exit 1
fi
rm -f "$bootstrap_dir/pipeline.failed" "$bootstrap_dir/pipeline.draining"
prepare_source
configure_docker
export goblin_hostname goblin_origin goblin_authority goblin_source_sha goblin_image
# All workers use the same retained source and isolated Docker configuration.
done_step
run_pipeline

step https
https_task
done_step

step activate
stage 'Activating public access'
touch "$private_dir/handoff"
state handoff
if [[ "$goblin_origin" == https://* ]]; then
  ingress_backend platform 8787
  k3s kubectl annotate ingress goblin -n goblin nginx.org/ssl-redirect=true --overwrite
else
  systemctl stop goblin-setup.service
  set_ingress_mode LoadBalancer
fi
goblin_node_ip=$(k3s kubectl get nodes -o json | "$GOBLINCTL" internal node-address)
check_app "$goblin_node_ip"
# Also require the assigned public hostname to resolve and serve the application.
check_app ''
if [[ "$goblin_origin" == https://* ]]; then
  stage 'Checking the embedded sign-in server and MCP discovery'
  curl --fail --silent --show-error --noproxy '*' --connect-timeout 5 --max-time 15 \
    "$goblin_origin/.well-known/oauth-authorization-server" --output "$bootstrap_dir/oauth-discovery.json"
  "$GOBLINCTL" internal json-test "$bootstrap_dir/oauth-discovery.json" issuer "$goblin_origin/"
  curl --fail --silent --show-error --noproxy '*' --connect-timeout 5 --max-time 15 \
    "$goblin_origin/.well-known/oauth-protected-resource/mcp" --output "$bootstrap_dir/mcp-discovery.json"
  "$GOBLINCTL" internal json-test "$bootstrap_dir/mcp-discovery.json" resource "$goblin_origin/mcp"
  k3s kubectl delete service/goblin-installation endpointslice/goblin-installation -n goblin --ignore-not-found=true
fi
done_step
systemctl stop goblin-setup.service
# Clean only transient downloads; retain the native CLI, verifier, state and log.
rm -rf "$bootstrap_dir"
rm -f "$private_dir/handoff"
state ready
systemctl disable goblin-setup.service goblin-installer.service || printf '[Goblin] Could not disable setup units; the completion guard will stop them on next start.\n' >&2
printf 'Goblin ready: %s\n' "$goblin_origin"
