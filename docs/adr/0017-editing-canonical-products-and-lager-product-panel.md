# ADR-0017: Editing canonical products and the Lager product panel

- Status: Accepted
- Date: 2026-10-25

## Context

The product owner asked that images can be added to a product selected in the Lager, and that price and product data
can be edited while the product is in stock. Products are canonical catalog data shared by all tenants (ADR-0004);
images already follow the rule "only the creating organization (or a platform administrator)" (ADR-0011). Prices are
not product data: every organization has its own marketplace offer (net price, MOQ, delivery days, condition, status)
and its own POS price. There was no endpoint to change a product after it was created.

## Decision

1. **`PUT /api/catalog/products/{id}`** — full replacement of brand, name, category, quality, EAN, MPN, model and
   compatible models. Module `ProductImport` + permission `imports.manage` (same as creating products and managing
   images); only the organization in `products.created_by_organization_id` (`Product.CanEdit`), otherwise 403
   `product.not_owner`. Products without a creator (platform data) are not editable through this endpoint.
2. **Same validation as creating** (`Product.Update` shares `Normalize` with `Product.Create`: lengths, EAN check digit,
   identifier required, ≤ 100 compatible models). In addition a category change must keep the stock mode
   (`SerializedStock.RequiresImei/AllowsImei` equal), otherwise 422 `product.category_stock_mode`: existing stock of any
   organization (IMEI units vs. quantities) would no longer fit the product.
3. **Identity collisions are refused, never merged:** a new EAN that belongs to another product → 409
   `product.ean_taken`; a new brand + MPN key + quality that belongs to another product → 409 `product.identity_taken`
   (both with `productId` of the other product). The unique indexes catch concurrent races with the same codes.
4. **Optimistic concurrency:** `GET /api/catalog/products/{id}` returns `version` (xmin) plus
   `createdByCurrentOrganization` and `canEdit`; the PUT must send `version` (422 without it, 409 `concurrency` when
   stale). An unchanged body returns 200 without writing.
5. **Audit:** `Product` is `IAuditable`; every change is an audit entry with from/to values and the acting
   organization.
6. **RLS on `catalog.products`** (migration `CatalogProductOwnerUpdates`): everyone reads; a tenant session inserts only
   products created by itself and updates only products it created, and cannot hand a product to another organization
   (WITH CHECK); sessions without a tenant are unrestricted; no DELETE/TRUNCATE grant.
7. **Read helpers for the panel:** `GET /api/inventory/stock?productId=` (one product in every own warehouse) and
   `GET /api/offers?productId=` (own offers of one product).
8. **Lager product panel (frontend):** a stock row (click, Enter/Space on the focused row, or "Details") opens a dialog
   with Bilder (manager when `canEdit`, read-only gallery otherwise), Artikeldaten (form or read-only), Preise & Angebot
   (EK of the latest goods receipt, own POS price with module Pos, own offer with module B2BSales) and Bestand (per
   warehouse, recent movements, Korrigieren). Read-only sections explain why (another company's shared catalog
   product, or missing module/permission).

## Consequences

- A creator's edits are visible to every organization that stocks or offers the product (intended: one canonical
  catalog). Wrong edits by the creator affect others; the audit log records who changed what. A moderation/claim
  process for disputed product data is future work.
- Organizations other than the creator cannot correct shared product data themselves; they keep their own prices,
  offers and stock.
- Category changes between IMEI and quantity stock modes require a new product.
