#!/usr/bin/env bash
# Disposable real k3s/CRI/Fluent Bit/VictoriaLogs check; never uses the user's context.
set -euo pipefail
cd "$(dirname "$0")/.."
logging_test_dir=$(mktemp -d)
logging_test_node="goblin-logging-test-$$"
logging_test_image="goblin-logging-smoke:test-$$"
logging_forward_pid=
cleanup() {
  if [[ -n "$logging_forward_pid" ]]; then kill "$logging_forward_pid" 2>/dev/null || true; fi
  docker rm -fv "$logging_test_node" >/dev/null 2>&1 || true
  docker image rm "$logging_test_image" >/dev/null 2>&1 || true
  rm -rf "$logging_test_dir"
}
trap cleanup EXIT
kube() { kubectl --kubeconfig "$logging_test_dir/kubeconfig" "$@"; }
# Pull exactly the images selected in the shipping manifests.
mapfile -t logging_images < <(sed -n 's/^[[:space:]]*image: //p' deploy/auth/logging/workloads.yaml)
for image in "${logging_images[@]}"; do docker pull "$image" >/dev/null; done
printf 'Building the disposable .NET logging workload…\n'
dotnet publish backend/tests/Goblin.LoggingSmoke -c Release -o "$logging_test_dir/publish" --nologo > "$logging_test_dir/publish.log"
cat > "$logging_test_dir/publish/Dockerfile" <<'DOCKERFILE'
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble
WORKDIR /app
COPY --chown=1000:1000 . .
USER 1000:1000
ENTRYPOINT ["dotnet", "Goblin.LoggingSmoke.dll"]
DOCKERFILE
docker build -q -t "$logging_test_image" "$logging_test_dir/publish" > /dev/null
printf 'Starting an isolated k3s cluster…\n'
docker run -d --privileged --name "$logging_test_node" -p 127.0.0.1::6443 \
  rancher/k3s:v1.35.1-k3s1 server --disable=traefik --disable=servicelb --disable=metrics-server \
  --write-kubeconfig-mode=600 >/dev/null
for ((attempt=0; attempt<90; attempt++)); do
  if docker exec "$logging_test_node" test -f /etc/rancher/k3s/k3s.yaml; then break; fi
  sleep 2
done
docker exec "$logging_test_node" cat /etc/rancher/k3s/k3s.yaml > "$logging_test_dir/kubeconfig"
chmod 600 "$logging_test_dir/kubeconfig"
logging_api_port=$(docker port "$logging_test_node" 6443/tcp | cut -d: -f2)
sed -i "s/127.0.0.1:6443/127.0.0.1:${logging_api_port}/" "$logging_test_dir/kubeconfig"
for ((attempt=0; attempt<90; attempt++)); do
  if kube get nodes --no-headers 2>/dev/null | rg -q ' Ready '; then break; fi
  sleep 2
