#!/bin/sh
# Restore drill against the production stack: restores a dump (default: newest in the backups volume) into the scratch
# database handynet_restore_test, runs sanity queries, drops it. Never touches the live database.
#   cd /opt/handynet/deploy && ./backup/restore-prod.sh [dump-file-name]
set -eu
cd "$(dirname "$0")/.."
DC="docker compose --env-file .env.prod -f compose.prod.yml"
FILE=${1:-}
if [ -z "$FILE" ]; then
  FILE=$($DC run --rm --no-deps -T --entrypoint sh backup -c 'ls -1t /backups/handynet-*.dump | head -n 1' | tr -d '\r')
fi
case "$FILE" in /*) ;; *) FILE=/backups/$FILE ;; esac
$DC run --rm -T --entrypoint sh backup -c "
  set -eu
  dropdb --if-exists handynet_restore_test
  createdb handynet_restore_test
  pg_restore -d handynet_restore_test --no-owner '$FILE'
  psql -v ON_ERROR_STOP=1 -d handynet_restore_test -c \"SELECT (SELECT count(*) FROM infra.__ef_migrations_history) AS migrations, (SELECT count(*) FROM tenancy.organizations) AS organizations, (SELECT count(*) FROM ordering.orders) AS orders, (SELECT count(*) FROM infra.audit_log) AS audit_entries;\"
  dropdb handynet_restore_test
  echo 'restore test ok: $FILE'"
