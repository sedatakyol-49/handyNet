# ADR-0004: Money, stock reservations and canonical product identity

- Status: Accepted
- Date: 2026-10-15

## Money

- All amounts are `long` **euro cents** (`*_minor` columns, `Money` value object). No `float`/`double` anywhere
  near money (architecture test). Input with sub-cent precision is rejected, never rounded silently.
- VAT is computed once per order on the net total with `MidpointRounding.AwayFromZero`; B2B prices are net.
  Only the 19 % standard rate exists so far. Reduced rate, reverse charge and B2B invoice/GoBD requirements are
  **not implemented** and no compliance is claimed. (POS receipts: gross prices, §25a margin scheme and immutable
  receipts — ADR-0008.)
- Order lines snapshot SKU, product name and unit price at placement; the client may send the price it saw
  (`expectedUnitPriceMinor`) and gets `409 order.price_changed` instead of a silently different price.

## Stock

- `inventory.stock_levels` holds `on_hand` and `reserved` per warehouse/product, with CHECK constraints
  `on_hand >= 0` and `0 <= reserved <= on_hand`. Every `on_hand` change writes an append-only
  `inventory.stock_movements` row with the resulting quantity.
- Reservations are created by one conditional `UPDATE` inside the order transaction, so concurrent checkouts
  cannot oversell (25 parallel orders against 5 units → exactly 5 succeed, tested). Shipping consumes the
  reservation; cancel/reject releases it. Optimistic concurrency (`xmin`) protects other updates.
- POS (ADR-0008) applies the same availability rule: only `on_hand − reserved` can be sold over the counter, so a B2B
  reservation is never sold at the till. B2C will use the same reservation path when built.
- Serialized units (IMEI) live in `inventory.stock_units` with a partial unique index on IMEI for units in stock
  (InStock/Reserved) platform-wide (ADR-0007), so one device cannot be stocked twice; selling marks the unit Sold under
  optimistic concurrency, so it cannot be sold twice either (ADR-0008).

## Catalog identity

- Canonical products are shared across tenants; supplier offers (SKU, price, condition, delivery time,
  warehouse) are separate rows in `marketplace.offers`. Supplier inventory stays in the supplier's tenant.
- Identity: unique EAN/GTIN (check digit validated), and unique `(brand, MPN, part quality)`. Part quality
  (Original / ServicePack / Refurbished / Aftermarket) is part of identity, so an aftermarket copy never merges
  into an original part.
- Matching is conservative: identifiers that disagree (EAN of another brand/quality/MPN, MPN with a different
  EAN) produce a **conflict for review**, never a merge.
