# Deployment (single EU host, MVP)

Recommended target: one Hetzner Cloud VPS in Germany (Falkenstein/Nuremberg) running Docker Compose. The stack is
`deploy/compose.prod.yml`: Caddy (automatic HTTPS) -> web (Angular SSR) and API -> PostgreSQL 16, plus a daily backup
container. Only ports 80/443 are published. See also `docs/operations.md` (roles, secrets, GDPR).

```
Internet -> Caddy :443 --/api/*, /health/*--> api:8080 --> postgres (handynet_app, RLS)
                      \--everything else---> web:4000 (SSR)         migrator (one-shot, handynet_owner)
                                                                    backup (daily pg_dump + files, optional S3 upload)
```

## 1. Server

- Hetzner Cloud **CX32** (4 vCPU shared, 8 GB RAM, 80 GB) is a comfortable start; CX22 (2 vCPU / 4 GB) works for a
  pilot. Check current pricing (roughly EUR 5-15 per month). Image: Ubuntu 24.04. Add your SSH key, enable Hetzner
  backups (about +20 percent) as a second safety net, keep the location in the EU.
- Resource limits in the compose file add up to roughly 3 GB RAM (postgres 1 GB, api 1 GB, web 512 MB, caddy/backup 256 MB each).

Harden and install Docker:

```sh
adduser deploy && usermod -aG sudo deploy        # then log in as deploy, disable root/password SSH login
apt update && apt -y upgrade && apt -y install ufw unattended-upgrades fail2ban
ufw default deny incoming && ufw allow 22/tcp && ufw allow 80/tcp && ufw allow 443/tcp && ufw allow 443/udp && ufw enable
curl -fsSL https://get.docker.com | sh && usermod -aG docker deploy
```

Docker publishes ports by editing iptables itself and bypasses ufw for published ports. This stack publishes only
80/443 (which you allow anyway) and no database port, so that is fine. Do not add `ports:` to postgres/api/web.
Optionally also set a Hetzner Cloud Firewall (22, 80, 443) in front of the server.

## 2. DNS

Create an `A` (and `AAAA`) record for your domain (e.g. `seha.example.de`) pointing to the server before the first
start; Caddy obtains the Let's Encrypt certificate on first start and needs ports 80/443 reachable.

## 3. Images (CI)

On every push to `main`, `.github/workflows/ci.yml` runs the tests and then builds and pushes
`ghcr.io/<owner>/handynet-api:<commit-sha>` and `handynet-web:<commit-sha>` (plus `:latest`). Pull requests only build.
No secret is baked into an image: all configuration comes from the environment at runtime (`.dockerignore` files also
exclude `.env*`, keys and `*.local.json` from the build context).

Make the GHCR packages readable by the server: either set the packages to public (no secrets inside) or create a
GitHub personal access token with `read:packages` and run `docker login ghcr.io -u <user>` on the server once.

## 4. First start

```sh
sudo mkdir -p /opt/handynet && sudo chown deploy: /opt/handynet && cd /opt/handynet
git clone <repo-url> .            # or copy only the deploy/ directory (scp -r deploy host:/opt/handynet/)
cd deploy
cp .env.prod.example .env.prod && chmod 600 .env.prod
./scripts/generate-secrets.sh     # paste the four generated values into .env.prod (replace the empty lines)
$EDITOR .env.prod                 # DOMAIN, ACME_EMAIL, IMAGE_PREFIX, IMAGE_TAG, SMTP_*, bootstrap admin e-mail
docker compose --env-file .env.prod -f compose.prod.yml config -q      # validates the file and required variables
./deploy.sh                       # pull, backup (skipped on first run), migrate, start, wait for /health/ready
```

Every variable is documented in `deploy/.env.prod.example`. Store a copy of `.env.prod` in your password manager:
losing `JWT_SIGNING_KEY` only logs users out, but losing the Data Protection key ring (volume `dpkeys`, included in the
backups) makes encrypted fiscal credentials unreadable.