done
kube wait --for=condition=Ready nodes --all --timeout=60s
# Save the fixture by tag. Pinned upstream images are fetched by k3s directly.
docker save "$logging_test_image" | docker exec -i "$logging_test_node" ctr -n k8s.io images import - >/dev/null
kube create namespace goblin
kube apply -k deploy/auth/logging
kube rollout status deployment/goblin-victorialogs -n goblin --timeout=180s
kube rollout status daemonset/goblin-fluent-bit -n goblin --timeout=180s
smoke() {
  local name=$1
  cat <<POD | kube apply -f -
apiVersion: v1
kind: Pod
metadata:
  name: $name
  namespace: goblin
  labels:
    app: goblin-auth
spec:
  restartPolicy: Never
  automountServiceAccountToken: false
  containers:
    - name: auth
      image: $logging_test_image
      imagePullPolicy: Never
      args: [$name]
POD
  kube wait "pod/$name" -n goblin --for=jsonpath='{.status.phase}'=Succeeded --timeout=60s
}
forward() {
  if [[ -n "$logging_forward_pid" ]]; then kill "$logging_forward_pid" 2>/dev/null || true; fi
  kube port-forward -n goblin service/goblin-victorialogs :9428 --address 127.0.0.1 > "$logging_test_dir/forward.log" 2>&1 &
  logging_forward_pid=$!
  for ((attempt=0; attempt<40; attempt++)); do
    if rg -q 'Forwarding from' "$logging_test_dir/forward.log"; then break; fi
    sleep 1
  done
  logging_query_port=$(sed -n 's/Forwarding from 127.0.0.1:\([0-9]*\).*/\1/p' "$logging_test_dir/forward.log" | head -1)
  [[ -n "$logging_query_port" ]]
  logging_query_url="http://127.0.0.1:$logging_query_port"
}
check_records() {
  local marker=$1
  for ((attempt=0; attempt<60; attempt++)); do
    curl -fsS --max-time 5 --data-urlencode "query=kubernetes.pod_name:=$marker" --data 'limit=100' \
      "$logging_query_url/logs/select/logsql/query" > "$logging_test_dir/records.jsonl"
    if [[ $(wc -l < "$logging_test_dir/records.jsonl") -ge 5 ]]; then break; fi
    sleep 2
  done
  node --input-type=module - "$logging_test_dir/records.jsonl" "$marker" <<'JS'
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
const raw=readFileSync(process.argv[2],'utf8');
const entries=raw.trim().split('\n').filter(Boolean).map(line=>JSON.parse(line));
assert.equal(entries.length, 5, 'Restarting the collector must not replay already-delivered files in this acknowledged-delivery scenario');
const info=entries.find(row=>row.category==='Goblin.LoggingSmoke' && row.level==='Information');
assert.ok(info, 'Structured .NET event must arrive');
assert.equal(info.category, 'Goblin.LoggingSmoke');
assert.equal(info['state.Marker'], process.argv[3]);
assert.equal(info['state.AttemptId'], '9007199254740993');
assert.equal(JSON.parse(info.scopes)[0].WorkId, '9007199254740993');
assert.equal(info['kubernetes.namespace_name'], 'goblin');
assert.equal(info['kubernetes.container_name'], 'auth');
assert.ok(info['kubernetes.host']);
assert.match(info._time, /Z$/);
assert.ok(entries.some(row=>row.level==='Warning' && row._msg.includes('\nsecond line') && row.exception.includes('Synthetic smoke failure')));
assert.ok(entries.some(row=>row._msg===`Plain console ${process.argv[3]}`));
assert.ok(entries.some(row=>row._msg===`Plain stderr ${process.argv[3]}` && row.stream==='stderr'));
assert.ok(entries.some(row=>row.category==='Microsoft.AspNetCore.Hosting.Diagnostics' && row.level==='Information' && row['state.Marker']===process.argv[3]));
assert.ok(!raw.includes('filtered-smoke-debug-details'));
console.log(`PASS: ${process.argv[3]} — timestamps, messages, levels, fields, 64-bit IDs, scopes, multiline, pod metadata, text fallback`);
JS
}
smoke logging-first
forward
check_records logging-first
printf 'Checking PVC persistence and collector buffering through an outage/restart…\n'
kube scale deployment/goblin-victorialogs -n goblin --replicas=0
kube wait --for=delete pod -l app=goblin-victorialogs -n goblin --timeout=60s
smoke logging-buffered
# Allow discovery and a failed flush into persistent chunks before restarting.
sleep 10
kube rollout restart daemonset/goblin-fluent-bit -n goblin
kube rollout status daemonset/goblin-fluent-bit -n goblin --timeout=90s
kube scale deployment/goblin-victorialogs -n goblin --replicas=1
kube rollout status deployment/goblin-victorialogs -n goblin --timeout=90s
forward
check_records logging-first
check_records logging-buffered
GOBLIN_TEST_VICTORIALOGS_URL="$logging_query_url" GOBLIN_TEST_LOG_MARKER=logging-buffered \
  node dist/scripts/check-logs-ui.js
printf 'PASS: logging pipeline and real VMUI; disposable cluster removed on exit.\n'
