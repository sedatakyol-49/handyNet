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
| `ConnectionStrings:HandyNet` | API connection as `handynet_app` |
| `ConnectionStrings:HandyNetMigrations` | owner connection for `migrate` |
| `Jwt:SigningKey` | ≥ 32 random bytes |
| `Bootstrap:AdminEmail` / `Bootstrap:AdminPassword` | creates/promotes the platform admin at startup (idempotent) |
| `FileStorage:S3:AccessKey` / `FileStorage:S3:SecretKey` | object storage credentials when `FileStorage:Provider` = `S3` (or leave empty for the AWS default credential chain) |

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
| `Jwt:AccessTokenMinutes` | 60 | access token lifetime; lower to 15 once the web app refreshes tokens |
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
