#!/bin/sh
# Nightly logical backup (custom format, compressed). Run from cron on the Docker host, e.g.
#   15 2 * * *  /opt/handynet/deploy/backup/backup.sh >> /var/log/handynet-backup.log 2>&1
# Copy the resulting files off-site (Hetzner Storage Box / Object Storage). For point-in-time recovery
# use WAL archiving (pgBackRest or WAL-G) — see docs/operations.md; this script is the baseline.
set -eu
BACKUP_DIR=${BACKUP_DIR:-/var/backups/handynet}
KEEP_DAYS=${KEEP_DAYS:-14}
STAMP=$(date -u +%Y%m%dT%H%M%SZ)
mkdir -p "$BACKUP_DIR"
docker compose exec -T postgres pg_dump -U handynet_owner -d handynet -Fc -Z 6 > "$BACKUP_DIR/handynet-$STAMP.dump"
# Verify the archive is readable before trusting it.
docker compose exec -T postgres pg_restore --list < "$BACKUP_DIR/handynet-$STAMP.dump" > /dev/null
find "$BACKUP_DIR" -name 'handynet-*.dump' -mtime +"$KEEP_DAYS" -delete
echo "backup ok: $BACKUP_DIR/handynet-$STAMP.dump"
