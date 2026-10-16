# Implementation roadmap

Status legend: ✅ done and verified by automated tests · 🟡 partial · ⬜ not started.

## Phase 1 — Vertical slice (this release)

| # | Item | Status |
|---|---|---|
| 1 | Repository, solution, module projects, CI | ✅ (CI workflow written; first GitHub run pending a remote) |
| 2 | Docker Compose (PostgreSQL, migrator, API, web) | 🟡 written, not executed (no Docker on dev machine) |
| 3 | Authentication (JWT, lockout, rate limit), registration, organizations, memberships, staff | ✅ |
| 3a | Tenant isolation: verified tenant context + PostgreSQL RLS + restricted DB role | ✅ |
| 3b | Admin: verification workflow, per-tenant module activation | ✅ |
| 4 | Supplier registration (organization with wholesaler capability, admin approval) | ✅ |
| 5 | Canonical product catalog with conservative EAN/MPN/quality matching | ✅ |
| 6 | CSV/XLSX import: mapping (auto-guess + manual), validation, preview/dry run, matching, history, approval (commit), atomic apply | ✅ |
| 7 | Supplier offers and inventory (warehouses, stock levels, movements) | ✅ |
| 8 | Dealer B2B catalog browsing | ✅ |
| 9 | B2B order creation with transactional stock reservation, idempotency, confirm/ship/cancel/reject | ✅ |
| 10 | Automated tests for the full workflow | ✅ 107 backend tests |
| 11 | Angular UI for the slice (all slice screens, de/en, RTL-ready, SSR for public pages) | ✅ build + 18 unit + 8 Playwright e2e against the real API |

## Phase 2 — Commercial B2B
Dependencies: Phase 1.
- ✅ Sessions and account emails (ADR-0010): opaque refresh tokens stored as SHA-256 hashes, rotation on every use, reuse detection revokes the token family, sliding expiry (`Jwt:RefreshTokenDays`, 14), logout / logout-all / change-password, all sessions revoked on password change or reset; password reset (enumeration-safe 202, single-use hashed 1 h token, per-IP rate limit + per-user cooldown, lifts lockout); email verification on registration with verify + resend (unverified users can still sign in, `emailVerified` in token response and `/api/me`); de/en emails via Notifications with the token kept out of the outbox and redacted from the email queue after delivery. Open: ⬜ web app refresh/logout/reset/verify screens, ⬜ access token down to 15 min once the web app refreshes (kept at 60), ⬜ purge job for expired token rows, ⬜ stored per-user language.
- ✅ Notifications module: outbox → email queue (order placed / confirmed / shipped / rejected / cancelled, organization verified), SMTP via MailKit with retry/backoff, Mailpit in Compose. Open: user language preference, HTML templates, in-app notifications.
- ✅ Shipping module: carrier adapter interface, Manual + Simulated adapters, label ≠ handover, tracking sync job, parcel/insurance/lithium rules, delivery → 48 h inspection window → auto-completion (ADR-0005). Open: real DHL/DPD/GLS/UPS adapters (need API credentials + sandbox verification), returns labels, multi-parcel shipments.
- ✅ Payments core (ADR-0006): PSP adapter interface + Simulated provider, idempotent webhooks (status re-fetched), commission + VAT, refunds on cancel, payouts after completion, immutable double-entry ledger with a single validated write path, seller balance. Open: Mollie Connect adapter (needs contract + test key), seller onboarding/KYC at the PSP, reconciliation with PSP reports, partial refunds, chargebacks.
- ✅ Commission invoices (ADR-0009): monthly invoice per seller for captured payments, corrections (Rechnungskorrektur) for refunds after invoicing, gapless platform-wide numbers `HN-YYYY-nnnnnn` / `HN-RK-YYYY-nnnnnn` (DB-enforced), immutable documents (GoBD-style triggers), German PDF with the §14 UStG fields, issuer from configuration (placeholders refused in Production), monthly job + admin endpoint, idempotent per seller and month, run per seller under RLS. Open: ⬜ XRechnung/ZUGFeRD e-invoices, ⬜ DATEV export, ⬜ archiving of issued PDFs, ⬜ invoice emails, ⬜ reverse charge for non-German sellers, ⬜ seller/admin UI, ⬜ tax adviser review of the template.
- ✅ Disputes: complaint within the inspection window puts the order on hold (no auto-completion, no payout); seller refund, buyer withdrawal, escalation, platform decision; refund flows to Payments + ledger. Open: return shipments and restocking, partial refunds, evidence uploads (photos), dispute emails, SLA/auto-escalation.
- Ratings and risk indicators.
- Import: saved mapping profiles, REST/JSON/XML feeds, SFTP pull via job scheduler (Hangfire), resumable
  chunked commits for >20k rows, platform moderation queue for new canonical products.
