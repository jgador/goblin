# Public HTTPS handoff. The status listener remains read-only throughout issuance.
# shellcheck shell=bash
# shellcheck disable=SC2154
setup_listener() {
  install -d -m 0755 /etc/systemd/system/goblin-setup.service.d
  cat > /etc/systemd/system/goblin-setup.service.d/listener.conf <<UNIT
[Service]
ExecStart=
ExecStart=/opt/goblin/bin/goblinctl internal serve --host $1 --port $2 --health-socket /run/goblin-setup/health.sock
UNIT
  systemctl daemon-reload
  systemctl restart goblin-setup.service
}

ingress_backend() {
  local patch
  patch=$(k3s kubectl get ingress goblin -n goblin -o json | "$GOBLINCTL" internal ingress-backend "$1" "$2")
  k3s kubectl patch ingress goblin -n goblin --type=json -p "$patch"
}

https_task() {
  if [[ "$goblin_origin" == http://* ]]; then
    stage 'Local loopback HTTP does not require a public certificate'
    return 0
  fi
  stage 'Preparing public certificate validation and installation progress'
  goblin_node_ip=$(k3s kubectl get nodes -o json | "$GOBLINCTL" internal node-address)
  # Bind the progress listener only to the VM's private IPv4 address. Azure opens
  # no inbound rule for this port. The public route is NGINX on ports 80/443.
  cat > "$bootstrap_dir/progress-route.yaml" <<YAML
apiVersion: v1
kind: Service
metadata:
  name: goblin-installation
  namespace: goblin
spec:
  ports:
    - name: http
      port: 8787
      targetPort: 18080
---
apiVersion: discovery.k8s.io/v1
kind: EndpointSlice
metadata:
  name: goblin-installation
  namespace: goblin
  labels:
    kubernetes.io/service-name: goblin-installation
    endpointslice.kubernetes.io/managed-by: goblin-installer
addressType: IPv4
ports:
  - name: http
    protocol: TCP
    port: 18080
endpoints:
  - addresses: ["${goblin_node_ip}"]
    conditions:
      ready: true
YAML
  k3s kubectl apply -f "$bootstrap_dir/progress-route.yaml"
  touch "$private_dir/handoff"
  ingress_backend goblin-installation 8787
  # Moving setup off public port 80 happens before ServiceLB claims it.
  setup_listener "$goblin_node_ip" 18080
  set_ingress_mode LoadBalancer
  stage 'Checking the assigned hostname before requesting a certificate'
  check_progress http

  certificate_email=$(cat "$private_dir/certificate-email")
  cat > "$bootstrap_dir/public-issuer.yaml" <<YAML
apiVersion: cert-manager.io/v1
kind: ClusterIssuer
metadata:
  name: goblin-letsencrypt
spec:
  acme:
    email: "${certificate_email}"
    server: https://acme-v02.api.letsencrypt.org/directory
    privateKeySecretRef:
      name: goblin-letsencrypt-account
    solvers:
      - http01:
          ingress:
            name: goblin
YAML
  k3s kubectl apply -f "$bootstrap_dir/public-issuer.yaml"
  # F5 owns one Ingress per hostname. Editing this Ingress avoids a conflicting
  # solver Ingress, and keeps ACME validation working during later renewals.
  k3s kubectl patch ingress goblin -n goblin --type=merge -p \
    "{\"spec\":{\"tls\":[{\"hosts\":[\"${goblin_hostname}\"],\"secretName\":\"goblin-public-tls\"}]}}"
  stage 'Waiting for the trusted HTTPS certificate'
  k3s kubectl wait --for=create certificate/goblin-public-tls -n goblin --timeout=120s
  k3s kubectl wait --for=condition=Ready certificate/goblin-public-tls -n goblin --timeout=900s
  stage 'Checking public HTTPS with certificate verification'
  check_progress https
}

check_progress() {
  local scheme=$1 attempt
  for ((attempt=0; attempt<60; attempt++)); do
    if curl --fail --silent --show-error --noproxy '*' --connect-timeout 5 --max-time 10 \
        "${scheme}://${goblin_hostname}/setup/healthz" --output "$bootstrap_dir/setup-public-health.json" && \
        "$GOBLINCTL" internal json-test "$bootstrap_dir/setup-public-health.json" setup true; then return 0; fi
    sleep 5
  done
  printf 'Installation progress is not reachable through %s. Check DNS, NGINX, ports 80/443 and the certificate.\n' "$scheme" >&2
  return 1
}
