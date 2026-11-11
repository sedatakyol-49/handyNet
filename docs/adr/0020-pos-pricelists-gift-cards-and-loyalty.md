# ADR-0020: Price lists, gift cards and loyalty points at the till

- Status: Accepted (backend implemented and tested; frontend built separately — **no tax or legal compliance claimed, to be reviewed by a tax adviser**, see "Not done / not verified")
- Date: 2026-11-11
- Extends: ADR-0008 (POS, line policy, receipts), ADR-0012 (fiscal process data, DSFinV-K)

## Context

The owner asked to finish the open POS roadmap items of Phase 2c D: customer-specific prices (price lists), gift cards
and loyalty points. Coupons and promotions are out of scope. All three touch the financial core — till prices and the
cashier's rights, payments, VAT, immutable receipts, storno and returns — so every rule must be enforced on the server,
inside the receipt transaction, and existing receipts and request fingerprints must not change.

## Decision

### Price lists (Preislisten)

- `pos.pricelists` (name unique per organization, `discount_basis_points` 0–10000, `is_active`, `valid_from`/`valid_to` as
  Europe/Berlin days, inclusive, `xmin`) and `pos.pricelist_items` (`(pricelist_id, product_id)` unique, gross
  `price_minor > 0`). Price lists are never deleted (customers and receipts reference them; deactivate instead); items
  are configuration (DELETE granted, audited). `pos.customers.pricelist_id` with a composite FK on the organization.
- Resolution for (product, customer): the customer's list, if active and valid today → its item price; else, with a
  general discount and a base price, `base − round_half_away(base × bp / 10000)` (never below 1 cent); else the base. The
  base is the existing chain (own POS price → own offer → none). The resolved price is **the till price** for
  `LinePolicy`: a cashier without `pos.price_override` sells at the list price to that customer — and only to that
  customer (selling at the list price to someone else, or at the regular price to the list customer, needs the right).
- `customerId` on the product grid, `GET /api/pos/prices/{productId}` and `GET /api/pos/lookup`; price DTOs gain
  `priceSource = "Pricelist"` and `basePriceMinor`.
- Assigning or removing a customer's price list needs `pos.manage` (it changes what the customer pays); a till user may
  still edit the customer as long as the list stays as it is.
- The receipt stores `pricelist_id` and prints "Preisliste: <name>" (snapshot in the document) when at least one line
  sold at the list's price.

### Gift cards (Mehrzweck-Gutschein)

- `pos.gift_cards`: `code_hash` (SHA-256 of the normalized code), `code_last4`, `initial_minor`, `balance_minor`,
  `status` (Active/Voided), issue data, `expires_on`, `customer_id`, `xmin`. `pos.gift_card_transactions` is append-only
  (Issue, Redeem, Restore, Void, Reissue; signed amount, balance after, receipt, reason, user).
- Codes: 16 characters from a 32-character alphabet without look-alikes (≈ 80 bits, cryptographic RNG), shown as
  `XXXX-XXXX-XXXX-XXXX`. **The code is never stored** — not in the card row, the receipt, the document, the audit log or
  a transaction. It is returned once, in `issuedGiftCards[].code` of the response that created it (sale, refund onto a
  gift card, reissue); replays and later reads return `code: null`. The receipt shows `Geschenkgutschein ••••-<last4>`.
- Selling: `SaleLineRequest.giftCardAmountMinor` creates a line without product (quantity 1, no discount, tax treatment
  `NotTaxable`, 0 % bucket of Kassenbeleg-V1, DSFinV-K business case `MehrzweckgutscheinKauf`), within
  `Pos:GiftCard:MinMinor`/`MaxMinor`; expiry 31 December of the issue year + `Pos:GiftCard:ValidityYears`. Gift card lines
  earn no points, take no points reduction and cannot be paid with a gift card. A receipt that sold a gift card cannot be
  invoiced (§14 invoice).
- Redeeming: `PaymentMethod.GiftCard` with `giftCardCode`. The card row is locked `FOR UPDATE` after the register and
  session locks (several cards in code-hash order), then: active, not expired, `amount ≤ balance`. It is **non-cash** for
  the TSE ("Unbar", card and gift card summed into one entry) and for cash rounding (only the cash part is rounded).
- Guarantees in the database: `0 ≤ balance ≤ initial` (CHECK), Voided ⇒ balance 0, identity/initial amount/expiry fixed
  and Voided final (trigger), transactions append-only (grant + trigger), and a **deferred constraint trigger checks at
  every commit that the balance equals the sum of the card's transactions** — a balance cannot move without a ledger row
  and vice versa. Concurrency is tested: 12 parallel sales on four registers against one card spend it exactly as far as
  the balance allows (the test fails when the domain check or the row lock is removed).
