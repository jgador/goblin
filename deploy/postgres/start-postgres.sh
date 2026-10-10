#!/usr/bin/env bash
set -euo pipefail

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
