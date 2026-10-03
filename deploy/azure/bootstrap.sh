#!/usr/bin/env bash
# Azure Custom Script launches scripts through /bin/sh; ensure Bash semantics.
if [ -z "${BASH_VERSION:-}" ]; then
  exec /bin/bash "$0" "$@"
fi
set +x
set -Eeuo pipefail
umask 077

# Public status is readable by the unprivileged UI; configuration is root-only.
install -d -m 0751 /var/lib/goblin
install -d -m 0755 /var/lib/goblin/install
install -d -m 0700 /var/lib/goblin/install/private
exec > >(tee -a /var/log/goblin-bootstrap.log) 2>&1
printf 'running\n' > /var/lib/goblin/bootstrap-status
bootstrap_dir=''
current_stage='Preparing the server'
stage() { current_stage=$1; printf '[Goblin] %s\n' "$current_stage"; }
finish() {
  local result=$?
  if [[ "$result" != 0 ]]; then
    printf 'failed\n' > /var/lib/goblin/bootstrap-status
    printf '[Goblin] Setup failed during: %s (exit code %s).\n' "$current_stage" "$result" >&2
  fi
  if [[ -n "$bootstrap_dir" ]]; then rm -rf "$bootstrap_dir"; fi
  exit "$result"
}
trap finish EXIT

if command -v cloud-init >/dev/null; then
  cloud_init_result=0
  cloud-init status --wait || cloud_init_result=$?
  if [[ "$cloud_init_result" != 0 && "$cloud_init_result" != 2 ]]; then exit "$cloud_init_result"; fi
fi
if ! command -v curl >/dev/null || ! command -v tar >/dev/null; then
  apt-get -o DPkg::Lock::Timeout=300 update
  DEBIAN_FRONTEND=noninteractive apt-get -o DPkg::Lock::Timeout=300 install -y ca-certificates curl tar
fi
bootstrap_dir=$(mktemp -d /var/lib/goblin/bootstrap.XXXXXX)

# ARM encodes all user-supplied values before embedding them in shell source.
goblin_hostname=$(printf '%s' '__GOBLIN_HOSTNAME_BASE64__' | base64 --decode)
goblin_source_ref=$(printf '%s' '__GOBLIN_SOURCE_REF_BASE64__' | base64 --decode)
if [[ "$goblin_hostname" != localhost && ! "$goblin_hostname" =~ ^([a-z0-9]([a-z0-9-]*[a-z0-9])?\.)+[a-z]{2,}$ ]] || \
   [[ ! "$goblin_source_ref" =~ ^[A-Za-z0-9][A-Za-z0-9._/-]{0,127}$ ]]; then
  printf 'Invalid public hostname or Goblin source ref.\n' >&2
  exit 1
fi
stage 'Preparing native Goblin administration tooling'
goblinctl_version='__GOBLINCTL_VERSION__'
goblinctl_sha256='__GOBLINCTL_SHA256__'
if [[ "$(uname -m)" != x86_64 ]]; then printf 'Goblin requires an x86-64 host.\n' >&2; exit 1; fi
if [[ -n "${GOBLINCTL_LOCAL_BINARY:-}" ]]; then
  # Explicit developer input: never compiled or downloaded on the installed host.
  [[ "${GOBLINCTL_LOCAL_SHA256:-}" =~ ^[a-f0-9]{64}$ ]]
  install -m 0755 "$GOBLINCTL_LOCAL_BINARY" "$bootstrap_dir/goblinctl"
  printf '%s  %s\n' "$GOBLINCTL_LOCAL_SHA256" "$bootstrap_dir/goblinctl" | sha256sum --check
