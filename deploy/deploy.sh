#!/bin/sh
# Deploy / update on the server. Run from the deploy/ directory:
#   ssh host 'cd /opt/handynet/deploy && ./deploy.sh <sha>'
#   ./deploy.sh [image-tag]     image-tag = commit SHA built by CI (default: IMAGE_TAG in .env.prod)
# Order: backup -> pull -> up (the one-shot migrator runs before the new API starts) -> readiness check.
set -eu
cd "$(dirname "$0")"
[ -f .env.prod ] || { echo ".env.prod missing" >&2; exit 1; }
if [ -n "${1:-}" ]; then IMAGE_TAG="$1"; export IMAGE_TAG; fi
DC="docker compose --env-file .env.prod -f compose.prod.yml"
if $DC ps --status running --services 2>/dev/null | grep -qx backup; then
  $DC exec -T backup /usr/local/bin/backup-service.sh once
fi
$DC pull --ignore-buildable
$DC build backup
$DC up -d --remove-orphans
i=0
while [ $i -lt 30 ]; do
  if $DC exec -T api bash -c 'exec 3<>/dev/tcp/127.0.0.1/8080 && printf "GET /health/ready HTTP/1.0\r\n\r\n" >&3 && head -n1 <&3 | grep -q " 200 "'; then
    echo "deploy ok (tag: ${IMAGE_TAG:-from .env.prod})"
    $DC ps
    exit 0
  fi
  i=$((i + 1))
  sleep 4
done
echo "API not ready after deploy; check: $DC logs --tail=100 api migrator" >&2
exit 1
