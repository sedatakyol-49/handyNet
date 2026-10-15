# ADR-0002: One DbContext, module-contributed models, explicit migrations

- Status: Accepted
- Date: 2026-10-15

## Decision

- A single `HandyNetDbContext` is assembled from `IModuleModel` registrations. Each module maps only tables in
  its own schema. Cross-module foreign keys are declared **without navigations** (referential integrity, no object
  coupling).
- All migrations live in `HandyNet.Migrations` and are versioned in git. Hand-written SQL (RLS, grants, views,
  functions) lives in dedicated migrations with an `Up` *and* a `Down`.
- Migrations are applied by an explicit deployment step (`HandyNet.Api migrate`, or the `migrator` service in
  Docker Compose) using the **schema-owner** connection. The running API uses the restricted `handynet_app`
  role and cannot run DDL.
- CI fails if the EF model has changes without a migration (`dotnet ef migrations has-pending-model-changes`).

## Rules for data safety

- Never edit a migration that has been applied anywhere; add a new one.
- Destructive changes (drop/rename column, type narrowing) use expand → migrate data → contract over separate
  releases; a backup is taken immediately before deploying them.
- Enum values are stored as text; renaming an enum member requires a data migration.
- Historical financial and stock records are append-only (`UPDATE`/`DELETE` revoked from the app role).

## Consequences

Atomic cross-module writes (e.g. order + reservations) without distributed transactions. The cost is one large
model; acceptable for a modular monolith.
