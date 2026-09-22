# Cluster logs

Kubernetes installations run a private, single-node VictoriaLogs store and a
Fluent Bit DaemonSet. Fluent Bit tails CRI container logs on each node, adds
Kubernetes pod metadata, and sends newline-delimited JSON to VictoriaLogs.
These components are installed with the Goblin application overlay in both
local and Azure installations. They do not run with a standalone `npm start`.

VictoriaLogs stores data on its own 5 Gi persistent volume and retains logs for
14 days. The persistent volume must be backed up separately if logs are needed
for audit or disaster recovery. Log retention does not limit disk usage within
the retention window; monitor volume capacity and increase the claim before it
fills. Fluent Bit buffers unsent records on an ephemeral 1 Gi volume, so a
collector pod restart or a prolonged outage can lose buffered records. The
collector retries transient HTTP failures but logging is **not** a durable Work
history or an exactly-once audit trail.

The VictoriaLogs Service has no Ingress, NodePort, or LoadBalancer. A
NetworkPolicy allows ingestion only from Fluent Bit pods. Logs can contain
credentials or other sensitive application output, so do not publish the
service or expose query endpoints through the browser. A cluster administrator
can inspect it with a temporary port-forward:

```bash
kubectl -n goblin port-forward service/goblin-victorialogs 9428:9428
curl -G 'http://127.0.0.1:9428/select/logsql/query' \
  --data-urlencode 'query=error' --data-urlencode 'limit=20'
```

For K3s, replace `kubectl` with `sudo k3s kubectl`. Check deployment health with
`kubectl -n goblin rollout status statefulset/goblin-victorialogs` and
`kubectl -n goblin rollout status daemonset/goblin-fluent-bit`. Inspect the
collector's own pod logs if ingestion stops. Pod metadata is enriched via a
read-only service account; the collector has no permission to read Secrets.
