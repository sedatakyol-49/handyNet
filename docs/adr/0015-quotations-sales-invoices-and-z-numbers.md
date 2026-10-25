# ADR-0015: Quotations, sales invoices outside the till, invoice payments and gapless Z numbers

- Status: Accepted (no legal or tax compliance claimed — **to be reviewed by a tax adviser**, see "Not done")
- Date: 2026-10-25
- Extends: ADR-0008 (POS, invoices for POS sales), ADR-0012 (fiscal layer, DSFinV-K), ADR-0014 (company letterhead)

## Context

Shops also sell wholesale: the customer orders, the goods leave the shop, but nothing is rung up at the till — an invoice
is issued instead, often after a price proposal (Angebot / Preisvorschlag). Everyone who works the till must be able to do
this. Z-reports had no number at all (only the session GUID); German closings carry a per-register Z number (DSFinV-K
`Z_NR`), which the export computed on the fly.

## Decision

### Module and access

- Part of the POS module (`/api/pos/quotations`, `/api/pos/sales-invoices`, schema `pos`), so the sales documents share
  customers, stock staging (Inventory contract) and the invoice series with invoices for POS sales. Every user with
  `pos.sell` creates quotations and invoices, issues them and records payments (product owner's request). **Deliberate
  difference to the till:** the till's price/discount policy (`pos.price_override`, `pos.discount`) does not apply to
  quotations and invoices (wholesale prices are negotiated) — owners who want that restriction must not give `pos.sell`.
- Credit notes need `pos.refund`, or a manager approval by PIN (`X-Pos-Approval`, existing mechanism; the approval is bound
  to a register, so the request names the `registerId`; `pos.approvals.sales_invoice_id`, used for `CreditNote`).

### Lines and amounts

- Lines: catalog product (quantity), device by IMEI (own in-stock unit, quantity 1; §25a follows the unit's tax scheme,
  never the client) or free text (no stock). Integer cents; sub-cent input is refused (UI and API take cents).
- Price mode per document: **gross** (VAT contained: per rate `vat = Σgross − round(Σgross × 10000 / (10000 + rate))`) or
  **net** (VAT added per rate: `round(Σnet × rate / 10000)`), half away from zero; rates 19 %, 7 %, 0 %. §25a lines count
  with their final price, show no VAT and add the note "Gebrauchtgegenstände/Sonderregelung"; margin VAT and purchase
  price are stored per issued line (`margin_details`, internal) and never returned or printed. The Angular form uses the
  same arithmetic for its preview (unit-tested on both sides with the same figures).
- Reverse charge (§ 13b UStG) is an optional flag: all lines 0 %, no §25a lines, customer VAT id required, note
  "Steuerschuldnerschaft des Leistungsempfängers" — the UI warns that it must be checked by a tax adviser.

### Quotations

- `pos.quotations`: number `AN-YYYY-nnnn` per organization and year from `pos.quotation_sequences` (+1 allocation in the
  creating transaction, so sequential; gaps are not legally relevant and not DB-checked), customer snapshot (POS customer
  incl. company/VAT id, or entered), validity date (default 30 days), notes. Statuses Draft → Sent (also set by email) →
  Accepted / Rejected (reopen possible); **Expired** is derived (open quotation past `valid_until`). Editable while Draft
  or Sent; never deleted. Duplicate creates a new number. Convert (row lock, idempotent) creates one invoice draft with
  the same lines and marks the quotation accepted. No stock is reserved.

### Sales invoices

- `pos.sales_invoices`: Draft (editable, no number) → Issued (final) or Discarded (kept, hidden). Issue in one transaction:
  draft row locked (a concurrent second issue waits, then returns the issued invoice — idempotent), customer refreshed
  from the POS customer, all checks first (§14 Abs. 4 fields: seller VAT id, customer name and full address, VAT id for
  reverse charge, warehouse when stock moves), **number of the shared series first** (the series row stays locked, so
  issues of one organization serialize and do not race on stock), then stock staged exactly like a POS sale (reserved
  stock is never sold, products with serialized stock must be sold by IMEI, IMEI units `InStock → Sold`, xmin conflicts
  answer 409), then the immutable document (seller from the organization profile, letterhead logo/footer of ADR-0014,
  invoice date, delivery/service date, due date = invoice date + payment term, default from the company profile or 14 days).
  Any refusal rolls back the number with everything else.
- **One gapless invoice series per organization and year** for invoices for POS sales and sales invoices/credit notes
  (`pos.invoice_sequences`, `RE-YYYY-nnnnnn`): the deferred gap check accepts either table, both insert/issue triggers
  require the number just allocated, and neither table may reuse a number of the other. The invoice list shows both
  origins (POS-sale invoices read only, paid at the till).
- Issued rows never change (trigger, also for the owner role); corrections only by a **credit note** (Stornorechnung):
  the exact negative mirror of the whole invoice (lines, VAT per rate, totals), next number of the same series, reference
  to the original, reason; stock and IMEI units go back to the invoice's warehouse; at most one per invoice (unique
  index, repeat returns it). Partial credit notes are not supported.

