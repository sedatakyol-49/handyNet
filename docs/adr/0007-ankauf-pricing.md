# ADR-0007: Ankauf price lists, versioned pricing policy and preliminary quotes

- Status: Accepted (business parameters are sample values pending confirmation)
- Date: 2026-10-15

## Decision

- **Monthly list** (XLSX, sheet "Mobilfunk" by default; header row auto-detected; German/English column names).
  The list price is the reference price for condition **Gut**. Rows without a usable price are reported as skipped.
- **Approval rules** per model, compared with the most recent earlier published price:
  new model → approval; any increase → approval; decrease > 30 % → approval; decrease ≤ 30 % or unchanged →
  automatic. A list can only be published when no entry is pending (approve or reject each).
- **Immutable price versions.** Publishing turns accepted/approved entries into `price_versions` rows; they are never
  edited (trigger-protected). **Only the current list counts**: the most recently starting published list whose
  validity covers today. A model missing from it, or whose new price was rejected, has *no* price — prices never
  silently carry over from a previous month, and expired lists offer nothing.
- **Versioned pricing policy** (`condition_policies`, immutable): condition adjustments (Neu +20 %, Wie neu +7 %,
  Sehr gut +5 %, Gut 0 %, Akzeptabel −20 %), brand and model overrides (model beats brand beats default), defect
  deductions (screen, housing, camera, Face ID/fingerprint, missing function), battery-health deduction below a
  threshold, platform margin, minimum dealer margin. Version 1 is seeded by migration from a literal.
- **Quote** = reference purchase price (Gut × condition × (1 − deductions), capped at 0), platform-to-dealer offer
  (− platform margin), maximum dealer-to-consumer price (− minimum dealer margin), and the dealer's gross margin for a
  given consumer price. A quote stores the price version and policy it used, is immutable, expires with its price
  (max. 7 days) and always states that the final payout is confirmed after physical inspection. Nothing is
  guaranteed for models without a current price.

## Device intake (implemented)

> **Update 2026-10-25:** extended by [ADR-0016](0016-ankauf-seller-capture-signature-till-payout-and-daily-report.md) — structured seller data with ID expiry, MRZ capture, QR self check-in, returning sellers, a signature bound to data and price before payment, and cash payouts from an open till.

`Quote → Received → Inspected → Completed | Rejected`

- **Received**: the quote must be the dealer's own and unexpired, used at most once; IMEI with valid Luhn digit
  (must equal the quote's IMEI if one was given); seller name, address, date of birth and ID document are recorded;
  sellers must be 18 or older. An IMEI that is already in stock anywhere on the platform is refused *before* any money
  changes hands (yes/no check via `inventory.imei_in_stock`, no other tenant data exposed).
- **Inspected**: the final offer is recalculated with the **quote's** price version and policy and the inspection
  findings — never with today's prices.
- **Completed** (one transaction): the amount paid (≤ inspected offer) and payment method are recorded, the catalog
  device product is found or created (brand + model + storage as part number), and an **IMEI stock unit** is created
  with its purchase price and tax scheme **§25a margin scheme** (bought from a private person), +1 stock and a movement.
  A partial unique index guarantees that an IMEI is in stock (InStock/Reserved) only once platform-wide.

## Ankaufbeleg (implemented)

`GET /api/ankauf/intakes/{id}/beleg` (module Ankauf, permission `inventory.manage`, own organization only; other
tenants' intakes → 404, intakes not yet completed → 409 `ankauf.not_completed`) returns a German PDF labelled
"Ankaufbeleg":

- document number `AB-<purchase date>-<last 8 hex digits of the intake id>` (the start of a v7 id is a timestamp, so
  the random tail is used) and purchase date/time in German local time;
- dealer (buyer): name, legal name, address and VAT id from the Tenancy contract (`IOrganizationDirectory.GetAddressAsync`);
- seller: name, address, date of birth, ID document type and number **masked except the last 4 characters**;
- device: brand, model, storage, IMEI, inspected condition, defects, battery health and inspection note;
- amount paid and payment method (cash: the seller confirms receipt; transfer: payment by bank transfer);
- note that the purchase from a private person contains no separately shown VAT and is documented for the margin
  scheme (§ 25a UStG);
- the seller's declaration of ownership (Eigentumserklärung: sole owner, free of third-party rights) and signature
  lines for seller and dealer.

The content is built in the domain (`Ankaufbeleg.Build`, unit-tested) and laid out by the shared
`SharedKernel.Documents.SimplePdf` writer (standard Helvetica fonts with WinAnsiEncoding, so ä ö ü ß € render; automatic
page breaks with page numbers). The receipt is generated on demand from the stored intake and is not archived as a
file. The wording is a template and has not been legally reviewed; no compliance claim is made.

## Open

Legal review of the Ankaufbeleg wording and archiving of issued/signed receipts, stolen-device/blacklist check, retention and
deletion of seller personal data after the legal period, selling serialized units via B2B offers (POS sales with §25a
margin VAT: see ADR-0008), individual offers for unpriced models, and confirmation of all percentages by the business.
