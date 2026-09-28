# Functions sourced by the native installer worker.
# shellcheck shell=bash
# shellcheck disable=SC2154
prepare_source() {
stage 'Downloading Goblin'
goblin_source_dir="$bootstrap_dir/source"
mkdir -p "$goblin_source_dir"
# Keep the first complete archive for this attempt across retries, even when
# the selected branch moves. Explicit reprovisioning starts a new download.
if [[ ! -f "$bootstrap_dir/goblin-source.tar.gz" ]]; then
  download "https://codeload.github.com/jgador/goblin/tar.gz/${goblin_source_ref}" "$bootstrap_dir/goblin-source.tar.gz.part"
  mv "$bootstrap_dir/goblin-source.tar.gz.part" "$bootstrap_dir/goblin-source.tar.gz"
fi
goblin_source_sha=$(sha256sum "$bootstrap_dir/goblin-source.tar.gz" | cut -d ' ' -f 1)
goblin_image="localhost/goblin-auth:${goblin_source_sha}"
# Public source files need their normal read/execute permissions when copied into
# the non-root image. Keep bootstrap's private umask for everything outside this extraction.
(
  umask 022
  tar --extract --gzip --file "$bootstrap_dir/goblin-source.tar.gz" \
    --directory "$goblin_source_dir" --strip-components=1 --no-same-owner --no-same-permissions
)

"$GOBLINCTL" validate-install --request "$goblin_source_dir/deploy/install-request.json" --source "$goblin_source_dir"
"$GOBLINCTL" internal prefetch-images --source "$goblin_source_dir" > "$bootstrap_dir/prefetch-images"
}

configure_docker() {
# This installer builds public images on the local Linux daemon. Do not inherit
# an interactive user's registry logins, Desktop helpers, contexts or builders.
export DOCKER_CONFIG="$bootstrap_dir/docker-config"
install -d -m 0700 "$DOCKER_CONFIG"
# An explicit anonymous Hub entry also prevents Docker from auto-detecting a
# host credential store when loading an otherwise empty configuration.
printf '{"auths":{"https://index.docker.io/v1/":{}}}\n' > "$DOCKER_CONFIG/config.json"
chmod 0600 "$DOCKER_CONFIG/config.json"
unset DOCKER_CONTEXT DOCKER_TLS DOCKER_TLS_VERIFY DOCKER_CERT_PATH BUILDX_CONFIG BUILDX_BUILDER
export DOCKER_HOST=unix:///var/run/docker.sock
}

image_task() {
stage 'Installing Docker'
if ! docker --version >/dev/null 2>&1 || ! dockerd --version >/dev/null 2>&1; then
  # Configure this before package installation can start Docker. Preserve any
  # existing settings, and let K3s keep forwarding traffic between its pods.
  install -d -m 0755 /etc/docker
  "$GOBLINCTL" internal docker-config
  apt-get -o DPkg::Lock::Timeout=300 update
  DEBIAN_FRONTEND=noninteractive apt-get -o DPkg::Lock::Timeout=300 install -y docker.io docker-buildx
fi
if ! docker buildx version >/dev/null 2>&1; then
  apt-get -o DPkg::Lock::Timeout=300 update
  DEBIAN_FRONTEND=noninteractive apt-get -o DPkg::Lock::Timeout=300 install -y docker-buildx
fi
stage 'Starting Docker'
systemctl enable --now docker
docker info >/dev/null

stage 'Checking retained Goblin build'
if docker image inspect "$goblin_image" >/dev/null 2>&1; then
  stage 'Reusing the completed Goblin image for this source snapshot'
else
  stage 'Building Goblin'
  DOCKER_BUILDKIT=1 docker build --load --network=host --platform linux/amd64 --progress=plain --tag "$goblin_image" "$goblin_source_dir" 2>&1 | "$GOBLINCTL" internal build-progress
fi
}

import_task() {
stage 'Importing Goblin into Kubernetes'
if k3s crictl inspecti "$goblin_image" >/dev/null 2>&1; then
  stage 'The Goblin image is already available in Kubernetes'
else
  docker save --output "$bootstrap_dir/goblin-image.tar.part" "$goblin_image"
  mv "$bootstrap_dir/goblin-image.tar.part" "$bootstrap_dir/goblin-image.tar"
  k3s ctr --namespace k8s.io images import "$bootstrap_dir/goblin-image.tar"
fi
}

database_task() {
stage 'Configuring durable Work storage'
GOBLIN_POSTGRES_CONFIGURE_APP=false GOBLIN_INSTALL_PROGRESS=true GOBLIN_INSTALL_STATE="$install_dir/status.json" "$GOBLINCTL" --repo /var/lib/goblin/config db setup
}

migrate_task() {
stage 'Checking and applying database migrations'
"$GOBLINCTL" db migrate "$goblin_image"
}

deploy_task() {
stage 'Configuring the Goblin hostname'
# Keep a rendered overlay on the VM for inspection and later HTTPS setup.
"$GOBLINCTL" internal prepare-install --request "$goblin_source_dir/deploy/install-request.json" --source "$goblin_source_dir" --destination /var/lib/goblin/deploy
"$GOBLINCTL" internal render-overlay /var/lib/goblin/deploy/azure/app "$goblin_hostname" "$goblin_image" "$goblin_origin"
k3s kubectl apply -k /var/lib/goblin/deploy/azure/app
# Sandbox recreates the pod from its template; replace it to load image/origin
# changes and a new password verifier on reprovisioning. The PVC is retained.
k3s kubectl delete pod -n goblin -l app=goblin-auth --ignore-not-found=true --wait=true
}

verify_task() {
stage 'Checking Goblin readiness'
k3s kubectl wait --for=condition=Ready sandbox/app -n goblin --timeout=300s
k3s kubectl rollout status deployment/goblin-headlamp -n goblin --timeout=300s
k3s kubectl rollout status deployment/goblin-victorialogs -n goblin --timeout=300s
k3s kubectl rollout status daemonset/goblin-fluent-bit -n goblin --timeout=300s
k3s kubectl rollout status deployment/traefik -n kube-system --timeout=300s
# Verify through the internal Traefik service while setup still owns port 80.
goblin_ingress_ip=$(k3s kubectl get service traefik -n kube-system -o jsonpath='{.spec.clusterIP}')
check_app "$goblin_ingress_ip"
printf '%s\n' "$goblin_source_sha" > /var/lib/goblin/application-source-sha256
}

check_app() {
  local address=$1 attempt url="$goblin_origin"
  local -a route=(--header "Host: ${goblin_authority}")
  if [[ -n "$address" ]]; then
    route+=(--resolve "${goblin_hostname}:80:${address}")
    url="http://${goblin_hostname}"
  fi
  for ((attempt=0; attempt<60; attempt++)); do
    if curl --fail --silent --show-error --noproxy '*' --connect-timeout 5 --max-time 10 \
        "${route[@]}" "${url}/readyz" --output "$bootstrap_dir/goblin-ready.json" && \
       "$GOBLINCTL" internal json-test "$bootstrap_dir/goblin-ready.json" ready true
    then
      if curl --fail --silent --show-error --noproxy '*' --connect-timeout 5 --max-time 10 \
          "${route[@]}" "${url}/" --output "$bootstrap_dir/goblin-page.html" && \
          grep -q '<title>.*Goblin</title>' "$bootstrap_dir/goblin-page.html"; then return 0; fi
    fi
    sleep 5
  done
  printf 'Goblin did not become available through Traefik. Inspect the pod, ingress, DNS and Traefik service.\n' >&2
  return 1
}
