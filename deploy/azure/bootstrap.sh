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
if ! command -v curl >/dev/null || ! command -v python3 >/dev/null; then
  apt-get -o DPkg::Lock::Timeout=300 update
  DEBIAN_FRONTEND=noninteractive apt-get -o DPkg::Lock::Timeout=300 install -y ca-certificates curl python3
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
stage 'Preparing the Goblin password'
cat > "$bootstrap_dir/hash-password.py" <<'PYTHON'
__GOBLIN_PASSWORD_HASHER__
PYTHON
printf '%s' '__GOBLIN_PASSWORD_BASE64__' | base64 --decode | \
  python3 "$bootstrap_dir/hash-password.py" > "$bootstrap_dir/owner-password"

stage 'Preparing the setup bundle'
# The deterministic, prebuilt zipapp is included in this version of the template.
# No SDK, application build, cluster or external bundle release is needed here.
base64 --decode > "$bootstrap_dir/goblin-setup.pyz" <<'BUNDLE'
__GOBLIN_SETUP_BUNDLE_BASE64__
BUNDLE
printf '%s  %s\n' '__GOBLIN_SETUP_BUNDLE_SHA256__' "$bootstrap_dir/goblin-setup.pyz" | sha256sum --check

# Reprovisioning explicitly starts a new attempt and can update the owner password.
# Stop the old worker (including recovery) before replacing its executable/config.
if systemctl cat goblin-installer.service >/dev/null 2>&1; then systemctl stop goblin-installer.service; fi
exec 9>/var/lib/goblin/install/installer.lock
flock -w 420 9
if systemctl cat goblin-setup.service >/dev/null 2>&1; then systemctl stop goblin-setup.service; fi
install -d -m 0755 /opt/goblin/setup
install -m 0755 "$bootstrap_dir/goblin-setup.pyz" /opt/goblin/setup/goblin-setup.pyz
python3 /opt/goblin/setup/goblin-setup.pyz unpack /opt/goblin/setup
install -m 0644 /opt/goblin/setup/goblin-setup.service /etc/systemd/system/goblin-setup.service
install -m 0644 /opt/goblin/setup/goblin-installer.service /etc/systemd/system/goblin-installer.service
install -m 0600 "$bootstrap_dir/owner-password" /var/lib/goblin/install/private/owner-password
printf '%s\n' "$goblin_hostname" > /var/lib/goblin/install/private/hostname
printf '%s\n' "$goblin_source_ref" > /var/lib/goblin/install/private/source-ref
printf 'http://%s\n' "$goblin_hostname" > /var/lib/goblin/public-url
rm -rf /var/lib/goblin/install/private/work
python3 /opt/goblin/setup/goblin-setup.pyz state init
exec 9>&-

stage 'Starting the installation status page'
systemctl daemon-reload
systemctl enable --now goblin-setup.service
setup_ready=false
for ((attempt=0; attempt<30; attempt++)); do
  if curl --fail --silent --show-error --noproxy '*' --connect-timeout 2 --max-time 3 \
      --unix-socket /run/goblin-setup/health.sock http://127.0.0.1/setup/healthz --output "$bootstrap_dir/setup-health.json" && \
      python3 - "$bootstrap_dir/setup-health.json" <<'PYTHON'
import json, sys
sys.exit(0 if json.load(open(sys.argv[1])).get('setup') is True else 1)
PYTHON
  then setup_ready=true; break; fi
  sleep 1
done
if [[ "$setup_ready" != true ]]; then printf 'The installation status page did not start.\n' >&2; exit 1; fi
stage 'Starting background installation'
# Type=exec returns once the worker is launched, independent of cluster readiness.
systemctl enable --now goblin-installer.service
printf 'setup-ready\n' > /var/lib/goblin/bootstrap-status
printf 'Follow installation progress: http://%s\n' "$goblin_hostname"
