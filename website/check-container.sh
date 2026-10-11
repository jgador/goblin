#!/usr/bin/env bash
set -euo pipefail
image=${1:?Pass the production image to verify}
container=$(docker run --detach --read-only --tmpfs /tmp:rw,noexec,nosuid,size=32m --publish 127.0.0.1::8080 "$image")
trap 'docker rm --force "$container" >/dev/null' EXIT
port=$(docker inspect --format '{{(index (index .NetworkSettings.Ports "8080/tcp") 0).HostPort}}' "$container")
for ((attempt=0; attempt<30; attempt++)); do
    if curl --fail --silent --max-time 2 "http://127.0.0.1:$port/healthz" >/dev/null; then break; fi
    sleep 1
done
node website/verify.mjs "http://127.0.0.1:$port"
