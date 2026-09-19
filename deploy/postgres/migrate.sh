#!/usr/bin/env bash
set +x
set -euo pipefail
goblin_migration_image=${1:?Pass the built Goblin image}
if command -v kubectl >/dev/null 2>&1; then goblin_migration_kubectl=(kubectl); else goblin_migration_kubectl=(k3s kubectl); fi
goblin_migration_job=$(python3 - "$goblin_migration_image" <<'PY'
import json,sys
print(json.dumps({"apiVersion":"batch/v1","kind":"Job","metadata":{"generateName":"goblin-schema-","namespace":"goblin"},"spec":{"backoffLimit":0,"template":{"metadata":{"labels":{"goblin-database-access":"true"}},"spec":{"automountServiceAccountToken":False,"restartPolicy":"Never","securityContext":{"runAsNonRoot":True,"runAsUser":1000,"runAsGroup":1000,"fsGroup":1000},"containers":[{"name":"migrate","image":sys.argv[1],"imagePullPolicy":"IfNotPresent","command":["dotnet","/tools/database/Goblin.Database.dll","apply","/migrations"],"env":[{"name":"ConnectionStrings__GoblinAdmin","value":"Host=goblin-postgres;Database=goblin;Username=goblin_admin;SSL Mode=VerifyFull;Root Certificate=/etc/postgres/ca.crt;SSL Certificate=/etc/postgres/tls.crt;SSL Key=/etc/postgres/tls.key;GSS Encryption Mode=Disable"}],"securityContext":{"allowPrivilegeEscalation":False,"readOnlyRootFilesystem":True,"capabilities":{"drop":["ALL"]}},"volumeMounts":[{"name":"admin","mountPath":"/etc/postgres","readOnly":True},{"name":"tmp","mountPath":"/tmp"}]}],"volumes":[{"name":"admin","secret":{"secretName":"goblin-postgres-admin-tls","defaultMode":288}},{"name":"tmp","emptyDir":{}}]}}}}))
PY
)
goblin_migration_name=$(printf '%s' "$goblin_migration_job" | "${goblin_migration_kubectl[@]}" create -f - -o name)
if ! "${goblin_migration_kubectl[@]}" wait --for=condition=Complete "$goblin_migration_name" -n goblin --timeout=180s; then
  "${goblin_migration_kubectl[@]}" logs "$goblin_migration_name" -n goblin
  exit 1
fi
"${goblin_migration_kubectl[@]}" logs "$goblin_migration_name" -n goblin
"${goblin_migration_kubectl[@]}" delete "$goblin_migration_name" -n goblin --wait=true