- Storno of a sale paid by gift card restores the amount onto the card (not onto a voided one: `pos.gift_card_not_usable`).
  Storno of the issuing receipt voids the card only if nothing was redeemed (`pos.gift_card_already_redeemed`). Gift
  card lines cannot be returned (`pos.gift_card_line_not_returnable`). Returns may refund onto a **new** gift card
  (`refundMethod: "GiftCard"`, code in the response once).
- Management (`pos.manage`): list/search (full code or last four), detail with history, void with reason, reissue
  (new code, same balance, old code dead). Lookup at the till (`pos.sell`) is rate-limited per user
  (`RateLimits:GiftCardLookupPerMinute`) and answers the same 404 for unknown and malformed codes.
- Offline: neither selling nor redeeming (`pos.offline_gift_card_not_allowed`, `pos.offline_payment_not_allowed`).
- Z-report: payment method `GiftCard` in `byPaymentMethod`, `giftCardsSoldCount`/`giftCardsSoldMinor` (net of storno).

### Loyalty points (Treuepunkte)

- `pos.loyalty_settings` (one row per organization; absent = disabled): `enabled`, `points_per_euro` 0–100,
  `point_value_minor` 1–100, `min_redeem_points`. `pos.loyalty_accounts` (per customer, `points ≥ 0` CHECK) and
  append-only `pos.loyalty_transactions` (Earn, Redeem, ReverseEarn, ReverseRedeem, Adjust). Same deferred
  "balance = Σ transactions" check as for gift cards.
- Redeeming (`CreateSaleRequest.redeemPoints`): programme enabled, a customer, at least the minimum, at most the balance
  (account row lock inside the sale transaction), value ≤ eligible total (all lines except gift cards, after line
  discounts). **A price reduction, not a payment**: the value is spread over the eligible lines pro rata (largest
  remainder, ties in line order) as `loyalty_discount_minor` per line; line gross, VAT and the §25a margin VAT are
  recomputed from the reduced gross. The line invariant becomes `line_gross = unit × qty − discount − loyalty_discount`
  (existing rows have 0). It is computed by the server and is therefore not the cashier's discount: no `pos.discount`
  right, no discount limit.
- Earning: `floor(eligible gross after all discounts / 100) × points_per_euro`, credited in the sale transaction.
- Storno gives redeemed points back and takes earned points back (at most the current balance; the receipt and the
  transaction record the actual figure). Returns take earned points back pro rata to the returned eligible gross
  (cumulative floor, so several partial returns never take more than was earned); redeemed points are not restored — the
  refund is the reduced line gross. Reversals work even if the programme was switched off meanwhile.
- Receipt: `loyaltyPointsRedeemed`, `loyaltyDiscountMinor`, `loyaltyPointsEarned`, `loyaltyBalanceAfter`, per line
  `loyaltyDiscountMinor`; printed as "Treuepunkte eingelöst / gesammelt / abgezogen / zurückgebucht" and "Punktestand".
  Invoices and e-receipts show the loyalty share together with the line discount. `CustomerDto.loyaltyPoints`;
  `GET /api/pos/customers/{id}/loyalty`; `POST …/loyalty/adjustments` (`pos.manage`, reason required, never below zero).
- Concurrency is tested (12 parallel redemptions on four registers against one balance; fails without the balance check).

### Fingerprints

New request fields enter the idempotency fingerprint only when present (`:g<amount>` per gift card line,
`:gc<normalized code>` per gift card payment, `#lp:<points>`), so fingerprints of existing requests are unchanged
(unit tests compare with the original formula).

## Consequences

- Receipts can now carry lines without a product (gift cards) besides invoice payments; reports that group by product
  skip them. The Kassenbeleg-V1 0 % bucket is used for the first time by sales.
- A gift card code that is lost cannot be recovered — only reissued.
- Three more append-only ledgers; corrections are new rows, never edits.

## Not done / not verified

- **Not reviewed by a tax adviser**: treating every gift card as a multi-purpose voucher (no VAT at sale, VAT when the
  goods are sold), its mapping to the 0 % bucket and to `MehrzweckgutscheinKauf` (the redemption is recorded as the
  payment type "Gutschein", not as a separate `MehrzweckgutscheinEinloesung` business case); loyalty redemptions as a
  price reduction that lowers VAT and the §25a margin; the expiry rule; voiding the remaining balance.
- Single-purpose vouchers (Einzweck-Gutscheine), coupons, promotions, discount tags; expiry notifications; partial
  refunds of a gift card balance in cash; transferring points between customers; points expiry.
- Customer data retention/anonymization does not yet cover loyalty history.
- Price lists per register or per date of sale in the past (prices are always resolved "today"); quantity breaks.
- Gift card redemption on invoices paid at the till (`pos.gift_card_not_allowed`).
- The frontend (management pages, till dialogs, voucher slip) is built separately and was not verified as part of this work.
