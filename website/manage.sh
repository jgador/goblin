#!/usr/bin/env bash
set -euo pipefail

website_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repo_dir=$(dirname -- "$website_dir")
artifacts="$repo_dir/.artifacts/website"
action=${1:-dev}
if (($#)); then shift; fi

# Use the native WSL daemon and private anonymous client configuration, matching
# the local installer. Do not load personal Docker contexts, logins, or helpers.
mkdir -p "$artifacts/docker"
if [[ ! -f "$artifacts/docker/config.json" ]]; then
    printf '%s\n' '{"cliPluginsExtraDirs":["/usr/libexec/docker/cli-plugins","/usr/lib/docker/cli-plugins"]}' >"$artifacts/docker/config.json"
fi
docker_command=(docker --config "$artifacts/docker" --host unix:///var/run/docker.sock)
compose=("${docker_command[@]}" compose --project-name goblin-website --file "$website_dir/compose.yaml" --env-file /dev/null)

local_k3s() {
    command -v k3s >/dev/null || { printf 'The local k3s installation is required.\n' >&2; exit 1; }
    k3s_command=(k3s)
    if ((EUID != 0)); then
        sudo -v
        k3s_command=(sudo k3s)
    fi
    kubectl=("${k3s_command[@]}" kubectl --kubeconfig /etc/rancher/k3s/k3s.yaml)
    "${kubectl[@]}" get --raw=/readyz --request-timeout=5s >/dev/null
}

case "$action" in
    dev|stop|status|logs)
        if ! "${compose[@]}" version >/dev/null 2>&1; then
            printf 'Install the native Linux Docker Compose plugin in WSL; see website/README.md.\n' >&2
            exit 1
        fi
        case "$action" in
            dev)
                "${compose[@]}" up --build --detach --wait --wait-timeout 60 "$@"
                printf '\nWebsite ready: http://localhost:5173\nIt is running detached; edit website/ for live reload.\n'
                ;;
            stop) "${compose[@]}" down "$@" ;;
            status) "${compose[@]}" ps "$@" ;;
            logs) "${compose[@]}" logs --tail 100 "$@" ;;
        esac
        ;;
    deploy)
        local_k3s
        metrics_ready=$("${kubectl[@]}" get apiservice v1beta1.metrics.k8s.io -o 'jsonpath={.status.conditions[?(@.type=="Available")].status}')
        if [[ "$metrics_ready" != True ]]; then
            printf 'Website autoscaling requires the local k3s metrics-server to be available.\n' >&2
            exit 1
        fi
        node_count=$("${kubectl[@]}" get nodes -o go-template='{{len .items}}')
        if [[ "$node_count" != 1 ]]; then
            printf 'Website image import requires the single-node local k3s installation.\n' >&2
            exit 1
        fi
        image="docker.io/library/goblin-website:local-$(date -u +%Y%m%d%H%M%S%N)"
        "${docker_command[@]}" build --file "$website_dir/Dockerfile" --target production --tag "$image" "$repo_dir"
        # Stream the image: no filesystem archive or registry is needed.
        "${docker_command[@]}" save "$image" | "${k3s_command[@]}" ctr --namespace k8s.io images import -
        sed "s|image: docker.io/library/goblin-website:local|image: $image|" "$website_dir/k8s/deployment.yaml" >"$artifacts/deployment.yaml"
        "${kubectl[@]}" apply -f "$website_dir/k8s/namespace.yaml"
        "${kubectl[@]}" apply -f "$website_dir/k8s/service.yaml" -f "$artifacts/deployment.yaml"
        "${kubectl[@]}" -n goblin rollout status deployment/website --timeout=120s
        "${kubectl[@]}" apply -f "$website_dir/k8s/hpa.yaml"
        "${kubectl[@]}" -n goblin wait --for=condition=ScalingActive horizontalpodautoscaler/website --timeout=120s
        printf '\nWebsite deployed to local k3s. Run make website-preview to open http://localhost:5174.\n'
        ;;
    load-test)
        local_k3s
        node_command=$(command -v node)
        if ((EUID == 0)); then
            exec "$node_command" "$website_dir/load-test.mjs" "$@"
        else
            exec sudo "$node_command" "$website_dir/load-test.mjs" "$@"
        fi
        ;;
    preview)
        local_k3s
        printf 'Open http://localhost:5174. Keep this terminal open; Ctrl+C stops forwarding.\n'
        exec "${kubectl[@]}" -n goblin port-forward --address 127.0.0.1 service/website 5174:80
        ;;
    *)
        printf 'Usage: bash website/manage.sh {dev|stop|status|logs|deploy|preview|load-test}\n' >&2
        exit 2
        ;;
esac
