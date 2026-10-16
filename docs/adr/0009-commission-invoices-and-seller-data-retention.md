# ADR-0009: Commission invoices to sellers, and retention of Ankauf seller data

- Status: Accepted (no tax or data-protection compliance claimed — see "Not done")
- Date: 2026-10-16

## Context

The marketplace keeps a commission plus 19 % VAT from every online-paid B2B order (ADR-0006: snapshotted per payment
as `commission_net_minor` / `commission_vat_minor`, booked to `CommissionRevenue` / `VatPayable`). Sellers need an
invoice for it to deduct the input VAT. Invoices must be complete, numbered without gaps and never changed (GoBD); a
refund after invoicing must be corrected by a new document.

Separately, Ankauf intakes store the private seller's name, address, birth date and ID document number
(`ankauf.intakes.seller`). Personal data may only be kept as long as the record-keeping duty requires (GDPR Art. 5(1)(e)),
and the length of that period is not confirmed yet.

## Decision — commission invoices (module `Invoicing`, schema `invoicing`)

### Scope and module

- New module `Invoicing`, depending only on the **contracts** of Payments (`ICommissionStatements`, new) and Tenancy
  (`IOrganizationDirectory.GetLegalDetailsAsync`). Nothing depends on Invoicing (architecture tests).
- Sellers read their documents with module `B2BSales` and permission `orders.fulfil`. Platform admins issue and review
  months. No new `PlatformModule`.

### What is invoiced

- **One invoice per seller and calendar month** (German local time, Europe/Berlin): one line per payment that was
  captured in the month and is still `Paid` (not refunded) when the month is issued — order number, payment date, net
  commission, VAT, gross. Payments refunded before issuing are simply not invoiced.
- **VAT per line** is the VAT snapshotted on the payment; the invoice checks it equals `round(net × rate)` (half away
  from zero, `Invoicing:VatRateBasisPoints`, default 1900, must equal `Payments:CommissionVatBasisPoints`) and refuses
  otherwise. Totals = sum of lines, so the invoice matches the ledger's `VatPayable` postings to the cent.
- A month is issued only once it is over. A seller without commission in the month gets no invoice.
- **Credit notes ("Rechnungskorrektur (Storno)")**: a payment that was invoiced and later refunded is credited in the
  run for the **month of the refund**. One credit note per corrected invoice and refund month; each line mirrors the
  invoice line exactly with the opposite sign and references it. The issued invoice is never edited.
  - We deliberately do not print "Gutschrift": under §14 Abs. 2 S. 2 UStG that word means a self-billed invoice, and
    using it for a correction can have tax consequences.

### Numbers

- Platform-wide, gapless, sequential per **series and calendar year of the issue date**: invoices `HN-2026-000001`,
  corrections `HN-RK-2026-000001` (own series; chosen so invoices stay 1..n without corrections in between, and the
  prefix shows the document type). Six digits; the database check refuses more.
- Allocation like POS receipt numbers (ADR-0008): `INSERT … ON CONFLICT DO UPDATE SET next_number = next_number + 1
  RETURNING` on `invoicing.number_sequences` inside the issuing transaction; the row stays locked until commit, a
  rollback returns the number. Enforced by the database regardless of code: the counter starts at 1 and only moves by
  +1; a document must carry exactly the number just allocated; a deferred constraint trigger fails any commit that took
  a number without writing its document; `(kind, year, sequence)` and `number` are unique; the printed number is checked
  against kind/year/sequence and the year against the issue date.

### Immutability and integrity (GoBD)

- `invoices` and `invoice_lines`: the app role has `SELECT, INSERT` only; triggers refuse `UPDATE`, `DELETE`, `TRUNCATE`
  even for the owner. The number counter cannot be deleted.
- At commit a document must have lines whose sums equal its totals (deferred trigger). Lines must match their
  document's kind and organization; a credit line must exactly negate a line of the corrected invoice for the same
  payment. A payment is invoiced at most once and credited at most once (unique `(payment_id, kind)`).
- The issuer and recipient are stored as snapshots (jsonb) together with service period, issue date and VAT rate.

### Issuer data

- `Invoicing:Issuer` (name, legal name, street, postal code, city, country, VAT id, tax number). `appsettings.json`
  ships **placeholders** containing `PLATZHALTER`. Issuing requires name, address and a VAT id or tax number; **in
  Production issuing is refused (`invoicing.issuer_not_configured`) while any value is still a placeholder**, and the
  job logs a warning at start. Outside Production placeholders are allowed and the PDF says "MUSTER – … keine gültige
  Rechnung".

