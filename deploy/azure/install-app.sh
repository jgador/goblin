# Sourced by bootstrap.sh after Kubernetes and Agent Sandbox are ready.
# Values from ARM are encoded before embedding to avoid shell interpolation.
goblin_hostname=$(printf '%s' '__GOBLIN_HOSTNAME_BASE64__' | base64 --decode)
goblin_source_ref=$(printf '%s' '__GOBLIN_SOURCE_REF_BASE64__' | base64 --decode)
if [[ ! "$goblin_hostname" =~ ^([a-z0-9]([a-z0-9-]*[a-z0-9])?\.)+[a-z]{2,}$ ]] || \
   [[ ! "$goblin_source_ref" =~ ^[A-Za-z0-9][A-Za-z0-9._/-]{0,127}$ ]]; then
  printf 'Invalid public hostname or Goblin source ref.\n' >&2
  exit 1
fi

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
curl --fail --silent --show-error --location --retry 5 --connect-timeout 15 --max-time 300 \
  "https://codeload.github.com/jgador/goblin/tar.gz/${goblin_source_ref}" \
  --output "$bootstrap_dir/goblin-source.tar.gz"
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

stage 'Configuring the Goblin hostname'
# Keep a rendered overlay on the VM for inspection and later HTTPS setup.
install -d -m 0750 /var/lib/goblin/deploy/auth /var/lib/goblin/deploy/azure/app
cp "$goblin_source_dir/deploy/auth/"{sandbox,kustomization}.yaml /var/lib/goblin/deploy/auth/
cp "$goblin_source_dir/deploy/azure/app/"{ingress,kustomization}.yaml /var/lib/goblin/deploy/azure/app/
python3 - "$goblin_hostname" "$goblin_image" <<'PYTHON'
from pathlib import Path
import sys

for path in Path('/var/lib/goblin/deploy/azure/app').glob('*.yaml'):
    path.write_text(path.read_text().replace('__GOBLIN_PUBLIC_HOSTNAME__', sys.argv[1]).replace('__GOBLIN_IMAGE__', sys.argv[2]))
PYTHON
k3s kubectl apply -k /var/lib/goblin/deploy/azure/app
# Sandbox recreates the pod from its template; replace it to load image/origin
# changes and a new password verifier on reprovisioning. The PVC is retained.
k3s kubectl delete pod -n goblin -l app=goblin-auth --ignore-not-found=true --wait=true

stage 'Checking Goblin readiness'
k3s kubectl wait --for=condition=Ready sandbox/goblin-auth -n goblin --timeout=300s
goblin_node_ip=$(k3s kubectl get nodes -o 'jsonpath={.items[0].status.addresses[?(@.type=="InternalIP")].address}')
# Traefik is installed asynchronously by K3s. Verify the complete local ingress
# path with the assigned Host header, without depending on public DNS propagation.
goblin_ready=false
for ((attempt = 0; attempt < 60; attempt++)); do
  if curl --fail --silent --show-error --noproxy '*' --connect-timeout 5 --max-time 10 \
      --resolve "${goblin_hostname}:80:${goblin_node_ip}" "http://${goblin_hostname}/" \
      --output "$bootstrap_dir/goblin-page.html" && \
     grep -q '<title>.*Goblin</title>' "$bootstrap_dir/goblin-page.html"; then
    goblin_ready=true
    break
  fi
  sleep 5
done
if [[ "$goblin_ready" != true ]]; then
  printf 'Goblin did not become available through Traefik. Inspect the goblin pod, ingress, and kube-system Traefik pods.\n' >&2
  exit 1
fi
printf '%s\n' "$goblin_source_sha" > /var/lib/goblin/application-source-sha256
printf 'http://%s\n' "$goblin_hostname" > /var/lib/goblin/public-url
