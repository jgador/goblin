#!/bin/sh
set -eu

mkdir -p /var/lib/goblin
printf '%s\n' failed > /var/lib/goblin/bootstrap-status
printf '%s\n' 'Goblin installation failed during VM access-key setup: Azure did not supply the SSH public key. The installation cannot continue. Retry the portal deployment and complete the key-generation and download step.' >&2
exit 1
