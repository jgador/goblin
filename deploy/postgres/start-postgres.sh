#!/usr/bin/env bash
set -euo pipefail

# The pre-18 PVC stored data directly in pgdata. Refuse to initialize a new
# database beside it when an existing installation changes its image version.
if [[ -s /var/lib/postgresql/pgdata/PG_VERSION ]]; then
  printf 'Existing PostgreSQL data requires a major-version migration before using PostgreSQL 18. Restore into a fresh volume or run pg_upgrade; the existing data has been preserved.\n' >&2
  exit 1
fi

# Secret volumes update their ..data symlink atomically. PostgreSQL needs SIGHUP
# to load a renewed server certificate/CA; no restart or connection edit is needed.
watch_certificates() {
  local previous='' current
  while sleep 10; do
    current=$(readlink /run/secrets/goblin-postgres-tls/..data)
    if [[ "$current" != "$previous" ]] && pg_ctl status -D "$PGDATA" >/dev/null 2>&1; then
      if pg_ctl reload -D "$PGDATA" >/dev/null 2>&1; then previous=$current; fi
    fi
  done
}
watch_certificates &
exec /usr/local/bin/docker-entrypoint.sh postgres \
  -c ssl=on -c ssl_min_protocol_version=TLSv1.2 \
  -c ssl_cert_file=/run/secrets/goblin-postgres-tls/tls.crt \
  -c ssl_key_file=/run/secrets/goblin-postgres-tls/tls.key \
  -c ssl_ca_file=/run/secrets/goblin-postgres-tls/ca.crt \
  -c hba_file=/etc/goblin-postgres/pg_hba.conf \
  -c ident_file=/etc/goblin-postgres/pg_ident.conf
