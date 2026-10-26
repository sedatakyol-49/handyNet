# Operations

## Database roles

| Role | Used by | Rights |
|---|---|---|
| owner (`postgres` locally, `handynet_owner` in Compose) | migrations only | owns all objects, DDL |
| `handynet_app` | the API | `SELECT/INSERT/UPDATE` on app tables, no `DELETE` (except module toggles), no `UPDATE` on audit log / stock movements, `EXECUTE` on reservation functions; RLS enforced; not superuser, no `BYPASSRLS` |

The migration creates `handynet_app` as `NOLOGIN` if it does not exist. Give it a login per environment:

```sql
ALTER ROLE handynet_app LOGIN PASSWORD '<secret>';
GRANT CONNECT ON DATABASE handynet TO handynet_app;
```

## Secrets

Never in git. Locally: `dotnet user-secrets` (project `HandyNet.Api`). Containers: environment variables from a
`.env` file that is git-ignored (template: `.env.example`) or the platform's secret store.

| Key | Purpose |
|---|---|
| `ConnectionStrings:HandyNet` | API connection as `handynet_app`. Set `Maximum Pool Size` so that (API instances × pool size) plus the migration and admin connections stays below PostgreSQL's `max_connections`. Otherwise the server refuses connections under load (53300), and the API answers 503 `service.busy` |
| `ConnectionStrings:HandyNetMigrations` | owner connection for `migrate` |
| `Jwt:SigningKey` | ≥ 32 random bytes |
| `Bootstrap:AdminEmail` / `Bootstrap:AdminPassword` | creates/promotes the platform admin at startup (idempotent) |
| `FileStorage:S3:AccessKey` / `FileStorage:S3:SecretKey` | object storage credentials when `FileStorage:Provider` = `S3` (or leave empty for the AWS default credential chain) |
| `Pos:Fiskaly:Credentials:{name}:ApiKey` / `…:ApiSecret` | fiskaly SIGN DE credentials referenced by name from an organization's fiscal configuration (alternative to entering them in the admin UI, where they are stored Data-Protection-encrypted) |

## POS fiscal layer (ADR-0012)

Not certified and not reviewed by a tax adviser. Non-secret settings:

| Key | Default | Purpose |
|---|---|---|
| `Pos:FiscalSigner` | unset (`Disabled` in Development/tests) | default fiscal mode for organizations without their own configuration: `Disabled` or `SimulatedTse`. Unset + no configuration = sales refused. `Disabled` is refused in Production; `SimulatedTse` too unless the staging flag is set; `FiskalyCloudTse` is configured per organization only |
| `Pos:Fiscal:AllowSimulatedTseInProduction` | `false` | **staging only**: allows the simulated test TSE in the Production environment (startup check, admin API and sale time) |
| `Pos:Fiscal:MaxAttempts` | `3` | attempts per TSE call (start/finish, same idempotency key) before the sale continues as a TSE outage |
| `Pos:Fiscal:RetryDelayMs` | `200` | pause between attempts (× attempt number) |
| `Pos:Fiskaly:BaseUrl` | `https://kassensichv-middleware.fiskaly.com/api/v2` | fiskaly SIGN DE v2 base URL (https only) |
| `Pos:Fiskaly:TimeoutSeconds` | `10` | HTTP timeout per fiskaly call |
| `Pos:Fiskaly:MaxRetries` | `2` | HTTP retries on timeouts, 429 and 5xx |
| `DataProtection:KeysPath` | unset (default key ring location of the host) | directory of the ASP.NET Data Protection key ring; **set it in every multi-instance or container deployment** (e.g. a volume) and back it up — without it stored fiskaly credentials and simulated TSE keys become unreadable after a restart |

The platform admin chooses the mode per organization (and optionally per register) under Administration → "Kassen &
TSE" (`/api/admin/pos/fiscal`). A TSE outage never blocks sales: receipts carry "Sicherheitseinrichtung ausgefallen",
outages are listed there and in `pos.fiscal_outages`. DSFinV-K: `GET /api/pos/exports/dsfinvk?registerId=…&from=…&to=…`
(`pos.manage`).

## File storage (ADR-0011)

- `FileStorage:Provider` = `Local` (default; `FileStorage:Local:RootPath`, Compose volume `files` at `/data/files`)
  or `S3` (`FileStorage:S3:ServiceUrl`, `Region`, `Bucket`, optional `KeyPrefix`, `ForcePathStyle`). Startup fails if
  the S3 settings are incomplete.
- Back up the `files` volume (or enable bucket versioning) together with the database: image rows without files
  return 404. The S3 adapter has not been verified against a real bucket yet — test uploads/serving/deletes once
  against the Hetzner bucket before switching production to it.

## Sessions and account emails (ADR-0010)

Non-secret settings (defaults in `appsettings.json`):

