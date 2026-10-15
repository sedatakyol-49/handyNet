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
