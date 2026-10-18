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
- Permissions: `pos.sell` (Owner, Manager, Staff): open sessions, sales, lookup, product grid, customers, reprints.
  `pos.manage` (Owner, Manager): registers, POS prices, Storno, date-range reports. Per-cashier rights (2026-10-17, see
  "Odoo-parity extensions"): `pos.price_override`, `pos.discount` (with a maximum discount %), `pos.refund`,
  `pos.cash_movement`, `pos.close_session`.

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

> **Update 2026-10-18:** extended by [ADR-0012](0012-fiscal-layer.md) — fiscal mode per organization/register
> (Disabled, simulated test TSE, fiskaly), outage handling, signed cash movements/closings, QR code, DSFinV-K export.
> The text below describes the original state.

- `IFiscalSigner` (start transaction → finish transaction with totals per VAT treatment and payment → `FiscalData`)
  is called inside the receipt transaction; all fiscal columns on the receipt are nullable.
- Only a `Disabled` test signer exists. It marks receipts "TSE nicht angebunden – Testbetrieb" and produces **no
  signature, transaction number or TSE time** — nothing is faked. Configuration `Pos:FiscalSigner`; without it, sales
  are refused (`pos.fiscal_signer_missing`). **In Production the API refuses to start with the Disabled signer**
  (options validation on start), like the Simulated carrier and payment provider.

## Odoo-parity extensions (2026-10-17, roadmap Phase 2c)

### Product grid and prices

- `GET /api/pos/registers/{id}/products` reads the view `pos.sellable_stock` (stock levels of the register's warehouse +
  catalog product fields + "has serialized units" + own POS price). Cross-module read as a SQL view (ADR-0001), direction
  POS → Inventory/Catalog only; `security_invoker = true`, so the caller's RLS policies apply. Paged, searchable (brand,
  name, EAN, MPN, model), category filter, in stock (`available > 0`) unless `includeOutOfStock`. `imageUrl` is reserved
  and always null until the Catalog provides product images.
- **Till price** = the organization's own **POS price** (`pos.prices`, gross, per organization and product, set/cleared
  with `PUT /api/pos/prices/{productId}`, `pos.manage`, audited, never deleted); otherwise the organization's **own
  marketplace offer**: its net B2B price + 19 % VAT, rounded half away from zero (an offer in the register's warehouse
  first, then published, then most recent); otherwise **none** (the cashier enters the price). POS must not depend on
  Marketplace (architecture tests), so POS declares the port `IOfferPriceSource` and the API host composes it from the
  new Marketplace contract `IOwnOfferPrices`.

### Per-cashier rights (Tenancy memberships)

- Owners and managers always hold every POS right and a 100 % discount limit; they cannot be restricted.
- Staff memberships get `pos_permissions` (subset of the five rights; null = defaults) and `pos_max_discount_percent`
  (0–100; null = 100). The **staff default keeps everything a cashier could do before** (discount, refund, cash in/out,
  closing); only `pos.price_override` is new and must be granted. A check constraint keeps POS rights off owner/manager
  rows; a stored list can never grant anything outside the five POS rights. Changed by the owner with
  `PUT /api/organization/staff/{userId}/pos-rights` (`organization.manage`); takes effect with the member's next request
  (membership is resolved per request).
- Enforced by the API: endpoint filters for returns (`pos.refund`), cash movements (`pos.cash_movement`) and closing
  (`pos.close_session`); in the sale transaction a domain rule (`LinePolicy`) refuses — with `403` and a stable code —
  a unit price different from the till price without `pos.price_override` (`pos.price_override_forbidden`), any
  discount without `pos.discount` (`pos.discount_forbidden`) and a discount above the limit, measured on unit price ×
  quantity in integer arithmetic (`pos.discount_limit_exceeded`). A refused sale rolls back like any other failure, so
  its receipt number is returned. No price known ⇒ free entry. The rule applies to serialized devices too (a model with
  a POS price sells at that price unless overridden).

### Customers, notes, history, reprints

- `pos.customers` (per organization, RLS, no DELETE): name, company, email, phone, address, VAT id. All identifying
  fields are `[PersonalData]`, so the audit log stores `"[personal data]"` instead of the values (as Ankauf, ADR-0009).
  A sale may name a customer; the receipt stores `customer_id` and prints a **snapshot** (name, company, address, VAT id
  — never email/phone) inside the immutable document. Storno/return receipts carry the original's customer. Later
  changes to the customer never change issued receipts; the snapshot is part of the retained record.
- **Line notes** (≤ 200 characters) are stored on the immutable receipt line, printed in the document and copied to
  refund lines.
- The request fingerprint (idempotency) includes note and customer only when present, so fingerprints of earlier
  requests are unchanged.