else
  [[ "$goblinctl_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ && "$goblinctl_sha256" =~ ^[a-f0-9]{64}$ ]]
  curl --fail --silent --show-error --location --proto '=https' --proto-redir '=https' \
    --retry 5 --connect-timeout 15 --max-time 300 \
    "https://github.com/jgador/goblin/releases/download/goblinctl-v${goblinctl_version}/goblinctl-x86_64-unknown-linux-musl.tar.gz" \
    --output "$bootstrap_dir/goblinctl.tar.gz"
  printf '%s  %s\n' "$goblinctl_sha256" "$bootstrap_dir/goblinctl.tar.gz" | sha256sum --check
  # Accept the original single-file releases and the licensed archive layout.
  # Keep an exact allowlist before extracting any untrusted archive entry.
  goblinctl_archive_entries=$(tar -tzf "$bootstrap_dir/goblinctl.tar.gz")
  if [[ "$goblinctl_archive_entries" != goblinctl && "$goblinctl_archive_entries" != $'goblinctl\nLICENSE\nNOTICE\nTHIRD_PARTY_NOTICES.md' ]]; then
    printf 'Unsupported native archive contents.\n' >&2
    exit 1
  fi
  tar --extract --gzip --file "$bootstrap_dir/goblinctl.tar.gz" --directory "$bootstrap_dir" --no-same-owner --no-same-permissions goblinctl
  chmod 0755 "$bootstrap_dir/goblinctl"
fi
[[ "$("$bootstrap_dir/goblinctl" --version)" == "goblinctl $goblinctl_version" ]]

stage 'Preparing the Goblin password'
if [[ -n "${GOBLIN_PASSWORD_HASH_FILE:-}" ]]; then
  "$bootstrap_dir/goblinctl" internal validate-password "$GOBLIN_PASSWORD_HASH_FILE"
  install -m 0600 "$GOBLIN_PASSWORD_HASH_FILE" "$bootstrap_dir/owner-password"
else
  printf '%s' '__GOBLIN_PASSWORD_BASE64__' | base64 --decode | \
    "$bootstrap_dir/goblinctl" internal hash-password > "$bootstrap_dir/owner-password"
fi

# Reprovisioning explicitly starts a new attempt and can update the owner password.
# Stop the old worker (including recovery) before replacing its executable/config.
if systemctl cat goblin-installer.service >/dev/null 2>&1; then systemctl stop goblin-installer.service; fi
exec 9>/var/lib/goblin/install/installer.lock
flock -w 420 9
if systemctl cat goblin-setup.service >/dev/null 2>&1; then systemctl stop goblin-setup.service; fi
"$bootstrap_dir/goblinctl" internal activate --binary "$bootstrap_dir/goblinctl"
export GOBLINCTL=/opt/goblin/bin/goblinctl
install -m 0600 "$bootstrap_dir/owner-password" /var/lib/goblin/install/private/owner-password
printf '%s\n' "$goblin_hostname" > /var/lib/goblin/install/private/hostname
printf '%s\n' "$goblin_source_ref" > /var/lib/goblin/install/private/source-ref
printf 'http://%s\n' "$goblin_hostname" > /var/lib/goblin/public-url
rm -rf /var/lib/goblin/install/private/work
"$GOBLINCTL" internal state init
exec 9>&-

stage 'Starting the installation status page'
systemctl daemon-reload
systemctl enable --now goblin-setup.service
setup_ready=false
for ((attempt=0; attempt<30; attempt++)); do
  if curl --fail --silent --show-error --noproxy '*' --connect-timeout 2 --max-time 3 \
      --unix-socket /run/goblin-setup/health.sock http://127.0.0.1/setup/healthz --output "$bootstrap_dir/setup-health.json" && \
      "$GOBLINCTL" internal json-test "$bootstrap_dir/setup-health.json" setup true
  then setup_ready=true; break; fi
  sleep 1
done
if [[ "$setup_ready" != true ]]; then printf 'The installation status page did not start.\n' >&2; exit 1; fi
stage 'Starting background installation'
# Type=exec returns once the worker is launched, independent of cluster readiness.
systemctl enable --now goblin-installer.service
printf 'setup-ready\n' > /var/lib/goblin/bootstrap-status
printf 'Follow installation progress: http://%s\n' "$goblin_hostname"
