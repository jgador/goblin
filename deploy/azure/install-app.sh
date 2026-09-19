# Sourced by the background installer after Kubernetes and cluster add-ons.
# shellcheck shell=bash
# The worker supplies configuration and the private working directory.
# shellcheck disable=SC2154
step image
stage 'Installing Docker'
if ! docker --version >/dev/null 2>&1 || ! dockerd --version >/dev/null 2>&1; then
  # Configure this before package installation can start Docker. Preserve any
  # existing settings, and let K3s keep forwarding traffic between its pods.
  install -d -m 0755 /etc/docker
  python3 - <<'PYTHON'
import json
from pathlib import Path

path = Path('/etc/docker/daemon.json')
config = json.loads(path.read_text()) if path.exists() else {}
config['ip-forward-no-drop'] = True
path.write_text(json.dumps(config, indent=2) + '\n')
PYTHON
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

stage 'Building Goblin'
DOCKER_BUILDKIT=1 docker build --load --network=host --platform linux/amd64 --tag "$goblin_image" "$goblin_source_dir"
stage 'Importing Goblin into Kubernetes'
docker save --output "$bootstrap_dir/goblin-image.tar" "$goblin_image"
k3s ctr --namespace k8s.io images import "$bootstrap_dir/goblin-image.tar"
stage 'Configuring durable Work storage'
GOBLIN_POSTGRES_CONFIGURE_APP=false bash "$goblin_source_dir/deploy/postgres/setup.sh"
bash "$goblin_source_dir/deploy/postgres/migrate.sh" "$goblin_image"

done_step
step deploy
stage 'Configuring the Goblin hostname'
# Keep a rendered overlay on the VM for inspection and later HTTPS setup.
install -d -m 0750 /var/lib/goblin/deploy/auth /var/lib/goblin/deploy/azure/app
cp "$goblin_source_dir/deploy/auth/"{sandbox,kustomization,execution}.yaml /var/lib/goblin/deploy/auth/
cp "$goblin_source_dir/deploy/azure/app/"{ingress,kustomization}.yaml /var/lib/goblin/deploy/azure/app/
python3 - "$goblin_hostname" "$goblin_image" "$goblin_origin" <<'PYTHON'
from pathlib import Path
import sys

for path in Path('/var/lib/goblin/deploy/azure/app').glob('*.yaml'):
    path.write_text(path.read_text().replace('__GOBLIN_PUBLIC_HOSTNAME__', sys.argv[1])
                    .replace('__GOBLIN_IMAGE__', sys.argv[2]).replace('http://' + sys.argv[1], sys.argv[3]))
PYTHON
k3s kubectl apply -k /var/lib/goblin/deploy/azure/app
# Sandbox recreates the pod from its template; replace it to load image/origin
# changes and a new password verifier on reprovisioning. The PVC is retained.
k3s kubectl delete pod -n goblin -l app=goblin-auth --ignore-not-found=true --wait=true

done_step
step verify
stage 'Checking Goblin readiness'
k3s kubectl wait --for=condition=Ready sandbox/goblin-auth -n goblin --timeout=300s
k3s kubectl rollout status deployment/traefik -n kube-system --timeout=300s
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
       python3 - "$bootstrap_dir/goblin-ready.json" <<'PYTHON'
import json, sys
try:
    sys.exit(0 if json.load(open(sys.argv[1])).get('ready') is True else 1)
except (ValueError, AttributeError):
    sys.exit(1)
PYTHON
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
# Verify through the internal Traefik service while setup still owns port 80.
goblin_ingress_ip=$(k3s kubectl get service traefik -n kube-system -o jsonpath='{.spec.clusterIP}')
check_app "$goblin_ingress_ip"
printf '%s\n' "$goblin_source_sha" > /var/lib/goblin/application-source-sha256
done_step