Order of events: `postgres` initialises an empty volume and runs `postgres/01-roles.sh` (creates `handynet_app`,
`NOBYPASSRLS`) -> one-shot `migrator` applies migrations as `handynet_owner` -> `api` starts (the compose dependency
`service_completed_successfully` guarantees migrations ran first) -> `web` -> `caddy`.

**Bootstrap admin:** with `BOOTSTRAP_ADMIN_EMAIL`/`BOOTSTRAP_ADMIN_PASSWORD` set, the API creates or promotes that
platform admin at startup (idempotent). Log in at `https://<domain>`, change the password to a personal one, then
remove `BOOTSTRAP_ADMIN_PASSWORD` from `.env.prod` and run `docker compose ... up -d api`.

Smoke test: `curl -fsS https://<domain>/health/ready` returns `Healthy`; open the site, log in, send a test e-mail
(password reset) to verify SMTP.

## 5. Updates

From your machine or CI: `ssh deploy@host 'cd /opt/handynet/deploy && ./deploy.sh <commit-sha>'`.
`deploy.sh` takes a fresh backup, pulls the images, runs `up -d` (migrator first, then the new API/web, with
`--remove-orphans`) and waits for `/health/ready`. To pin the version permanently, set `IMAGE_TAG` in `.env.prod`.
Rollback: `./deploy.sh <previous-sha>`; migrations must be backward compatible (expand/contract, see
`docs/operations.md`), otherwise restore the pre-deploy backup.
Update the repo files on the server (`git pull`) when `compose.prod.yml` or the Caddyfile changed.

## 6. Backups and restore drill

The `backup` container runs daily at `BACKUP_TIME_UTC` (default 02:15 UTC): `pg_dump -Fc` (verified with
`pg_restore --list`), a tarball of product images and the Data Protection keys, local retention `BACKUP_KEEP_DAYS`
(volume `backups`), and, if `BACKUP_S3_BUCKET` is set, upload to S3-compatible storage (Hetzner Object Storage endpoint
`https://<location>.your-objectstorage.com`). Create a dedicated private bucket and key; configure a lifecycle rule
(for example expire after 30-90 days) because the job never deletes remote objects. Local backups live on the same
host, so the off-host upload is what protects against losing the server: enable it before going live.

- Manual backup: `docker compose --env-file .env.prod -f compose.prod.yml exec backup /usr/local/bin/backup-service.sh once`
- Check logs: `docker compose ... logs backup` (a failed run prints `BACKUP FAILED`).
- **Restore drill** (weekly or at least monthly; a backup never restored is not a backup): `./backup/restore-prod.sh`
  restores the newest dump into a scratch database `handynet_restore_test`, prints sanity counts and drops it; it never
  touches the live database. For an uploaded copy, first download it into the `backups` volume.
- **Disaster restore:** new server, steps 1-4 with an empty volume, then stop the API
  (`docker compose ... stop api`), `dropdb`/`createdb handynet`, `pg_restore -d handynet --no-owner <dump>` as
  `handynet_owner` through `docker compose run --rm --entrypoint sh backup`, restore `files`/`dpkeys` from the tarball
  into their volumes, run `migrator`, `up -d`.
- Point-in-time recovery (WAL archiving with pgBackRest/WAL-G) is not part of this MVP baseline (RPO is up to 24 h).

The legacy host-cron scripts `deploy/backup/backup.sh` and `restore-test.sh` remain for the local `docker-compose.yml` stack.

## 7. Monitoring

- External uptime check (UptimeRobot, Better Stack or similar) on `https://<domain>/health/ready` every minute
  (200 = API and database reachable) with e-mail/push alert; optionally also `/` for the web tier.
- Watch disk usage (`df -h`, Hetzner alerting), `docker compose ... ps` (health states) and logs:
  `docker compose ... logs -f --tail=100 api`. Logs are JSON and rotated (5 x 10 MB per container).
