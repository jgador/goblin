# F5 NGINX Ingress Controller. Sourced by the installer, including workers.
# shellcheck shell=bash
# shellcheck disable=SC2154
NGINX_VERSION=5.7.0
NGINX_CHART_VERSION=2.8.0
NGINX_CHART_SHA256=a48a61321273160cd4f58ff4c4de9aed169097e1203de1f2ca1579c0236c6703
NGINX_TEMPLATE_SHA256=f3f640512b822ab9be6208573004af0a4104cadc69d135900e663b6d78769075

nginx_task() {
  stage 'Downloading F5 NGINX Ingress Controller'
  pull_images 1 "nginx/nginx-ingress:${NGINX_VERSION}"
  cached_download "https://helm.nginx.com/stable/nginx-ingress-${NGINX_CHART_VERSION}.tgz" \
    "nginx-ingress-${NGINX_CHART_VERSION}.tgz" "$NGINX_CHART_SHA256"
  cached_download "https://raw.githubusercontent.com/nginx/kubernetes-ingress/v${NGINX_VERSION}/internal/configs/version1/nginx.ingress.tmpl" \
    "nginx-ingress-template-${NGINX_VERSION}.tmpl" "$NGINX_TEMPLATE_SHA256"
  # F5's default $host drops the port from localhost:8788. Customize only the
  # HTTP/gRPC upstream Host directives in the checksum-pinned official template;
  # retain its ACME handling and keep client-supplied forwarded headers untrusted.
  local template="$download_cache/nginx-ingress-template-${NGINX_VERSION}.tmpl"
  # These patterns contain literal NGINX variables, not shell variables.
  # shellcheck disable=SC2016
  [[ $(grep -Ec '(proxy|grpc)_set_header Host .*\$host' "$template") == 2 ]] || return 1
  # shellcheck disable=SC2016
  sed '/set_header Host /s/\$host/\$http_host/' "$template" > "$bootstrap_dir/nginx-ingress.tmpl"
  stage 'Installing NGINX with internal routing'
  cat > "$bootstrap_dir/nginx-chart.yaml" <<YAML
apiVersion: helm.cattle.io/v1
kind: HelmChart
metadata:
  name: goblin-nginx
  namespace: kube-system
spec:
  targetNamespace: kube-system
  chartContent: $(base64 --wrap=0 "$download_cache/nginx-ingress-${NGINX_CHART_VERSION}.tgz")
  valuesContent: |-
    fullnameOverride: goblin-nginx
    serviceNameOverride: goblin-nginx
    controller:
      image:
        repository: nginx/nginx-ingress
        tag: "${NGINX_VERSION}"
      ingressClass:
        name: nginx
        setAsDefaultIngress: true
      service:
        type: ClusterIP
      config:
        entries:
          access-log-off: "True"
          proxy-buffering: "False"
          proxy-read-timeout: "3600s"
          proxy-send-timeout: "3600s"
          ingress-template: |-
$(sed 's/^/            /' "$bootstrap_dir/nginx-ingress.tmpl")
YAML
  # One watched chart; runtime service changes are direct Kubernetes patches.
  install -d -m 0755 /var/lib/rancher/k3s/server/manifests
  install -m 0600 "$bootstrap_dir/nginx-chart.yaml" /var/lib/rancher/k3s/server/manifests/goblin-nginx.yaml.tmp
  mv /var/lib/rancher/k3s/server/manifests/goblin-nginx.yaml.tmp /var/lib/rancher/k3s/server/manifests/goblin-nginx.yaml
  k3s kubectl wait --for=create deployment/goblin-nginx-controller -n kube-system --timeout=600s
  k3s kubectl rollout status deployment/goblin-nginx-controller -n kube-system --timeout=600s
  set_ingress_mode ClusterIP
}

set_ingress_mode() {
  # Persist the desired type in the watched chart as well as changing the live
  # Service. A Helm reconciliation or VM restart must keep public HTTPS enabled.
  local chart=/var/lib/rancher/k3s/server/manifests/goblin-nginx.yaml
  sed "s/^        type: \(ClusterIP\|LoadBalancer\)$/        type: $1/" "$chart" > "$chart.tmp" || return 1
  mv "$chart.tmp" "$chart" || return 1
  k3s kubectl apply -f "$chart" || return 1
  k3s kubectl patch service goblin-nginx -n kube-system --type=merge -p "{\"spec\":{\"type\":\"$1\"}}" || return 1
  wait_ingress_mode "$1"
}

wait_ingress_mode() {
  local actual pods attempt
  for ((attempt=0; attempt<60; attempt++)); do
    actual=$(k3s kubectl get service goblin-nginx -n kube-system -o jsonpath='{.spec.type}' --request-timeout=5s 2>/dev/null) || actual=''
    if [[ "$actual" == "$1" ]]; then
      if [[ "$1" == LoadBalancer ]]; then return 0; fi
      if pods=$(k3s kubectl get pods -n kube-system -o name --request-timeout=5s 2>/dev/null) && \
          ! printf '%s\n' "$pods" | grep -q '^pod/svclb-goblin-nginx-'; then return 0; fi
    fi
    sleep 3
  done
  printf 'NGINX did not switch to %s.\n' "$1" >&2
  return 1
}
