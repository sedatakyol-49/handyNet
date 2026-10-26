# ADR-0019: Platform IMEI blacklist and serialized device history

- Status: Accepted (own list only; no external lost/stolen database is connected — see "External databases")
- Date: 2026-10-26

## Context

Serialized stock (IMEI units) enters a tenant's stock through two paths today: an Ankauf intake (bought from a private
person, §25a) and the Lager entry "Artikel anlegen" (`POST /api/inventory/items`, single or bulk, one IMEI per device,
idempotent, all or nothing). Devices leave stock through POS sales and sales invoices and come back through POS returns
and credit notes. A platform that trades used phones must not let a device that is reported lost or stolen be bought,
stocked or sold, and dealers need to see each device's life (receipt, sale, return). HandyNet has no contract with an
external lost/stolen database (e.g. GSMA Device Check).

An earlier, unmerged branch (2026-10-16) added a separate serialized goods receipt (`/api/inventory/goods-receipts`,
table `inventory.goods_receipts`). Main has since gained the Lager entry with IMEIs, which covers the same need (N
devices of one product by IMEI, warehouse, purchase price, idempotency key, all or nothing). That part is **not** ported;
its useful behaviour (every offending IMEI reported at once) is added to the Lager entry instead.

## Decision

### Platform IMEI blacklist

- `inventory.imei_blacklist` is **platform data, not tenant-owned** (no `organization_id`): IMEI, reason
  (Lost/Stolen/Fraud/Other; Other needs a note), note, source of the report, created by/at, deactivated by/at/reason.
  At most one *active* entry per IMEI (partial unique index `ux_imei_blacklist_active`). Entries are never deleted (no
  DELETE/TRUNCATE grant, trigger); the only allowed update is a one-time deactivation with a reason. Blocking again
  creates a new entry, so the history is complete. Every block and lift also writes an `AdminImeiBlock` /
  `AdminImeiUnblock` entry to the audit log (with the admin's note or reason).
- **Who sees it.** RLS policy `platform_admin_only` admits rows only when the session variable `app.platform_admin` is
  `on` (`infra.is_platform_admin()`). `TenantSessionInterceptor` sets it on every connection, `on` only for an
  authenticated platform admin. Tenant code paths therefore read zero rows even with an unfiltered query (integration
  test). As with `app.current_org`, the application role could set the variable itself; RLS guards against mistakes in
  tenant code, the endpoint policy (`RequirePlatformAdmin`) is the authorization.
- Tenants only get yes/no through `SECURITY DEFINER` functions: `inventory.imei_blacklisted(text)` and the batch form
  `inventory.imeis_blacklisted(text[])`, plus `inventory.imeis_in_stock(text[])` next to the existing `imei_in_stock`.
  Responses never contain the reason, note, source or another organization.
- **Enforcement in one place plus a database backstop.**
  - `IInventory.StageReceiveUnitAsync` refuses a blacklisted IMEI with `409 imei.blacklisted` (before the in-stock
    check). Ankauf completion and the Lager entry both go through it. `StageSellUnitAsync` (POS sale, sales invoice) and
    `StageReturnUnitAsync` (POS return/Storno, credit note) apply the same check, so a device reported after it was
    stocked can neither be sold nor come back into stock.
  - The Ankauf quote (with IMEI) and the intake **Received** step refuse a blacklisted IMEI with `imei.blacklisted`
    before any money changes hands; the intake page shows a dedicated warning.
  - The POS lookup of a blacklisted own unit and a sales-document line with such an IMEI answer `409 imei.blacklisted`,
    so the cashier sees it at scan time.
  - The Lager entry checks all IMEIs of a request at once (`IInventory.FindImeiConflictsAsync`) and refuses with every
    offending IMEI in `imeis: [{ imei, code }]` (`imei.blacklisted`, `unit.imei_in_stock`): code `entry.imei_conflicts`
    for several, the specific code (with `imei`) for one. Nothing is booked.
  - Trigger `stock_units_not_blacklisted` (`BEFORE INSERT OR UPDATE OF status` on `inventory.stock_units`) raises
    SQLSTATE `HNB01` when a blacklisted IMEI would become InStock/Reserved/Sold, whatever the code path. Callers map
    `HNB01` (`DbErrors.IsImeiBlacklisted`) to `imei.blacklisted`.
- Admin API (`/api/admin/imei-blacklist`, platform admins only): `GET` list/search (IMEI prefix, active/inactive,
  paging), `POST` add, `POST /{id}/deactivate`. Admin UI: Administration → "IMEI-Sperrliste" (platform nav section).

### Device history

- `inventory.stock_movements.unit_id` (nullable FK to `stock_units`) links the unit receipt, sale and return movements
  to their unit. Units and movements created before this change have no link (their history is empty; the receipt date
  is shown instead).
- `GET /api/inventory/units` (filter product, status, IMEI prefix, warehouse; paging) and
  `GET /api/inventory/units/{id}` (unit + history) — module Inventory, permission `inventory.manage` (purchase prices).
  UI: "Geräte (IMEI)" (`/app/lager/geraete`), cards on phones, a table from `md`.

### External databases

`IExternalImeiCheck` is the adapter port for an external lost/stolen database; `StageReceiveUnitAsync` asks it after the
own checks. The only implementation is `DisabledExternalImeiCheck` (always `NotChecked`; it never blocks and never
claims a device is clean). **No external database (GSMA or other) is connected or verified.** A real adapter needs a
contract, credentials and verification against the provider's sandbox before it is registered.

## Consequences

- A blacklisted device is refused at every entry and exit of stock that exists today. Selling serialized units via B2B
  offers does not exist; when it is built it must use the same `IInventory` paths (the trigger also covers it).
- Devices already in stock when they are blacklisted stay visible in the dealer's stock but cannot be sold; what happens
  with them next is a manual process outside HandyNet for now. No statement is made about legal duties around reported
  devices.
- Dropped from the old branch: the separate goods-receipt table and endpoints, and §25a margin scheme with a reason for
  supplier receipts (the Lager entry books devices with regular VAT; Ankauf books §25a).
