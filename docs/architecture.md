# Architecture

See the ADRs in [`docs/adr`](adr/) for the reasoning. This page is the map.

## Runtime

```mermaid
flowchart LR
    Browser["Angular app<br/>(SSR-ready)"] -->|HTTPS /api, JWT + X-Organization-Id| Api
    subgraph Api["HandyNet.Api (modular monolith)"]
        MW["Auth → TenantResolution → Authorization<br/>endpoint filters: tenant / module / permission"]
        Modules["Modules"]
        Outbox["Outbox dispatcher"]
    end
    Api -->|role handynet_app<br/>RLS enforced| PG[(PostgreSQL 16)]
    Migrator["HandyNet.Api migrate<br/>(schema owner)"] --> PG
```

## Module dependency map (implemented slice)

Arrows point from a module to what it may depend on. Enforced by project references and
`tests/HandyNet.ArchitectureTests`.

```mermaid
flowchart TD
    Disputes --> Ordering
    Payments --> Ordering
    Payments --> Ledger
    Shipping --> Ordering
    Shipping --> Catalog
    Notifications --> Ordering
    Notifications --> Tenancy
    Notifications --> Identity
    Ordering --> Marketplace
    Ordering --> Inventory
    Ordering --> Tenancy
    Imports --> Marketplace
    Imports --> Inventory
    Imports --> Catalog
    Marketplace --> Inventory
    Marketplace --> Catalog
    Marketplace --> Tenancy
    Inventory --> Catalog
    Tenancy --> Identity
    Identity --> Persistence
    Catalog --> Persistence
    Persistence --> SharedKernel
```

| Module | Schema | Owns | Public contract |
|---|---|---|---|
| Identity | `identity` | users, password hashing, lockout, JWT | `IUserAccounts` |
| Tenancy | `tenancy` | organizations, verification, memberships/roles, module activation, tenant resolution | `IOrganizationDirectory` |
| Catalog | `catalog` | canonical products, EAN/MPN identity, matching rules | `IProductCatalog`, `ProductMatcher` |
| Inventory | `inventory` | warehouses, stock levels, movements, reservations | `IInventory` |
| Marketplace | `marketplace` | supplier offers, B2B listing view, reservation function | `IOfferReservations`, `IOfferSync` |
| Imports | `imports` | CSV/XLSX parsing, mapping, preview (dry run), commit | – (top-level) |
| Ordering | `ordering` | B2B orders, idempotency, lifecycle incl. delivery + inspection window, completion job | `IOrderFulfilment` |
| Shipping | `shipping` | shipments, carrier adapters (Manual, Simulated), labels, tracking events, parcel/insurance/lithium rules, tracking sync job | – (top-level; uses `IOrderFulfilment`) |
| Payments | `payments` | payments via PSP adapter (Simulated today), webhooks (status re-fetched), commission, refunds, payouts after completion | – (top-level) |
| Disputes | `disputes` | buyer complaints within the inspection window, message thread, seller refund / buyer withdrawal / escalation, platform decision | – (top-level; uses `IOrderFulfilment`) |
| Ledger | `ledger` | immutable double-entry journal; single write path `ledger.post_entry`; tenant-visible own accounts | `ILedger` |
| Notifications | `notifications` | email queue fed by outbox events (order placed/status changed, organization verified), SMTP delivery with retry | – (consumes events only) |
| Persistence | `infra` | DbContext, RLS session interceptor, audit log, outbox | `IModuleModel` |

### Planned modules and where they attach

| Planned module | Depends on |
|---|---|
| Pricing & Ankauf | Catalog, Tenancy (versioned price lists, approval workflow) |
| POS / Kassensystem | Inventory (same reservation path), Catalog, Payments, Fiscal (fiskaly TSE adapter) |
| Repairs | Catalog, Inventory (parts consumption), Ordering (parts orders), Shipping |
| Invoicing & Accounting | Ordering, Payments, POS (XRechnung/ZUGFeRD, DATEV export) |
| Returns & Warranty (return shipments, restocking, partial refunds) | Disputes, Shipping, Inventory, Payments |
| B2C Marketplace | Catalog, Marketplace (separate B2C visibility), Payments |

## Request pipeline

1. JWT bearer authentication (`sub`, `hn_admin` claims).
2. `TenantResolutionMiddleware`: if `X-Organization-Id` is present, verify membership in the DB, load status,
   role and modules into `TenantContext`.
3. Endpoint filters: `RequireTenant` → `RequireModule` (verified + enabled) → `RequirePermission`.
4. EF `TenantSessionInterceptor` sets `app.current_org` on every connection; RLS does the rest.
5. `SaveChanges` interceptors write the audit log and the outbox in the same transaction.

## Errors

RFC 7807 problem details with a stable machine-readable `code` (`order.insufficient_stock`,
`module.disabled`, …). 422 = validation, 409 = conflict/state, 403 = tenant/module/permission, 404 = not found
(also used for other tenants' resources, so existence is not revealed).