- **Order history** `GET /api/pos/sales`: filters receipt number, register, session, date range (from inclusive, to
  exclusive), gross amount range (signed), cashier, customer, payment method, kind.
- **Reprints** `POST /api/pos/sales/{id}/reprints` append a row to `pos.receipt_reprints` (sequence 1, 2, … per receipt,
  unique, serialized per receipt by an advisory lock; immutable like receipts) and return the unchanged receipt with the
  copy number for the "Kopie" marker. The receipt itself is never touched.

### Session and cash

- Opening and closing can be entered as **denomination counts** (euro coins/notes in cents, each at most once); the total
  is stored as opening/counted cash and the counts as jsonb on the session. If an amount is sent as well, both must agree
  (`pos.denominations_mismatch`). The opening figures can never change afterwards (trigger, also for the owner role);
  closing figures are fixed with the closed session (existing trigger).
- Cash movements take a **predefined reason** (`ChangeFund`, `OwnerDeposit` for deposits; `BankDeposit`, `Expense`,
  `OwnerWithdrawal` for withdrawals; `Other` for both, needs a text) and/or free text; the stored text is the free text
  or the German label. `GET /api/pos/cash-movement-reasons` lists them.
- **Reports** `GET /api/pos/reports/sales`: by product (sold/returned/net quantity, gross, discount), by cashier (sales,
  refunds, gross) and by payment method — for a session (`pos.close_session` or `pos.manage`) or a date range of at
  most 366 days, optionally per register (`pos.manage`). Gross only: per-product VAT would reveal §25a purchase prices.

## Staff, approvals, e-receipts, invoices, cash rounding (2026-10-18, roadmap Phase 2c B–F)

### Product images on tiles

- `imageUrl` of the product grid is the thumbnail of the catalog product's primary image, read through the Catalog
  contract (`IProductCatalog.GetImagesAsync`, one query per page); relative to the API base, public and immutable (ADR-0011).

### POS PIN and cashier switching on a shared device

- Every member may have a POS PIN per organization (`pos.cashier_pins`): 4–6 ASCII digits, no repeated digit or plain
  run (1234/4321), stored as salted PBKDF2-SHA256 (100,000 iterations), `[PersonalData]` (never in the audit log). Own PIN
  `PUT /api/pos/me/pin` with the account password (`IUserAccounts.VerifyPasswordAsync`); the owner sets/clears any member's
  PIN on the Team page (`PUT /api/pos/cashiers/{userId}/pin`, `organization.manage`), which also lifts a lockout.
- A 4–6 digit PIN has few combinations, so the protection is the attempt limit: 5 wrong entries in a row lock the PIN
  for 15 minutes (stored, survives restarts; the PIN row is locked `FOR UPDATE` during a check so parallel guesses queue);
  PIN endpoints are rate-limited per signed-in user (`RateLimits:PosPinPerMinute`, 20).
- `POST /api/pos/registers/{id}/cashier` {userId, pin} starts a **cashier session** (`pos.cashier_sessions`): an opaque
  256-bit token (only its SHA-256 is stored) sent as `X-Pos-Cashier`. It is valid only for the same organization (RLS), the
  same device login (the user who signed in on the device), not ended, not expired (`Pos:CashierSessionHours`, 12), and not
  idle beyond the register's `auto_lock_minutes` (last-seen updated at most every 30 s). The cashier must still be a member
  with `pos.sell`; their rights are read fresh per request. A new sign-in ends earlier ones of the same device login and
  register; `POST /api/pos/cashier/lock` ends it. Invalid/expired tokens answer `403 pos.cashier_invalid`.
- A POS endpoint filter resolves the token into the request's **PosActor**: receipts, reprints, sessions, cash movements,
  customers and prices record the cashier as `created_by`; endpoint rights (`RequirePosPermission`) and the line policy use
  the cashier's permissions and discount limit. The device stays logged in as its user; without the header the till acts as
  that user (unchanged behaviour). Writes on another register are refused (`pos.cashier_register_mismatch`).
- The web till keeps the cashier per browser tab (sessionStorage), shows the PIN pad for "Kassierer wechseln", locks on
  request or after the register's idle minutes (client timer; the server enforces the same on the token).

### Manager approval by PIN

