#!/usr/bin/env bash
set -euo pipefail
# Keep client/server versions within Kubernetes' supported one-minor skew.
mkdir -p .artifacts/website-production/bin
binary=.artifacts/website-production/bin/kubectl
curl --fail --silent --show-error --location --proto '=https' --proto-redir '=https' \
    https://dl.k8s.io/release/v1.37.1/bin/linux/amd64/kubectl -o "$binary"
printf '%s  %s\n' 65691ff77eb6fa44c908b77a1082c9f092c3b9733b5cefabec0d1104890e21a8 "$binary" | sha256sum --check
sudo install -m 0755 "$binary" /usr/local/bin/kubectl