- Buyer-specific pricing rules.
- Staff invitations that the invited user accepts (existing accounts cannot be added directly).
- Localized API problem titles (`Accept-Language`); the UI currently translates known error codes.
- Playwright e2e in CI against a Compose stack; e2e cleanup of test organizations.
- Translations for tr, es, ru, ar, fa (only scaffolds today).
- Production host names in `frontend/angular.json` `security.allowedHosts` (Angular SSR host check).

## Phase 3 — Store operations
Dependencies: Phase 2 payments + invoicing.
- ✅ Serialized stock (IMEI) with platform-wide uniqueness; Ankauf device intake (quote → receive → inspect → pay + stock, §25a purchase price recorded). Selling serialized units over the counter (POS, §25a margin VAT) ✅. Open: selling serialized units via B2B offers, blacklist check. ✅ Seller-data retention job (ADR-0009): anonymizes seller data after `Ankauf:SellerDataRetentionYears` (disabled until the legal period is confirmed; warning in Production), Ankaufbeleg shows "anonymisiert", seller data no longer copied into the audit log.
- ✅ Ankaufbeleg: PDF purchase receipt for a completed intake (`GET /api/ankauf/intakes/{id}/beleg`) with dealer and seller data, masked ID number, device/IMEI/condition, amount and payment method, Eigentumserklärung with signature lines, §25a note; rendered by the shared `SimplePdf` writer (WinAnsiEncoding, multi-page; also used for simulated shipping labels). Open: UI button, legal review of the wording, archiving of issued receipts, signed/countersigned copy upload.
- 🟡 POS / Kassensystem backend (ADR-0008): ✅ registers with gapless per-register receipt numbers (DB-enforced),
  cash sessions with deposits/withdrawals and daily closing (expected vs. counted cash, Z-report totals per payment
  method and VAT treatment), sales of products (EAN) and IMEI units, cash/card with change, line discounts, 19 % VAT and
  §25a margin VAT, Storno and partial returns as new negative receipts, stock back incl. IMEI units, immutable receipts
  (GoBD), idempotent sales/returns, EAN/IMEI lookup, fiscal signer abstraction with a test signer that is refused in
  Production. Open: ⬜ POS touch UI (frontend), ⬜ real fiskaly TSE adapter (sandbox first) incl. TSE outage handling,
  ⬜ DSFinV-K export, ⬜ receipt printing (ESC/POS/PDF) and e-receipts, ⬜ card terminal integration (ZVT/OPI),
  ⬜ till registration with the tax office, ⬜ reduced VAT rate/vouchers, ⬜ external compliance review. No fake
  signatures, no compliance claimed.
- ✅ Pricing & Ankauf (ADR-0007): monthly XLSX list, approval rules, immutable price versions (current list only), versioned condition policy with overrides and deductions, dealer quotes with preserved versions. Open: device intake + inspection to final price, §25a documentation, stock entry with IMEI, individual offers, frontend.
- Repair management and the B2B repair network.

## Phase 4 — Public
- B2C storefront with Angular SSR, multi-language (de default, en, tr, es, ru, ar/fa RTL), checkout, buyback form,
  repair partner discovery.
- Demo/training tenants with synthetic data, demo reset, strict separation from ledger and live PSP.
- Hetzner production: managed backups + WAL archiving (PITR), restore drills, monitoring (OTel collector).

## Known limitations of Phase 1
- Single VAT rate (19 %); no invoices; no payments — orders are commitments only.
- Order = one seller; the UI splits the cart per seller.
- Access tokens live 60 min (refresh tokens since ADR-0010; logout revokes only the refresh token, the access token runs out); revoking admin rights or membership takes effect at the next request for tenant
  data (membership is checked per request) but the admin claim persists until token expiry.
- Import commit runs row-by-row in one transaction (fine up to the 20,000-row limit; to be batched later).
- No Redis/Hangfire yet — not needed by the slice.
- Platform admins cannot yet view tenant data across organizations (by design; needs audited read paths).