- `POST /api/pos/registers/{id}/approvals` {approverUserId, pin, permissions} — an owner or manager (not the cashier
  asking) holding the rights enters their PIN; the result is a single-use token (`X-Pos-Approval`) valid for
  `Pos:ApprovalSeconds` (120), bound to the organization, register, device login and the requesting cashier. Approvable
  rights: `pos.price_override`, `pos.discount` (the limit becomes the approver's, 100 %), `pos.refund`, `pos.cash_movement`,
  `pos.close_session`.
- The approval is locked (`FOR UPDATE`, usable?) before the TSE signs and used in the same transaction as the record it
  allows: `pos.approvals` stores `used_at`, `used_for` (Sale/Storno/Return/CashMovement/CloseSession) and the receipt,
  cash movement or closed session. The receipt document prints "Freigabe: <name>"; receipt, cash movement and session DTOs
  return the approval. Insert and use are audited; the row is final after use (trigger), never deleted.

### E-receipts

- `POST /api/pos/sales/{id}/email` {email | null = the receipt customer's email} records `pos.receipt_emails`
  (recipient `[PersonalData]`, at most 5 per receipt) and raises `ReceiptEmailRequested` (Pos **contract**) in the same
  transaction; the Notifications module (now depending on Pos contracts, architecture tests) renders a German text + HTML
  email (HTML-encoded values) and sends it with the existing queue/retry. The payload carries the receipt as printed
  without the customer block; the recipient address is the only personal data in it.
- `POST /api/pos/sales/{id}/public-link` returns a 256-bit token (only its hash stored, `pos.receipt_links`) and the path
  `/beleg/{token}`; the QR on the receipt screen encodes the full URL. `GET /api/public/receipts/{token}` (anonymous,
  rate-limited per IP) reads through the SECURITY DEFINER function `pos.public_receipt`, which returns exactly one unexpired
  receipt document; the API removes the customer snapshot. Unknown, malformed and expired tokens answer the same 404.
  Expiry `Pos:PublicReceiptLinkDays` (90).

### Invoices for POS sales (Rechnung, §14 UStG)

- `POST /api/pos/sales/{id}/invoice` {customer | null = the receipt's customer} issues one invoice per sale receipt
  (repeat = the same invoice, 200). Refused for refund receipts, cancelled or (partly) returned sales (a correction invoice
  is not supported yet), without the seller's VAT id, and without the customer's name and full address.
- Number `RE-YYYY-nnnnnn` per organization and calendar year (Europe/Berlin): `INSERT … ON CONFLICT DO UPDATE +1 RETURNING`
  on `pos.invoice_sequences` (row-locked until commit); triggers: counter starts at 1 and moves by +1, a deferred check
  fails any commit that took a number without its invoice, and an invoice must carry the number just allocated and match a
  sale receipt of the organization. All checks run before the number is taken, so a refusal never touches the counter.
- Content (stored as jsonb, immutable, PDF rendered on download with SimplePdf): seller name/legal name/address/VAT id,
  customer name/company/address/VAT id, number, issue date, date of supply (receipt date), lines with quantity, unit price,
  discount and amount, net/VAT/gross per rate for regularly taxed lines; §25a lines as gross only with the note
  "Gebrauchtgegenstände/Sonderregelung" (§14a Abs. 6 UStG), never their VAT; payment lines and the receipt reference.
  `GET /api/pos/sales/{id}/invoice` (204 when none), `GET /api/pos/invoices/{id}/pdf`.

### Cash rounding

- Per register `cash_rounding`: the part of a sale paid in cash (total − card) is rounded to 0.05 € (1–2 cents down, 3–4 up);
  `receipts.rounding_minor` (−2…+2, check) with payments = total + rounding. Lines, VAT and the receipt total stay exact
  (TSE and VAT see the exact amounts; the payment totals include the rounding). Storno mirrors the rounding, cash returns
  on a rounding register round the payout. The receipt document, e-receipt, invoice payment lines and the Z-report
  (`summary.roundingMinor`, expected cash from the rounded payments) show it.

## Consequences

- The till cannot be used in production until a certified TSE adapter exists. A real adapter must also handle TSE
  outages (KassenSichV allows selling with a "TSE ausgefallen" mark) — the current interface returns `Result` so that
  path can be added, but it is not designed in detail.
- Calling a remote TSE inside the database transaction holds the register lock for the duration of the call; acceptable
  for one till, to be measured with a real adapter.

## Not done (no compliance claimed)

Added 2026-10-18: invoice corrections (Rechnungskorrektur) for invoiced sales that are later returned, XRechnung/ZUGFeRD
for POS invoices, e-receipt languages other than German, a PIN reset by the member without the owner, and a legal review of
the invoice and e-receipt wording.

Certified TSE adapter (fiskaly, sandbox first), DSFinV-K export, notification of the till to the tax office (§146a
Abs. 4 AO), receipt printing (ESC/POS, PDF) and electronic receipts, card terminal integration (ZVT/OPI; card payments
are recorded as confirmed by the cashier), reduced VAT rate, vouchers, cross-session returns reporting, customer
invoices for business customers (§14 UStG) — and an external review of all of the above.
