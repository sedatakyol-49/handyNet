# ADR-0001: Modular monolith with Clean Architecture per module

- Status: Accepted
- Date: 2026-10-15

## Context

HandyNet will eventually cover ~20 business domains (identity, catalog, imports, inventory, B2B/B2C marketplace,
POS, repairs, shipping, payments, invoicing, …). The team is small, the product is new, and many workflows cross
domains inside a single business transaction (e.g. *place order + reserve stock*). Financial correctness and data
integrity matter more than independent deployability.

## Decision

- One deployable ASP.NET Core application (`HandyNet.Api`) composed of **module projects**
  (`HandyNet.Modules.*`). No microservices.
- Each module owns its **PostgreSQL schema** (`identity`, `tenancy`, `catalog`, `inventory`, `marketplace`,
  `imports`, `ordering`), and is organised in folders `Domain` / `Application` / `Infrastructure` / `Contracts`.
- Modules talk to each other **only through `Contracts`** (public interfaces/DTOs) and, for shared identifiers,
  public domain enums/records. `Application` and `Infrastructure` types are `internal`.
- Dependency direction is fixed (see `docs/architecture.md`) and enforced by `HandyNet.ArchitectureTests`
  (NetArchTest) and by project references. Cycles are impossible at compile time.
- Domain code has no dependency on EF Core, ASP.NET Core or Npgsql (also enforced by tests).
- CQRS is used selectively: reads that span modules use SQL views/read models (e.g. `marketplace.offer_listings`);
  no mediator library.
- Integration events go through a **transactional outbox** (`infra.outbox_messages`), written in the same
  transaction as the state change and dispatched with `FOR UPDATE SKIP LOCKED` (at-least-once; handlers must be
  idempotent).

## Consequences

- Cross-module transactions are simple and ACID (one `DbContext`, see ADR-0002).
- A module can later be extracted behind its `Contracts` if scaling requires it; schemas already separate data.
- Discipline is required: cross-schema SQL (views, functions, FKs) is allowed only in the direction of the
  dependency map and must be documented in the migration.