| Key | Default | Purpose |
|---|---|---|
| `Jwt:AccessTokenMinutes` | 15 | access token lifetime; the web app refreshes shortly before expiry and on 401 |
| `Jwt:RefreshTokenDays` | 14 | refresh token lifetime, renewed on every rotation |
| `Identity:PasswordResetTokenMinutes` | 60 | reset link lifetime |
| `Identity:EmailVerificationTokenHours` | 48 | verification link lifetime |
| `RateLimits:AuthPerMinute` | 20 | per IP: login, registration, change password, reset confirm, verify |
| `RateLimits:RefreshPerMinute` | 60 | per IP: refresh, logout |
| `RateLimits:AccountEmailsPerHour` | 10 | per IP: reset request, verification resend |
| `Notifications:PublicBaseUrl` | `http://localhost:4200` | base of the links in emails (`/email-bestaetigen#token=…`, `/passwort-zuruecksetzen#token=…`) — set to the public web app URL in every environment |

Rate limits partition by the connection's remote IP. Behind a reverse proxy, configure forwarded headers first, or all
clients share one bucket. Without `Notifications:Smtp:Host` emails (including reset links) are only logged — fine for
development, never for production. Support: a user's sessions can be ended with `POST /api/auth/logout-all` (as the
user) or by a password reset; `identity.refresh_tokens.revocation_reason = 'Reuse'` marks a detected token theft
(also logged as a warning).

## Platform administration of companies and users (ADR-0013)

Administration → "Unternehmen" / "Benutzer" (`/api/admin/organizations`, `/api/admin/users`). Support cases are handled
there: lock an account (all sessions end at once; reason `AccountDisabled` on its refresh tokens), lift a lockout, end
all sessions (`AdminRevoked`), send a reset or verification link, fix a display name, change roles or transfer the
ownership of a company. Admins see contract and account data only — no receipts, customers, sales or Ankauf seller data.

- Audit: admin writes are `infra.audit_log` rows with `action LIKE 'Admin%'` and `changes->>'Reason'`; opening a detail
  view is an `AdminRead` row (who looked at which company/account and when), e.g.
  `SELECT occurred_at, user_id, entity_type, entity_id, action, changes FROM infra.audit_log WHERE changes->>'Source' = 'platform' ORDER BY id DESC;`
- The database refuses to remove the last owner of a company (`ck_memberships_owner_required`) and the last active
  platform admin (`ck_users_last_platform_admin`), also for manual SQL. The `Bootstrap:AdminEmail` account gets the
  platform-admin role back at every API start; it is not re-enabled automatically — if it was disabled, enable it with
  the owner connection (`UPDATE identity.users SET is_active = true, disabled_at = NULL, disabled_reason = NULL WHERE email = '…'`).

## Deploying a new version

1. Take a backup (`deploy/backup/backup.sh`).
2. Run migrations: `docker compose run --rm migrator` (or `dotnet HandyNet.Api.dll migrate`).
3. Roll out the API. Migrations must be backward compatible with the previous API version (expand/contract).

## Backups and restore

- Baseline: nightly `pg_dump -Fc` with archive verification and 14-day retention (`deploy/backup/backup.sh`),
  copied off-site (Hetzner Storage Box / Object Storage).
- Point-in-time recovery: enable WAL archiving with pgBackRest or WAL-G to object storage (planned for the
  Hetzner production setup; not configured in Compose).
- Restore drill weekly: `deploy/backup/restore-test.sh` restores the newest dump into a scratch database and
  runs sanity queries. A failed drill is an incident.

## Health and telemetry

- `GET /health/live` (process), `GET /health/ready` (database).
- Structured logs via Serilog (console). Traces via OpenTelemetry; set `OTEL_EXPORTER_OTLP_ENDPOINT` to export.

## Personal data (GDPR)

`ankauf.intakes.seller` holds sellers' names, addresses, dates of birth and ID document numbers, collected for the
second-hand trade record-keeping duty. It is tenant-isolated (RLS), never exposed to other organizations and not copied
into the audit log (`[PersonalData]`). A daily job anonymizes it once `Ankauf:SellerDataRetentionYears` have passed since
completion (ADR-0009). **The setting is null (disabled) by default** because the legal period must be confirmed by legal
review; the API logs a warning at startup in Production while it is unset. Backups keep anonymized data until they age out.

## Commission invoices

Set `Invoicing:Issuer` (name, legal name, address, VAT id, tax number) before go-live — in Production the shipped
`PLATZHALTER` values make every issuing attempt fail with `invoicing.issuer_not_configured`. The job issues the previous
month automatically (`Invoicing:AutoIssue`, checked every `Invoicing:AutoIssueCheckMinutes`); a platform admin can
(re-)issue any completed month with `POST /api/admin/invoices/issue {"month":"YYYY-MM"}` — it is idempotent.
`Invoicing:VatRateBasisPoints` must equal `Payments:CommissionVatBasisPoints`.
