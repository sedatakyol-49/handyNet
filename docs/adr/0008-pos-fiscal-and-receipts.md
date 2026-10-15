# ADR-0008: POS (Kassensystem), fiscal signer abstraction, immutable receipts and §25a VAT

- Status: Accepted (no fiscal compliance claimed — see "Not done")
- Date: 2026-10-15

## Context

Dealers sell accessories and used devices over the counter. A till must not sell stock that a B2B order has reserved,
must not sell the same device twice, and its records must be complete and unchangeable (GoBD). German law
(KassenSichV, §146a AO) requires every till transaction to be signed by a certified technical security device (TSE) and
a DSFinV-K export; used devices bought from private persons are taxed under the margin scheme (§25a UStG).

## Decision

### Module and access

- New module `Pos` (schema `pos`, `PlatformModule.Pos`), depending on Inventory, Catalog and Tenancy contracts only.
  Store sales are the dealer's own money and do not pass through Payments/Ledger (ADR-0006).
- Permissions: `pos.sell` (Owner, Manager, Staff): sessions, cash movements, sales, returns, lookup.
  `pos.manage` (Owner, Manager): registers, Storno.

### Registers, sessions, receipt numbers

- A register (Kasse) sells from one own warehouse. **Receipt numbers are per register, sequential and gapless**: the
  sale transaction takes the number with `UPDATE pos.registers SET next_receipt_number = next_receipt_number + 1 …
  RETURNING`, which also locks the register row until commit; any failure rolls the number back. The database enforces
  it independently of the code: the counter only moves by +1, a receipt must carry exactly the number just allocated,
  `(register_id, number)` is unique, and a deferred constraint trigger fails any commit that allocated a number
  without writing its receipt.
- A cash session (Kassensitzung) opens with counted cash; at most one open session per register (partial unique
  index). Sales need an open session (checked in code under a shared row lock and by an insert trigger). Cash
  movements (Einlage/Entnahme) need a reason; a withdrawal or cash refund can never exceed the expected cash.
- Closing (Tagesabschluss) locks the session exclusively (running sales finish first), computes the Z-report figures
  from the session's receipts and movements — receipt counts, totals, totals per payment method and per VAT treatment,
  first/last receipt number — and stores expected cash, counted cash and the difference. Expected cash = opening +
  cash applied to receipts (change netted out, cash refunds negative) + deposits − withdrawals. A closed session is
  immutable (trigger).

### Stock

- Same availability rule as B2B orders: only `on_hand − reserved` can be sold (`IInventory.StageSellAsync` uses
  `StockLevel.Adjust`; the CHECK `reserved <= on_hand` is the last line of defence). Products that have serialized
  units in the warehouse must be sold by IMEI.
- IMEI units: `InStock → Sold` on sale, `Sold → InStock` on Storno/return. Optimistic concurrency (`xmin`) on the unit
  and its stock level means two tills selling the same device concurrently get one success and one `409`. A returned
  unit still passes the platform-wide unique index of in-stock IMEIs.
- Movements `Sale` and `Return` are written for every change.

### Receipts (GoBD)

- Receipts, lines and payments are written once and never changed: the app role has only `SELECT, INSERT`, and
  triggers refuse `UPDATE`, `DELETE` and `TRUNCATE` even for the owner. Corrections are **new negative receipts**
  referencing the original: **Storno** (whole receipt, exact mirror of lines and payments, at most once — unique
  index) and **Return** (selected quantities; refunded by cash or card). Partial refunds allocate gross and VAT so that
  all returns of a line add up to exactly the original line; more than was sold can never come back.
- Each receipt stores a printable document (jsonb) exactly as issued: seller name/legal name/address/VAT id, register,
  number, time, lines (with IMEI), VAT breakdown, payments, change and fiscal data.
- Sales and returns require an `Idempotency-Key`; a retry returns the original receipt, a reused key with a different
  body is a `422`. Concurrent duplicates wait on the register lock and then get the stored receipt.

### VAT

- Prices are gross. Standard rate per line: `vat = gross − round(gross × 100/119)` (half away from zero).
- §25a margin scheme for units bought from private persons (`TaxScheme.Margin25a`, set by Ankauf intake): VAT only on
  the positive margin over the unit's purchase price, `0` at or below it. The margin VAT is stored (Z-report, tax
  return) but **never printed** and not exposed per line by the API (it would reveal the purchase price); the receipt
  carries the note "Gebrauchtgegenstände/Sonderregelung (§ 25a UStG)".

### Fiscal signer (KassenSichV)

- `IFiscalSigner` (start transaction → finish transaction with totals per VAT treatment and payment → `FiscalData`)
  is called inside the receipt transaction; all fiscal columns on the receipt are nullable.
- Only a `Disabled` test signer exists. It marks receipts "TSE nicht angebunden – Testbetrieb" and produces **no
  signature, transaction number or TSE time** — nothing is faked. Configuration `Pos:FiscalSigner`; without it, sales
  are refused (`pos.fiscal_signer_missing`). **In Production the API refuses to start with the Disabled signer**
  (options validation on start), like the Simulated carrier and payment provider.

## Consequences

- The till cannot be used in production until a certified TSE adapter exists. A real adapter must also handle TSE
  outages (KassenSichV allows selling with a "TSE ausgefallen" mark) — the current interface returns `Result` so that
  path can be added, but it is not designed in detail.
- Calling a remote TSE inside the database transaction holds the register lock for the duration of the call; acceptable
  for one till, to be measured with a real adapter.

## Not done (no compliance claimed)

Certified TSE adapter (fiskaly, sandbox first), DSFinV-K export, notification of the till to the tax office (§146a
Abs. 4 AO), receipt printing (ESC/POS, PDF) and electronic receipts, card terminal integration (ZVT/OPI; card payments
are recorded as confirmed by the cashier), reduced VAT rate, vouchers, cross-session returns reporting, customer
invoices for business customers (§14 UStG) — and an external review of all of the above.
