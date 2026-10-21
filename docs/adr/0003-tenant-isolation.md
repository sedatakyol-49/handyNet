# ADR-0003: Tenant isolation with verified tenant context and PostgreSQL Row-Level Security

- Status: Accepted
- Date: 2026-10-15

## Context

Independent businesses (wholesalers, shops, repair providers) share one database. A user can belong to several
organizations. Leaking one tenant's stock, prices or orders to another is a critical failure.

## Decision

1. **Tenant resolution.** The client names the organization in `X-Organization-Id`. `TenantResolutionMiddleware`
   accepts it only after confirming the authenticated user's membership in the database, then loads verification
   status, role and enabled modules into a scoped `TenantContext`. The header alone grants nothing.
2. **API authorization.** Endpoint filters: `RequireTenant`, `RequireModule(module)` (organization must be
   verified and the module enabled by the platform admin) and `RequirePermission(permission)` (derived from the
   membership role). Disabled modules are rejected server-side, not only hidden in the UI.
3. **Database enforcement.** On every connection open, EF sets `app.current_org`. Tenant-owned tables have RLS
   policies `organization_id = infra.current_org()`; orders are visible to buyer **or** seller. With no tenant,
   policies match nothing. The API connects as `handynet_app` (no superuser, no `BYPASSRLS`, not table owner),
   so the policies always apply.
4. **Cross-tenant operations** are narrow, audited database objects owned by the migration role:
   - `marketplace.offer_listings` (view): only published offers of verified sellers with `B2BSales`, only for a
     request that has a tenant → B2B offers are never visible to anonymous/B2C traffic.
   - `marketplace.reserve_offer_stock(offer, qty, ref)` (`SECURITY DEFINER`): buyer must be verified with
     `B2BPurchasing`; offer must be published; single conditional `UPDATE … WHERE on_hand - reserved >= qty`.
   - `inventory.release_reservation(id)`: only the stock owner or the reserving organization.
   - Marketplace moderation (added 2026-10-18, migration `MarketplaceModeration`): the listing view and
     `reserve_offer_stock` also exclude offers with `moderation = 'Blocked'` and offers of sellers with an active row
     in `marketplace.seller_blocks`. Platform-admin functions `admin_offers`, `admin_sellers`, `moderate_offers` and
     `moderate_seller` (`SECURITY DEFINER`) refuse to run for a tenant session (`infra.current_org()` must be NULL)
     and write `infra.audit_log` in the same statement. The app role may only read its own `seller_blocks` row; a
     trigger rejects any change of the moderation columns on `offers` (and any status change of a blocked offer)
     outside `moderate_offers`. `blocked_offers(ids)` only tells a caller which of the ids it already has are blocked.
   - Company administration (added 2026-10-21, ADR-0013, migration `PlatformAdministration`): `pos.admin_register_names(ids)`
     and `marketplace.admin_offer_counts(ids)` return only register names and offer counts and refuse tenant sessions;
     `tenancy.admin_remove_membership` is the only path that deletes a membership (active platform admin, reason, audited).
5. Identity/tenancy/catalog tables are not tenant-owned (users, memberships, organizations, canonical products)
   and are protected by application authorization instead.

## Consequences

- A forgotten `WHERE organization_id = …` in application code returns no foreign rows instead of leaking them
  (proven by `DatabaseSecurityTests`, including a mutation check during development).
- Every new tenant-owned table must get an RLS policy in its migration — part of the review checklist.
- Platform-admin reporting across tenants needs dedicated, audited read paths; ADR-0013 adds the ones for company and
  account administration (counts and names only). Tenants' operational data stays out of reach of the admin paths.
