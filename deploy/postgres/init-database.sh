#!/usr/bin/env bash
# The official image sources this file once, while initializing an empty PVC.
# Keep shell options and temporary environment changes inside this subshell.
(
set +x
set -euo pipefail
export GOBLIN_APP_PASSWORD
GOBLIN_APP_PASSWORD=$(cat "${GOBLIN_APP_PASSWORD_FILE:?}")
psql --no-psqlrc --set=ON_ERROR_STOP=1 --username "${POSTGRES_USER:?}" --dbname "${POSTGRES_DB:?}" <<'SQL'
\getenv app_password GOBLIN_APP_PASSWORD
BEGIN;
CREATE ROLE goblin_app LOGIN PASSWORD :'app_password'
    NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;
REVOKE ALL ON DATABASE goblin FROM PUBLIC;
GRANT CONNECT ON DATABASE goblin TO goblin_app;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
CREATE SCHEMA goblin AUTHORIZATION goblin_admin;
GRANT USAGE ON SCHEMA goblin TO goblin_app;
ALTER DEFAULT PRIVILEGES FOR ROLE goblin_admin IN SCHEMA goblin
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO goblin_app;
ALTER DEFAULT PRIVILEGES FOR ROLE goblin_admin IN SCHEMA goblin
    GRANT USAGE, SELECT ON SEQUENCES TO goblin_app;
COMMIT;
SQL
unset GOBLIN_APP_PASSWORD
)
