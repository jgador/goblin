#!/usr/bin/env bash
# The official image sources this file once, while initializing an empty PVC.
# Keep shell options and temporary environment changes inside this subshell.
(
set +x
set -euo pipefail
psql --no-psqlrc --set=ON_ERROR_STOP=1 --username "${POSTGRES_USER:?}" --dbname "${POSTGRES_DB:?}" <<'SQL'
BEGIN;
CREATE ROLE goblin_app LOGIN PASSWORD NULL
    NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;
-- The image requires an initialization password, but clients use certificates.
ALTER ROLE goblin_admin PASSWORD NULL;
REVOKE ALL ON DATABASE goblin FROM PUBLIC;
GRANT CONNECT ON DATABASE goblin TO goblin_app;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO goblin_app;
ALTER DEFAULT PRIVILEGES FOR ROLE goblin_admin IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO goblin_app;
ALTER DEFAULT PRIVILEGES FOR ROLE goblin_admin IN SCHEMA public
    GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO goblin_app;
COMMIT;
SQL
)
