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

## Phase 2b — Lager & Marketplace usability (requested 2026-10-16)
Dependencies: Phase 1 catalog/inventory/marketplace, Imports pipeline.
Backend status (2026-10-17): 🟡 backend ✅ for all four items below, frontend ⬜. Implemented: `POST /api/inventory/items`
(Imports module; same `ProductMatcher`/product creation/offer upsert as the import commit; one transaction; Idempotency-Key
with request hash, replayed response; stock is *received* (+quantity, Receipt movement with EK) rather than synchronized;
IMEIs Luhn-checked and platform-unique; smartphones only by IMEI, tablets/wearables optionally; B2B offers for IMEI stock
refused until serialized B2B sales exist), `GET /api/inventory/items/lookup` (EAN/MPN/text, own stock/offers, suggested
SKU, `canCreateProduct`), product images (ADR-0011: `IFileStorage` local/S3, SkiaSharp re-encode to WebP, EXIF stripped,
thumbnails, ≤ 8 per product, reorder/remove, public immutable serving), `GET /api/marketplace/offers/{id}/detail`,
`GET /api/marketplace/facets`, filters (brand/category/quality/condition/seller lists, VK net price range, max delivery
days, compatible model, has images) with indexes. Frontend ✅ (wizard, image manager, detail modal + gallery, filter bar with chips, e2e). Open: S3 verification
against a real bucket, orphan-file cleanup job, gross price display (frontend).
- ✅ **Manual single-item entry in Lager** (in addition to CSV/XLSX import): 3-step dialog "Artikel anlegen"
  1) identify — EAN scan/typing or MPN, conservative catalog match (same `ProductMatcher` as the import); no match →
  create a canonical product (brand, name, category, quality, EAN/MPN, model, compatible models, images);
  2) stock — warehouse, quantity or one IMEI per unit for serialized categories, optional purchase price (EK);
  3) offer (optional) — SKU (suggested), VK net price, MOQ, delivery days, condition, publish toggle.
  One idempotent backend command that runs the same validation/matching/apply path as a one-row import (atomic,
  audited); "Speichern & nächster Artikel" with scanner focus for fast entry.
- ✅ **Marketplace stays read-only for products** (backend: test asserts no non-GET route under `/api/marketplace`): no "add product" there by design (products enter only via Lager
  manual entry or import); e2e asserts no create action on the marketplace page.
- ✅ **Marketplace detail modal**: clicking a row (or Enter on a focused row) opens a dialog with all product data
  (brand, name, category, quality, EAN, MPN, model, compatible models), offer data (seller + city, SKU, condition,
  net + gross price, available, delivery days, MOQ, last update), image gallery (placeholder if none), other
  sellers' offers for the same product, quantity + add to cart; deep link `?offer={id}`. Needs product images:
  `IFileStorage` abstraction (local disk dev, S3-compatible object storage prod), JPEG/PNG/WebP ≤ 5 MB, EXIF
  stripped, thumbnails, MIT-licensed image library.
- ✅ **Richer marketplace filters**: brand dropdown with counts, quality, condition, VK price range (min/max net),
  max delivery days, seller, compatible model ("passend für …"), "with images"; facet endpoint, active-filter chips,
  filters persisted in the URL, collapsible on mobile, supporting DB indexes. The requested "VKVK" filter was a typo for a
  Verkäufer (seller) filter (clarified 2026-10-17): seller multi-select with facet counts (`sellerId`).
