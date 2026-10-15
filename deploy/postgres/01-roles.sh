#!/bin/sh
# Runs once when the PostgreSQL volume is initialised. Creates the restricted application login.
# Schema objects and grants are created later by the migrator (`HandyNet.Api migrate`).
set -eu
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<SQL
DO \$\$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'handynet_app') THEN
    CREATE ROLE handynet_app LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '${HANDYNET_APP_PASSWORD}';
  END IF;
END \$\$;
GRANT CONNECT ON DATABASE ${POSTGRES_DB} TO handynet_app;
SQL
