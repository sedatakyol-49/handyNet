# HandyNet

Multi-tenant B2B platform for the German mobile phone, electronics, wholesale and repair industry.
Modular monolith: ASP.NET Core (.NET 10) + PostgreSQL 16, Angular frontend.

**Current state:** first vertical slice — business registration and verification, per-tenant modules, canonical
catalog, CSV/XLSX supplier import with preview and approval, supplier offers and inventory, B2B browsing, and
B2B orders with transactional stock reservation. See [docs/roadmap.md](docs/roadmap.md) for what is and is not
built. Nothing here claims fiscal or legal compliance.

## Repository

```
backend/                 .NET solution (HandyNet.slnx)
  src/HandyNet.Api           composition root, `migrate` command
  src/HandyNet.SharedKernel  Result/Error, Money, tenancy abstractions, endpoint filters
  src/HandyNet.Persistence   DbContext, RLS session interceptor, audit log, outbox
  src/HandyNet.Migrations    EF migrations (+ hand-written RLS/grants/functions)
  src/Modules/*              Identity, Tenancy, Catalog, Inventory, Marketplace, Imports, Ordering
  tests/                     Unit, Integration (real PostgreSQL), Architecture
frontend/                Angular app
deploy/                  PostgreSQL init script, backup/restore scripts
docs/                    ADRs, architecture & module map, data model, roadmap, operations
```

## Local development (Windows, no Docker)

Requirements: .NET SDK 10, Node 22, PostgreSQL 16 on `localhost:5432`, `dotnet-ef` tool.

```powershell
# 1. Database + application role (once). Use your own passwords.
psql -U postgres -c "CREATE DATABASE handynet"
psql -U postgres -c "CREATE ROLE handynet_app LOGIN PASSWORD '<app-password>'"

# 2. Secrets (stored outside the repo)
cd backend
dotnet user-secrets --project src/HandyNet.Api set "ConnectionStrings:HandyNet" "Host=localhost;Database=handynet;Username=handynet_app;Password=<app-password>"
dotnet user-secrets --project src/HandyNet.Api set "ConnectionStrings:HandyNetMigrations" "Host=localhost;Database=handynet;Username=postgres;Password=<postgres-password>"
dotnet user-secrets --project src/HandyNet.Api set "Jwt:SigningKey" "<at least 32 random characters>"
dotnet user-secrets --project src/HandyNet.Api set "Bootstrap:AdminEmail" "admin@handynet.local"
dotnet user-secrets --project src/HandyNet.Api set "Bootstrap:AdminPassword" "<admin password>"

# 3. Migrate and run (API on http://localhost:5170, OpenAPI at /openapi/v1.json)
dotnet run --project src/HandyNet.Api -- migrate
dotnet run --project src/HandyNet.Api

# 4. Frontend (http://localhost:4200) — see frontend/README.md
cd ../frontend; npm install; npm start
```

> Windows **Smart App Control** blocks freshly compiled, unsigned DLLs at random. On a development machine it
> must be turned off (Windows Security → App & browser control), otherwise builds succeed but the app/tests
> fail to load assemblies.

### Tests

```powershell
cd backend
dotnet test HandyNet.slnx
```

Integration tests need a PostgreSQL superuser connection. They look for, in order: env
`HANDYNET_TEST_PG_ADMIN`, `tests/HandyNet.IntegrationTests/testsettings.local.json`
(`{ "AdminConnection": "Host=localhost;Username=postgres;Password=…;Database=postgres" }`, git-ignored), or Docker
(Testcontainers). Every run uses a fresh throw-away database and runs the API as an RLS-restricted role.

If the API is running from `bin/`, build tests elsewhere to avoid file locks: `dotnet test HandyNet.slnx --artifacts-path ../.artifacts`.

## Docker Compose

```sh
cp .env.example .env   # set real secrets
docker compose up --build
# web: http://localhost:8080   (Angular SSR server; proxies /api to the API)
```

`migrator` applies migrations as the owner, then `api` starts as `handynet_app`. Compose has not yet been run
in this repository's development environment (no Docker available) — treat it as unverified until CI or a
Docker host runs it.

## Documentation

- [Architecture & module dependency map](docs/architecture.md)
- [Data model](docs/data-model.md)
- [ADRs](docs/adr/)
- [Roadmap & known limitations](docs/roadmap.md)
- [Operations: roles, secrets, deploy, backup/restore](docs/operations.md)
