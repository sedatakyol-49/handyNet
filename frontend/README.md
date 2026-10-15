# HandyNet Web (Angular)

Frontend for HandyNet, the multi-tenant B2B platform for the German mobile phone / spare-parts wholesale and repair
industry. Every screen talks to the real HandyNet API (`../backend`); there is no mock data.

- Angular 22 (standalone components, signals, new control flow, zoneless change detection by default), strict TypeScript
- SSR via `@angular/ssr` – public pages (`/login`, `/registrierung`) are server-rendered, everything under `/app` is
  rendered in the browser only (it depends on the token in `localStorage`; see `src/app/app.routes.server.ts`)
- Tailwind CSS v4 (PostCSS setup from Tailwind's Angular guide: `.postcssrc.json` + `@import "tailwindcss"`)
- Unit tests: Angular CLI unit-test builder with Vitest + jsdom (`ng test`)
- End-to-end tests: Playwright against the running API

## Setup

Requirements: Node 22+, npm, the HandyNet API running on `http://localhost:5170`.

```bash
npm install
npx playwright install chromium   # only needed for e2e tests
npm start                         # http://localhost:4200, /api is proxied to http://localhost:5170
```

The dev server proxies `/api` to the API via `proxy.conf.json`, so the app always calls same-origin `/api/...`
(`API_BASE_URL` in `src/app/core/api/api-config.ts` is empty by default; override it if the API lives on another origin –
CORS on the API already allows `http://localhost:4200`).

## Scripts

| Command | What it does |
| --- | --- |
| `npm start` | Dev server with SSR + API proxy on port 4200 |
| `npm run build` | Production build (browser + server bundles) into `dist/handynet-web` |
| `npm run serve:ssr:handynet-web` | Runs the built SSR server (port 4000, or `PORT`); forwards `/api` to `HN_API_URL` (default `http://localhost:5170`) |
| `npm test` | Unit tests once (Vitest via `ng test --watch=false`) |
| `npm run test:watch` | Unit tests in watch mode |
| `npm run e2e` | Playwright e2e tests (see below) |
| `node scripts/check-i18n.mjs` | Verifies that every translation key used in the code exists in `de.json` and `en.json` |

For SSR deployments the Angular server only accepts known `Host` headers (SSRF protection). `localhost` is allowed in
`angular.json` (`security.allowedHosts`); add your production host there or set `NG_ALLOWED_HOSTS`.

## Structure

```
src/app
├─ core/                     app-wide singletons, no UI
│  ├─ api/                   DTO types (models.ts), typed API clients (handynet-api.ts), RFC 7807 problem parsing
│  ├─ auth/                  SessionStore (signals: token, /api/me, selected organization, module/permission checks),
│  │                         AuthService, authInterceptor (Bearer + X-Organization-Id, 401 → logout), route guards
│  ├─ i18n/                  I18nService (signal-based), `t` / `tEnum` pipes, locales/*.json
│  ├─ format/                money (cents → "1.234,56 €", parsing), date pipe
│  ├─ ui-state/              toasts + describeProblem() (API problem → user text)
│  └─ storage.service.ts     localStorage wrapper that is a no-op during SSR
├─ shared/
│  ├─ ui/                    button, field/input/select, table, status badge, pagination, empty state,
│  │                         banner (error/warning/info, shows API problem title), toast host, dialog, page header
│  └─ data/query.ts          createQuery(): signal-driven loader (latest request wins, keeps previous data while loading)
└─ features/                 one folder per screen, all lazy-loaded
   ├─ auth/                  Login, Registrierung (reactive form, client + server validation)
   ├─ shell/                 top bar (org switcher, language, user menu), side navigation (navigation.ts)
   ├─ dashboard/             Übersicht (org status, verification notice, real order counts, quick links)
   ├─ marketplace/           Marktplatz (search with "/" shortcut, filters, dense results, add to cart)
   ├─ cart/                  CartStore (signals, persisted per organization, grouped by seller) + checkout page
   ├─ orders/                Einkauf / Verkauf lists and order detail with allowed actions
   │  ├─ shipping/           "Versand" section (book/hand over/track shipments, buyer receipt + inspection) and
   │  │                      its pure helpers (kg → g, carrier limits, remaining time, form validation) in shipping.logic.ts
   │  ├─ payment/            "Zahlung" section (buyer pays online, seller sees commission/payout) and payment.logic.ts
   │  └─ dispute/            "Reklamation" section, message thread component and dispute.logic.ts (actions per role/status)
   ├─ finance/               Finanzen (seller): open balance, recent payouts, paged ledger
   ├─ offers/                own offers with inline price/status editing, create offer from catalog
   ├─ inventory/             stock table, adjustment dialog, movements, warehouses
   ├─ imports/               import wizard (upload → preview/mapping → approve/discard) and history
   ├─ team/                  staff list + add staff
   └─ admin/                 platform administration: tabs "Unternehmen" (verify/reject/suspend, module toggles) and
                             "Reklamationen" (escalated disputes queue, detail + decision)
```

### Tenancy, modules and permissions

`GET /api/me` returns the user's memberships. The selected organization (top-bar switcher, persisted in
`localStorage`) is sent as `X-Organization-Id` on every tenant-scoped request by `authInterceptor`. Navigation items and
routes are declared once in `features/shell/navigation.ts` / `app.routes.ts` with the required module and permission;
they are hidden (and the route guarded) unless the selected organization is **Verified**, has the module and the user has
the permission – mirroring the API's own 403 checks. Unverified organizations see "Ihr Unternehmen wird geprüft" on the
overview instead of module pages. Switching organizations re-creates the current page so all data is reloaded.

