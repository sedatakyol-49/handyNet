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
- Refresh tokens + token revocation; email verification; password reset (needs Notifications).
- ✅ Notifications module: outbox → email queue (order placed / confirmed / shipped / rejected / cancelled, organization verified), SMTP via MailKit with retry/backoff, Mailpit in Compose. Open: user language preference, HTML templates, in-app notifications.
- ✅ Shipping module: carrier adapter interface, Manual + Simulated adapters, label ≠ handover, tracking sync job, parcel/insurance/lithium rules, delivery → 48 h inspection window → auto-completion (ADR-0005). Open: real DHL/DPD/GLS/UPS adapters (need API credentials + sandbox verification), returns labels, multi-parcel shipments.
- ✅ Payments core (ADR-0006): PSP adapter interface + Simulated provider, idempotent webhooks (status re-fetched), commission + VAT, refunds on cancel, payouts after completion, immutable double-entry ledger with a single validated write path, seller balance. Open: Mollie Connect adapter (needs contract + test key), seller onboarding/KYC at the PSP, reconciliation with PSP reports, partial refunds, chargebacks, commission invoices.
- Invoicing: invoice numbering, XRechnung/ZUGFeRD, DATEV export (external review before any compliance claim).
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
- Serialized stock (IMEI/serial) with partial unique index; device lifecycle.
- POS / Kassensystem (touch UI, barcode, cash/card, returns, daily closing) on the same reservation path;
  fiskaly TSE integration in sandbox first; DSFinV-K export. No fake signatures.
- Pricing & Ankauf: monthly XLSX price list import (Mobilfunk sheet), versioned condition multipliers,
  deductions, approval rules (increase / >30 % decrease / new model), immutable price versions.
- Repair management and the B2B repair network.

## Phase 4 — Public
- B2C storefront with Angular SSR, multi-language (de default, en, tr, es, ru, ar/fa RTL), checkout, buyback form,
  repair partner discovery.
- Demo/training tenants with synthetic data, demo reset, strict separation from ledger and live PSP.
- Hetzner production: managed backups + WAL archiving (PITR), restore drills, monitoring (OTel collector).

## Known limitations of Phase 1
- Single VAT rate (19 %); no invoices; no payments — orders are commitments only.
- Order = one seller; the UI splits the cart per seller.
- Access tokens live 60 min; revoking admin rights or membership takes effect at the next request for tenant
  data (membership is checked per request) but the admin claim persists until token expiry.
- Import commit runs row-by-row in one transaction (fine up to the 20,000-row limit; to be batched later).
- No Redis/Hangfire yet — not needed by the slice.
- Platform admins cannot yet view tenant data across organizations (by design; needs audited read paths).