- ✅ **Platform moderation of marketplace offers** (requested 2026-10-18: "deactivate products directly — all products
  of wholesaler A, or individual products"): admin tab "Marktplatz-Moderation" (`/api/admin/marketplace`). Seller list
  with offer counts and "block all / lift block" (seller-level block, also covers offers created later), offer table
  across all sellers (filters seller/brand/category/status/blocked/text, paging), single and bulk block/unblock (≤ 500,
  all or nothing), optional reason, confirmation dialogs. Moderation is separate from the seller's own status: blocked
  offers vanish from browse, facets, detail, cart refresh and ordering (`offer.blocked`, 409); the seller sees the block
  and reason and cannot change the status of a blocked offer (domain rule + DB trigger). Every action is written to
  `infra.audit_log` (admin, time, reason). Not yet: moderation history view, notifying the seller by email.

## Phase 2c — Standalone POS app, Odoo-POS parity (requested 2026-10-16)
Benchmark: Odoo 18 Point of Sale. Restaurant-only features (tables, kitchen display, self-ordering, food delivery)
are out of scope for phone shops unless requested.
- ✅ **A. Standalone app**: POS leaves the back-office shell/menus; own full-screen route `/kasse` with its own
  minimal top bar (register, session, cashier, online state, close), launched from the dashboard/registers list;
  touch-first, tablet + desktop, keyboard/scanner shortcuts kept.
- 🟡 **B. Selling screen like Odoo**: product grid with category tabs and images, search/scan, cart with numpad
  (Qty / % Disc / Price modes, +/-), line notes, price override with permission, parked orders (hold/resume,
  several open tickets), customer selection/creation at the till, payment screen with multiple methods, split,
  quick cash amounts, change, edit payment before finalizing, receipt screen (print / email / new order).
  Backend ✅ (ADR-0008 "Odoo-parity extensions"): product grid endpoint (own stock of the register's warehouse, paged,
  search, category filter + category list, serialized flag, available quantity, till price from own POS price → own
  offer net + 19 % → none, `imageUrl` placeholder), POS price maintenance, line notes on immutable receipts, price
  override enforced. ✅ Frontend wired (grid with prices, notes, price dialog, parked tickets, split payment, receipt screen). Open: ⬜ product images on tiles (Catalog images exist; POS `imageUrl` not yet filled), ⬜ e-mail receipt.
- 🟡 **C. Staff**: cashier switching with PIN on a shared device, per-cashier permissions (discount limit, price
  change, refund, cash in/out, closing), manager approval PIN. Backend ✅ per-cashier permissions on staff memberships
  (`pos.price_override`, `pos.discount` + max %, `pos.refund`, `pos.cash_movement`, `pos.close_session`; owners/managers
  always all), enforced by the API. Open: ⬜ PIN switching, ⬜ manager approval PIN; ✅ UI (rights gating at the till, Team page editor).
- 🟡 **D. Customers & pricing**: customer master at POS, pricelists, customer-specific prices, discount tags,
  loyalty points / coupons / promotions, gift cards (sale + redemption), cash rounding. Backend ✅ customer master
  (RLS, `[PersonalData]` kept out of the audit log), optional customer on a sale, printed snapshot on the receipt,
  per-organization POS price. Open: ⬜ pricelists/customer prices, ⬜ loyalty/coupons/gift cards, ⬜ cash rounding,
  ⬜ customer retention/anonymization job; ✅ UI (customer picker, create/edit, on receipt).
- 🟡 **E. Orders & documents**: order history with search/filter, refunds from an order, invoice for a POS sale,
  e-receipt by email/QR, reprint, ship-later / pick-up orders, quotation → POS order. Backend ✅ history filters
  (number, dates, amount, cashier, customer, payment method, kind), refunds from an order (existing returns), recorded
  reprints with copy number. Open: ⬜ invoice for a POS sale, ⬜ e-receipt, ⬜ ship-later, ⬜ quotations; ✅ UI (filter bar, reprint "Kopie n").
- 🟡 **F. Session & cash**: opening count with denominations, cash in/out with reasons, closing control with
  differences, session report, all-orders analytics, product/cashier/payment reports. Backend ✅ denominations at
  opening/closing (stored, total = opening/counted cash), predefined + free-text reasons, closing difference, sales
  report by product/cashier/payment method per session or date range. ✅ UI (denomination counting, reasons, report page). Open: ⬜ export (CSV/PDF).
- ⬜ **G. Devices**: customer display (second screen / remote device), receipt printer (ESC/POS, browser print),
  cash drawer kick, barcode scanner, card terminal (ZVT/OPI), QR bank payment, offline mode with sync —
  offline and terminals only together with the TSE/KassenSichV work (no unsigned receipts in Production).
- 🟡 **H. Design**: design spec and screens at least on par with Odoo POS (visual reference review before build),
  light/dark, RTL, large touch targets, Playwright visual checks. ✅ Screens built and reviewed from screenshots (desktop, tablet, phone, dark, RTL). Open: ⬜ automated visual regression checks.

## Phase 3 — Store operations
Dependencies: Phase 2 payments + invoicing.
- ✅ Serialized stock (IMEI) with platform-wide uniqueness; Ankauf device intake (quote → receive → inspect → pay + stock, §25a purchase price recorded). Selling serialized units over the counter (POS, §25a margin VAT) ✅. Open: selling serialized units via B2B offers, blacklist check. ✅ Seller-data retention job (ADR-0009): anonymizes seller data after `Ankauf:SellerDataRetentionYears` (disabled until the legal period is confirmed; warning in Production), Ankaufbeleg shows "anonymisiert", seller data no longer copied into the audit log.
- ✅ Ankaufbeleg: PDF purchase receipt for a completed intake (`GET /api/ankauf/intakes/{id}/beleg`) with dealer and seller data, masked ID number, device/IMEI/condition, amount and payment method, Eigentumserklärung with signature lines, §25a note; rendered by the shared `SimplePdf` writer (WinAnsiEncoding, multi-page; also used for simulated shipping labels). ✅ UI download button. Open: legal review of the wording, archiving of issued receipts, signed/countersigned copy upload.
- 🟡 POS / Kassensystem (ADR-0008): ✅ registers with gapless per-register receipt numbers (DB-enforced),
  cash sessions with deposits/withdrawals and daily closing (expected vs. counted cash, Z-report totals per payment
  method and VAT treatment), sales of products (EAN) and IMEI units, cash/card with change, line discounts, 19 % VAT and
  §25a margin VAT, Storno and partial returns as new negative receipts, stock back incl. IMEI units, immutable receipts
  (GoBD), idempotent sales/returns, EAN/IMEI lookup, fiscal signer abstraction with a test signer that is refused in
  Production. ✅ POS touch UI (registers, till, receipts, sessions, Z-report). Open: ⬜ real fiskaly TSE adapter (sandbox first) incl. TSE outage handling,
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