- Set `OTEL_EXPORTER_OTLP_ENDPOINT` on the `api` service if you add a tracing backend later.

## 8. Notes and known follow-ups

- **Client IPs:** the API trusts `X-Forwarded-For/-Proto` only from the compose network `172.30.0.0/24`
  (`ForwardedHeaders__KnownNetworks__0`), where Caddy lives (`172.30.0.10`), so rate limits and the audit log see the
  real client address. If you change the subnet in `compose.prod.yml`, change that value too.
- **CSP:** Angular SSR inlines critical CSS and state scripts, so `script-src`/`style-src` include `'unsafe-inline'`.
  A nonce-based CSP is a possible hardening. `frame-ancestors 'none'`, HSTS (1 year), `nosniff`, Referrer-Policy and
  Permissions-Policy are set in `deploy/caddy/Caddyfile`. HSTS has no `preload`; add it only when you are sure.
- **GDPR / fonts:** `frontend/src/index.html` loads Inter from Google Fonts. Fetching fonts from Google servers
  from a German visitor's browser has been ruled a GDPR violation by courts (LG Munich 2022). Before go-live
  self-host Inter (npm `@fontsource/inter`) and drop the two Google hosts from the CSP.
- Sign a data processing agreement (AVV/DPA) with Hetzner and with your SMTP provider; keep SMTP in the EU.
- Regular OS patching: `unattended-upgrades` handles the host; update images by redeploying (base images are pulled on
  `deploy.sh`; `caddy:2-alpine` and `postgres:16-alpine` receive minor/patch updates on pull; PostgreSQL major upgrades
  are a manual dump/restore).

## 9. Alternatives compared (prices approximate - check current pricing)

| | Single VPS (recommended) | Render / Railway | Vercel |
|---|---|---|---|
| Fit | Whole stack as designed: API with background workers (outbox, e-mail, retention jobs), SSR web, Postgres, Caddy | Docker services (API, web) plus managed Postgres; workers run as always-on services | Angular frontend could work; the .NET API does not fit |
| Workers / long-lived processes | Yes | Yes (always-on instances, not free tier) | No: functions are short-lived/scale-to-zero, so outbox, e-mail delivery and retention jobs would need a separate host |
| Database | Own Postgres 16, full control of roles, RLS, extensions, backups | Managed Postgres; backups included on paid plans; you cannot always create roles with BYPASSRLS or run init scripts | External (Neon/Supabase/other) |
| EU / GDPR | Germany, one provider, simple AVV | EU regions exist (Render: Frankfurt; Railway: EU West/Amsterdam); verify per service | Edge network is global; functions can be pinned to an EU region; DB external |
| Ops effort | You patch, monitor and back up (this repo automates most) | Low | Low for frontend, high for the API workaround |
| Cost | About EUR 5-15/month for the whole stack | Roughly USD 20-60+/month once API + web + Postgres are paid instances | Free/Pro plan for frontend plus a separate API host and DB |

Database role requirement (matters for any managed Postgres): `postgres/01-roles.sh` runs
`CREATE ROLE handynet_app LOGIN NOSUPERUSER NOBYPASSRLS` and `GRANT CONNECT`; the migrator (owner role) creates schemas,
RLS policies and grants to `handynet_app`. A managed provider therefore needs: (a) permission to create a second login
role (most do: Render/Railway give you a superuser-like owner in your own database, Neon/Supabase allow `CREATE ROLE`),
and (b) `NOBYPASSRLS` on the app role, which is the default for newly created roles. What you must not do is connect the
API with the provider's default admin/owner user: owners bypass RLS unless `FORCE ROW LEVEL SECURITY` is set, and
superusers always do. On Render/Railway, run `01-roles.sh`-equivalent SQL once through `psql`, connect the migrator with
the owner URL and the API with the `handynet_app` URL. Verify with `DatabaseSecurityTests`-style checks before relying on it.