### Issuing: job and admin endpoint, under RLS

- `InvoiceRunner.IssueMonthAsync(month)` is the only issuing path. It finds sellers through
  `payments.commission_sellers(from, until)` (SECURITY DEFINER, returns seller ids only), then processes each seller in
  its own scope as that seller's **system actor** (ADR-0005): payments are read and invoices written under the seller's
  RLS policies. Each document is its own transaction.
- **Idempotent**: an existing invoice for (seller, month) or credit note for (invoice, refund month) is returned, checked
  again after taking the number lock; partial unique indexes catch concurrent runs (the loser rolls back with its number).
- **Job** `InvoiceIssuingWorker`: checks every `Invoicing:AutoIssueCheckMinutes` (60) and issues the previous month once
  per process (re-runs after restarts are harmless) — in practice in the first hour of the 1st, catching up after
  downtime. `Invoicing:AutoIssue=false` disables it.
- Admin listing uses `invoicing.documents_for_month(month)` (ids + organization only) and reads each seller's documents
  as its system actor.

### PDF

German PDF via `SharedKernel.Documents.SimplePdf` with the invoice fields of §14 Abs. 4 UStG as we understand them:
issuer name/address, issuer VAT id and tax number, recipient name/address (and VAT id if known), issue date, unique
number, service ("Vermittlungsprovision …") and service period, per-order net/VAT/gross, net total, VAT rate and amount,
gross total; corrections name the corrected invoice number and date. Note that the commission was already withheld from
payouts. **No claim of legal completeness** — the template needs review by a tax adviser.

## Decision — Ankauf seller-data retention

- `Ankauf:SellerDataRetentionYears` (int, 1–30). **Default null = disabled** because the legal period is not confirmed;
  the API logs a warning at startup in Production while it is disabled.
- Job `SellerDataRetention` (daily): finds intakes whose retention start (`completed_at`, or `received_at` for intakes
  never closed) is older than now − N years via `ankauf.intakes_due_for_anonymization(cutoff, limit)` (SECURITY DEFINER,
  ids + organization only) and anonymizes them as each organization's system actor.
- **Anonymize** = replace `seller` with the fixed placeholder (`anonymisiert`, postal code `00000`, birth date
  1900-01-01, ID number `ANONYMISIERT`) and set `anonymized_at`. Everything else stays (device, IMEI, condition, amount
  paid, payment method, stock unit) for bookkeeping and §25a documentation. An anonymized open intake cannot be
  inspected or completed.
- `ankauf.intakes` keeps its `UPDATE` grant, now constrained by a trigger: identity columns never change; `seller`
  changes only together with setting `anonymized_at` (one way, never back); closed intakes change only by anonymization,
  and anonymization changes nothing else; no `DELETE`. A check constraint requires the exact placeholder when
  `anonymized_at` is set.
- The Ankaufbeleg still renders for anonymized intakes and shows "anonymisiert" plus the anonymization date.
- **Audit log**: properties marked `[PersonalData]` (now `Intake.Seller`) are written as `"[personal data]"`; the
  migration redacts the seller data earlier versions had copied into `infra.audit_log` (a deliberate, one-time edit of
  the audit trail for data protection; not reversible).

## Consequences

- Invoices exist only for online-paid B2B orders; POS and Ankauf are the dealer's own business and are not invoiced by
  the platform.
- A refund is corrected only after the refund month has ended (monthly batch), not immediately.
- Issuing serializes on the platform-wide counter row; fine for monthly batches.
- Backups (14-day retention, docs/operations.md) still contain anonymized data until they age out.

## Not done (no compliance claimed)

E-invoicing (XRechnung / ZUGFeRD; B2B e-invoice obligations from 2025/2027), DATEV export, archiving of the issued PDF
(the PDF is re-rendered from the immutable data; a GoBD archive with hash/format proof is open), sending invoices by
email, reverse charge for sellers outside Germany (all treated as domestic, 19 %), partial refunds,
tax adviser review of the invoice template, confirmation of the Ankauf retention period and of the anonymization scope
(e.g. whether the ID document type must also go), retention of other personal data (user accounts, organizations).