### Errors

API errors are RFC 7807 problem documents. `toApiProblem()` normalizes them; 422 `errors` are shown next to the matching
form fields (`fieldErrors()`), everything else in a banner or toast using the API `title` (or a translated text for a
few well-known `code`s in `errors.codes.*`).

### Checkout and idempotency

One order = one seller, so the cart is grouped by seller and each group is checked out separately. The first checkout
attempt of a group creates an `Idempotency-Key` (UUID) that is persisted with the cart and reused for retries after
network errors / 5xx; it is discarded after success or a definitive 4xx answer. On `409 order.price_changed` /
`order.insufficient_stock` (and 422 `order.below_minimum` / `order.offer_unavailable`) the affected lines are re-read
from the marketplace, changed prices are shown (old price struck through) and must be accepted before ordering again.
Net, VAT and gross shown after ordering come from the server response.

### Order lifecycle and shipping

Orders run Placed → Confirmed → Shipped → Delivered → Completed (or Cancelled / Rejected). A complaint during the
inspection window moves a Delivered order to **Disputed**; it ends as Completed (no refund) or **Refunded** (see Disputes). The order detail page has a
**Versand** section:

- Seller (B2BSales + `orders.fulfil`), order Confirmed, no non-cancelled shipment: form to book a shipment – carriers
  and their limits from `GET /api/shipments/carrier-profiles` (max weight / longest side / standard liability /
  insurable maximum / lithium rules are shown for the selected carrier and checked client-side), weight in kg
  (converted to integer grams without float math), dimensions in cm, "versichert" (pre-checked and explained when the
  order gross exceeds the selected carrier's `standardLiabilityMinor`), tracking number only for `Manual`.
  The declared value is the order gross (read-only). The API stays the final check (`shipment.*` error codes).
- Creating a shipment/label is **not** a handover. The order only becomes Shipped (and stock is consumed) when the
  seller confirms "An Versanddienstleister übergeben" (`POST /api/shipments/{id}/handover`). Before that the shipment
  can be cancelled. The label PDF is fetched with the auth headers as a blob and downloaded; "Sendungsverfolgung
  aktualisieren" calls `/sync` (hidden for `Manual`, which has no tracking API). Tracking events are shown newest first.
- While a shipment is open, the plain "Als versendet markieren" (`/orders/{id}/ship`) and "Ablehnen" actions are hidden
  (the API answers `409 order.has_open_shipment` if it happens anyway; the message is translated).
- Buyer: read-only shipment card + timeline; "Ware erhalten" when Shipped (`/confirm-delivery`), then the inspection
  deadline with remaining time and "Ware annehmen" (`/accept`) while Delivered. After the deadline the server
  completes the order. Problems must be reported before the deadline ("Problem melden", see Disputes).
- After the buyer confirms receipt the shipments are re-fetched immediately and once more ~4 s later (a Manual shipment
  becomes Delivered via background processing; no polling).
- State-changing actions (handover, cancel, receipt, accept) ask for confirmation in a dialog.

### Payments

Online payment is optional – orders without it stay valid (invoice / transfer outside the platform) and nothing is
blocked. In dev the provider is `Simulated` (a test checkout page served by the API; no real money).

- Order detail, **Zahlung** section. Buyer (B2BPurchasing + `orders.place`): "Jetzt online bezahlen" while the order is
  Placed/Confirmed and not paid (`POST /api/orders/{id}/payments`, then `window.location.assign(checkoutUrl)` in the same
  tab – the provider redirects back to the order detail page `/app/bestellungen/{orderId}`), "Zahlung fortsetzen" for an Open payment, status,
  amount, paid/refunded dates and the escrow explanation. Seller: gross, commission net + VAT, "Auszahlungsbetrag" and
  the payout state (matched by order id in `GET /api/payments/balance` → `recentPayouts`, otherwise "Auszahlung nach
  Abschluss der Bestellung"). Payments are re-read after every order action (cancel/reject refunds automatically).
- **Finanzen** (`/app/finanzen`, B2BSales + `orders.fulfil`): open balance, recent payouts (order number linking to the order) and the
  paged seller-payable ledger (`GET /api/payments/ledger`). Ledger amounts are shown from the seller's perspective:
  credits (API negative) as "+… Gutschrift", payouts (API positive) as "−… Auszahlung".
  New ledger descriptions contain the order number; older entries may still show a GUID (the ledger is immutable).
- Seller payout state also covers disputes: "on hold" while the order is Disputed, "refunding" after a refund decision
  until the background job has refunded the payment (the page re-reads the payments once ~5 s after the decision).
- Decision logic (which button, payout state, ledger sign) is pure and unit-tested in `payment.logic.ts`.

### Disputes (Reklamationen)

Order detail, **Reklamation** section (`features/orders/dispute`, data from `GET /api/disputes?orderId=`):

- Buyer (B2BPurchasing + `orders.place`), order Delivered and inspection window running, no active dispute:
  "Problem melden" opens a form (reason, description ≤ 4000 characters) that explains that automatic completion and
  the payout stop until the complaint is resolved (`POST /api/orders/{id}/disputes` → order Disputed).
- Every dispute of the order: status badge, reason, dates, chronological thread (sender Käufer / Verkäufer / Plattform,
  own messages right-aligned and tinted, labelled "Sie"), message input while Open/Escalated.
- Actions while active, each through a confirmation dialog with explanation: seller (B2BSales + `orders.fulfil`)
  "Erstattung gewähren" (text required → dispute and order Refunded, online payment refunded automatically), buyer
  "Reklamation zurückziehen" (text required → order Completed, seller is paid out), either party "An HandyNet
  übergeben" while Open (optional text → Escalated). Closed disputes show the resolution and who decided.
- Order lists filter by Disputed / Refunded; the overview counts "Offene Reklamationen" (sales) and
  "Einkauf: in Reklamation" (purchases).
- Administration → **Reklamationen** (`/app/administration/reklamationen`, platform admins): queue of escalated
  disputes (`GET /api/admin/disputes`; no organization names in that API, ids are shortened), detail with thread and
  decision form (refund yes/no + resolution, confirmation dialog, `POST /api/admin/disputes/{id}/decide`).
- Which actions are shown per role/status/permission, validation and label keys are pure and unit-tested in
  `dispute.logic.ts`; the API stays authoritative (`dispute.*` / `order.inspection_over` codes are translated).

## i18n

`I18nService` holds the active dictionary in a signal; templates use `{{ 'nav.marketplace' | t }}` and
`{{ order.status | tEnum: 'orderStatus' }}` for API enum values. German (`de.json`) is the default, bundled and used for
SSR; other languages are lazy-loaded JSON chunks. Missing keys fall back to German.

| Language | File | State |
| --- | --- | --- |
| Deutsch | `de.json` | complete |
| English | `en.json` | complete |
| Türkçe, Español, Русский | `tr.json`, `es.json`, `ru.json` | scaffold (navigation only) – TODO |
| العربية, فارسی | `ar.json`, `fa.json` | scaffold (navigation only) – TODO, sets `dir="rtl"` |

The selected language is stored in `localStorage` and applied to `<html lang dir>`. Layout uses logical CSS properties
(`ms-*`, `start-*`, `border-e`) so RTL mirrors correctly. Run `node scripts/check-i18n.mjs` after adding strings.

Money is always formatted with `Intl.NumberFormat('de-DE', { style: 'currency' })` from integer cents
(`1.234,56 €`), independent of the UI language; dates follow the UI language.

## End-to-end tests (Playwright)

The e2e tests run against the **real API** and create their own uniquely named test companies on every run (they do
not clean up). They need a platform admin account of the target environment, passed via environment variables – never
commit these credentials:

```bash
# API must be running on http://localhost:5170 (override with HN_API_URL)
# The frontend is started automatically (npm start) unless something already listens on HN_BASE_URL (default :4200)
HN_ADMIN_EMAIL=admin@handynet.local HN_ADMIN_PASSWORD='…' npm run e2e
```

PowerShell:

```powershell
$env:HN_ADMIN_EMAIL='admin@handynet.local'; $env:HN_ADMIN_PASSWORD='…'; npm run e2e
```

Without these variables the tests are skipped. Specs:

- `e2e/login-marketplace.spec.ts` – seeds a supplier (committed price list) and a shop via the API, then the shop logs
  in, checks module-based navigation and browses the marketplace (`/` shortcut, search, stock filter, quantity limits).
- `e2e/b2b-flow.spec.ts` – the full flow through the UI only: register supplier + shop, admin verifies and enables
  modules, supplier uploads and approves a CSV import, shop orders, supplier confirms and ships, stock is reduced.
- `e2e/checkout-conflicts.spec.ts` – price change between add-to-cart and checkout (409 handling) and cart persistence.
- `e2e/payments-flow.spec.ts` – shop orders (marketplace search by supplier SKU) and pays through the simulated checkout
  page ("Bezahlen"), order shows "Bezahlt"; supplier sees commission and payout amount and the open balance on Finanzen,
  confirms and ships without shipment; shop confirms receipt and accepts; the Finanzen page (refreshed for up to 20 s)
  shows the payout and an open balance of 0,00 €.
- `e2e/shipping-flow.spec.ts` – seller books a Simulated shipment, downloads the label (asserts a PDF response) and
  cancels it, then books a Manual shipment with a tracking number and hands it over (order Shipped); the buyer confirms
  receipt (Delivered, inspection deadline shown) and accepts (Completed). Simulated tracking takes 2 min in dev, so
  delivery is driven through the Manual path.
- `e2e/disputes-flow.spec.ts` – delivery prepared via the API (order → confirm → ship → confirm receipt); buyer reports a
  problem in the UI, seller writes a message and escalates, the platform admin decides "Keine Erstattung" in the
  administration → order Completed; second order: buyer reports, seller grants a refund → "Erstattet"; third order paid
  online: payout on hold while disputed, payment shows "Erstattet" after the refund job.

The login and registration endpoints are rate limited (20/min per IP by default); running the suite repeatedly within a
minute may hit `429`.

Run against the production SSR build instead of the dev server:

```bash
npm run build && PORT=4300 npm run serve:ssr:handynet-web
HN_BASE_URL=http://localhost:4300 HN_ADMIN_EMAIL=… HN_ADMIN_PASSWORD=… npm run e2e
```
