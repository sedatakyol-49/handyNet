#!/bin/sh
# Prints freshly generated secrets as KEY=value lines. Nothing is stored; redirect into your .env.prod:
#   ./scripts/generate-secrets.sh >> .env.prod        (then remove the empty placeholder lines for the same keys)
# Hex/base64url only, so values are safe inside connection strings and env files. Keep .env.prod at mode 600, never commit it.
set -eu
command -v openssl >/dev/null || { echo "openssl is required" >&2; exit 1; }
echo "POSTGRES_PASSWORD=$(openssl rand -hex 24)"
echo "HANDYNET_APP_PASSWORD=$(openssl rand -hex 24)"
echo "JWT_SIGNING_KEY=$(openssl rand -base64 48 | tr -d '\n=' | tr '+/' '-_')"
echo "BOOTSTRAP_ADMIN_PASSWORD=$(openssl rand -hex 12)-Aa1"
