# ADR-0004: Money, stock reservations and canonical product identity

- Status: Accepted
- Date: 2026-10-15

## Money

- All amounts are `long` **euro cents** (`*_minor` columns, `Money` value object). No `float`/`double` anywhere
  near money (architecture test). Input with sub-cent precision is rejected, never rounded silently.
- VAT is computed once per order on the net total with `MidpointRounding.AwayFromZero`; B2B prices are net.
  Only the 19 % standard rate exists so far. Reduced rate, §25a UStG margin scheme, reverse charge and
  invoice/GoBD requirements are **not implemented** and no compliance is claimed.
- Order lines snapshot SKU, product name and unit price at placement; the client may send the price it saw
  (`expectedUnitPriceMinor`) and gets `409 order.price_changed` instead of a silently different price.

## Stock

- `inventory.stock_levels` holds `on_hand` and `reserved` per warehouse/product, with CHECK constraints
  `on_hand >= 0` and `0 <= reserved <= on_hand`. Every `on_hand` change writes an append-only
  `inventory.stock_movements` row with the resulting quantity.
- Reservations are created by one conditional `UPDATE` inside the order transaction, so concurrent checkouts
  cannot oversell (25 parallel orders against 5 units → exactly 5 succeed, tested). Shipping consumes the
  reservation; cancel/reject releases it. Optimistic concurrency (`xmin`) protects other updates.
- POS and B2C will use the same reservation path when built.
- Serialized units (IMEI) are **not** implemented yet; the planned model is a `stock_units` table with a partial
  unique index on IMEI for units in sellable states, so one device cannot be sold twice.

## Catalog identity

- Canonical products are shared across tenants; supplier offers (SKU, price, condition, delivery time,
  warehouse) are separate rows in `marketplace.offers`. Supplier inventory stays in the supplier's tenant.
- Identity: unique EAN/GTIN (check digit validated), and unique `(brand, MPN, part quality)`. Part quality
  (Original / ServicePack / Refurbished / Aftermarket) is part of identity, so an aftermarket copy never merges
  into an original part.
- Matching is conservative: identifiers that disagree (EAN of another brand/quality/MPN, MPN with a different
  EAN) produce a **conflict for review**, never a merge.