### Payments and the till

- `pos.sales_invoice_payments` (append-only): bank transfer, card (e.g. outside the shop), other — recorded with date and
  reference. Payment state open / partially paid / paid and "overdue" (due date passed, not fully paid, not cancelled) are
  derived; the list filters by them and the back-office navigation shows the overdue count. An insert trigger locks the
  invoice and refuses payments on drafts, credit notes and cancelled invoices and any total above the invoice amount.
- **Cash received in the shop goes through the till** ("An der Kasse kassieren", `POST …/collect`, Idempotency-Key): one
  transaction like a sale (receipt number → open session → invoice lock → open amount) writes a receipt of kind
  `InvoicePayment` with one **not taxable** line "Zahlung Rechnung RE-…" (no product, tax treatment `NotTaxable`, 0 %,
  DSFinV-K USt-Schlüssel 5, business case "Forderungsaufloesung"), signed by the TSE like any receipt (0 % bucket of the
  process data), settled like a sale (change, cash rounding, card), and the payment row pointing to the receipt.
  **No second stock movement.** Card at the till works the same way. A cash payment row without a receipt is refused
  (check), a payment receipt without its payment row cannot commit (deferred trigger), and the receipt amount must equal
  the payment. Payment receipts cannot be cancelled or returned at the till (not a sale) and are counted separately on the
  Z-report (`invoicePaymentCount`, `invoicePaymentsMinor`); their cash is part of the expected cash.

### Emails and PDFs

- PDFs with the shared SimplePdf writer and the company letterhead (German layout like the POS invoice: sender line,
  address, facts, line table, net/VAT per rate, §25a gross, total, notes, seller footer). Draft invoices render a preview
  marked "Entwurf".
- Email: `pos.sales_document_emails` (append-only, ≤ 10 per document) raises `SalesDocumentEmailRequested` (Pos contract,
  outbox, same transaction) with the PDF; Notifications sends a short German text/HTML with the PDF attached
  (`email_messages.attachment_*`, the content is dropped once delivered or given up). The payload contains the customer's
  name/address inside the PDF (as printed) — personal data in the outbox like the receipt email's recipient.

### Z numbers

- `pos.registers.next_z_number` / `pos.cash_sessions.z_number`: taken at closing with a row-locking `UPDATE … RETURNING`
  **before** the session lock (same lock order as sales and refunds: register → session); the counter moves by +1 only, a
  deferred trigger fails any commit that took a Z number without closing a session with it, and a closing must carry the
  number just allocated; unique per register. The migration backfills existing closed sessions per register in closing
  order. Shown on the Z-report / interim report, the printed Z-report, the sessions list and the session detail; the
  DSFinV-K export uses it as `Z_NR` (and `REF_Z_NR`).

### UI

- POS app (Petrol design): menu entries and register tabs "Angebote" / "Rechnungen" (`/kasse/:registerId/angebote|rechnungen`):
  lists with filters, detail, touch + keyboard form (scan EAN/IMEI + Enter, product search of the warehouse, customer
  picker with quick create, gross/net, VAT per line, discount as amount or "10 %"), PDF/print/email, convert, issue,
  payments, collect at the till, credit note (PIN approval when the cashier lacks `pos.refund`).
- Back office: "Verkauf & Lager" → "Rechnungen & Angebote" (`/app/verkauf/…`) with the same shared components and an
  overdue badge. Translations in all seven locales.

## Consequences

- Issuing serializes per organization on the invoice series row (also with POS-sale invoices); acceptable for shop
  volumes, to be measured if many invoices are issued in parallel.
- The outbox carries invoice PDFs (tens of kB) and the emails keep them until delivered.

## Not done (no compliance claimed)

Partial credit notes and invoice corrections for POS-sale invoices, cancelling a mistaken till payment (would need a
storno of the payment receipt and a negative payment), refunds of payments after a credit note, dunning, XRechnung /
ZUGFeRD, DATEV export, discount/price rights for invoices, attachments other than the PDF, e-mail languages other than
German, and a review by a tax adviser of: the §14/§14a wording, the margin and reverse-charge notes, rounding per rate,
the "Forderungsauflösung" receipt and its TSE/DSFinV-K mapping, and the Z-number backfill.
