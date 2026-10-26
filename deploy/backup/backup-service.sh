#!/bin/sh
# Runs inside the backup container (deploy/compose.prod.yml).
#   daemon   run a backup every day at BACKUP_TIME_UTC (default 02:15), forever
#   once     run a single backup now and exit (docker compose run --rm backup once)
# Produces handynet-<stamp>.dump (pg_dump -Fc) and handynet-files-<stamp>.tar.gz (product images + Data Protection keys),
# verifies the dump, prunes local copies older than KEEP_DAYS and, if BACKUP_S3_BUCKET is set, uploads to S3-compatible storage.
set -eu
BACKUP_DIR=${BACKUP_DIR:-/backups}
KEEP_DAYS=${KEEP_DAYS:-14}
BACKUP_TIME_UTC=${BACKUP_TIME_UTC:-02:15}
: "${PGHOST:=postgres}" "${PGUSER:=handynet_owner}" "${PGDATABASE:=handynet}"
export PGHOST PGUSER PGDATABASE   # PGPASSWORD comes from the environment

run_backup() {
  stamp=$(date -u +%Y%m%dT%H%M%SZ)
  mkdir -p "$BACKUP_DIR"
  dump="$BACKUP_DIR/handynet-$stamp.dump"
  pg_dump -Fc -Z 6 > "$dump.part"
  pg_restore --list < "$dump.part" > /dev/null   # archive must be readable
  mv "$dump.part" "$dump"
  files="$BACKUP_DIR/handynet-files-$stamp.tar.gz"
  tar -czf "$files" -C /data .
  find "$BACKUP_DIR" -name 'handynet-*' -type f -mtime +"$KEEP_DAYS" -delete
  if [ -n "${BACKUP_S3_BUCKET:-}" ]; then
    export AWS_ACCESS_KEY_ID="$BACKUP_S3_ACCESS_KEY" AWS_SECRET_ACCESS_KEY="$BACKUP_S3_SECRET_KEY" AWS_DEFAULT_REGION="${BACKUP_S3_REGION:-eu-central}"
    prefix=${BACKUP_S3_PREFIX:-handynet}
    for f in "$dump" "$files"; do
      if [ -n "${BACKUP_S3_ENDPOINT:-}" ]; then
        aws --endpoint-url "$BACKUP_S3_ENDPOINT" s3 cp "$f" "s3://$BACKUP_S3_BUCKET/$prefix/$(basename "$f")" --only-show-errors
      else
        aws s3 cp "$f" "s3://$BACKUP_S3_BUCKET/$prefix/$(basename "$f")" --only-show-errors
      fi
    done
    # Remote retention: use a bucket lifecycle rule (see docs/deployment.md); this script never deletes remote objects.
    echo "uploaded to s3://$BACKUP_S3_BUCKET/$prefix/"
  fi
  echo "backup ok: $dump"
}

case "${1:-daemon}" in
  once) run_backup ;;
  daemon)
    echo "backup daemon: daily at $BACKUP_TIME_UTC UTC, keep $KEEP_DAYS days"
    hh=${BACKUP_TIME_UTC%:*}; mm=${BACKUP_TIME_UTC#*:}
    hh=${hh#0}; mm=${mm#0}; hh=${hh:-0}; mm=${mm:-0}   # no leading zeros: shell arithmetic would read them as octal
    while true; do
      # Seconds since UTC midnight (busybox date has no reliable -d parsing of dates, so compute by hand).
      now=$(( $(date -u +%s) % 86400 ))
      target=$(( hh * 3600 + mm * 60 ))
      wait=$(( target - now ))
      [ "$wait" -le 0 ] && wait=$(( wait + 86400 ))
      sleep "$wait"
      run_backup || echo "BACKUP FAILED $(date -u +%FT%TZ)" >&2
    done ;;
  *) echo "usage: $0 [daemon|once]" >&2; exit 2 ;;
esac
