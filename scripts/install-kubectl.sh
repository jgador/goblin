#!/usr/bin/env bash
set -euo pipefail

# Production targets AK3S; verification retains the version used by CI.
case "${1:-}" in
    production)
        version=v1.37.1
        checksum=65691ff77eb6fa44c908b77a1082c9f092c3b9733b5cefabec0d1104890e21a8
        ;;
    verification)
        version=v1.36.4
        checksum=8b8f088da2dab964f853b38464033b1be15ede2839eca751482357c45abdd05a
        ;;
    *)
        printf 'Usage: bash scripts/install-kubectl.sh {production|verification}\n' >&2
        exit 2
        ;;
esac
if (($# != 1)); then
    printf 'Usage: bash scripts/install-kubectl.sh {production|verification}\n' >&2
    exit 2
fi

repo_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
mkdir -p "$repo_dir/.artifacts/kubectl"
download_dir=$(mktemp -d "$repo_dir/.artifacts/kubectl/download.XXXXXXXX")
trap 'rm -rf "$download_dir"' EXIT
binary="$download_dir/kubectl"
curl --fail --silent --show-error --location --proto '=https' --proto-redir '=https' \
    "https://dl.k8s.io/release/$version/bin/linux/amd64/kubectl" -o "$binary"
printf '%s  %s\n' "$checksum" "$binary" | sha256sum --check
sudo install -m 0755 "$binary" /usr/local/bin/kubectl
