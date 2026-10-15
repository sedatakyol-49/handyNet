#!/bin/sh
# Restore drill: restores the newest backup into a scratch database and runs sanity queries.
# Run weekly; a backup that has never been restored is not a backup.
set -eu
BACKUP_DIR=${BACKUP_DIR:-/var/backups/handynet}
LATEST=$(ls -1t "$BACKUP_DIR"/handynet-*.dump | head -n 1)
docker compose exec -T postgres dropdb -U handynet_owner --if-exists handynet_restore_test
docker compose exec -T postgres createdb -U handynet_owner handynet_restore_test
docker compose exec -T postgres pg_restore -U handynet_owner -d handynet_restore_test --no-owner < "$LATEST"
docker compose exec -T postgres psql -U handynet_owner -d handynet_restore_test -v ON_ERROR_STOP=1 -c \
  "SELECT (SELECT count(*) FROM infra.__ef_migrations_history) AS migrations,
          (SELECT count(*) FROM tenancy.organizations) AS organizations,
          (SELECT count(*) FROM ordering.orders) AS orders,
          (SELECT count(*) FROM infra.audit_log) AS audit_entries;"
docker compose exec -T postgres dropdb -U handynet_owner handynet_restore_test
echo "restore test ok: $LATEST"
