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
| `node scripts/check-i18n.mjs` | Verifies the translation keys of all seven locale files (see i18n) |

For SSR deployments the Angular server only accepts known `Host` headers (SSRF protection). `localhost` is allowed in
`angular.json` (`security.allowedHosts`); add your production host there or set `NG_ALLOWED_HOSTS`.

## Structure

```
src/app
├─ core/                     app-wide singletons, no UI
│  ├─ api/                   DTO types (models.ts), typed API clients (handynet-api.ts), RFC 7807 problem parsing
│  ├─ auth/                  SessionStore (signals: token, /api/me, selected organization, module/permission checks),
│  │                         AuthService, authInterceptor (Bearer + X-Organization-Id, refresh + retry, sign-out),
│  │                         TokenRefresher (single-flight, cross-tab lock), account-links (fragment token, email
│  │                         language), password-policy, route guards
│  ├─ i18n/                  I18nService (signal-based), `t` / `tEnum` pipes, locales/*.json
│  ├─ format/                money (cents → "1.234,56 €", parsing), date pipe
│  ├─ ui-state/              toasts + describeProblem() (API problem → user text)
│  └─ storage.service.ts     localStorage wrapper that is a no-op during SSR
├─ shared/
│  ├─ ui/                    button, field/input/select, table, status badge, pagination, empty state,
│  │                         banner (error/warning/info, shows API problem title), toast host, dialog, page header
│  └─ data/query.ts          createQuery(): signal-driven loader (latest request wins, keeps previous data while loading)
└─ features/                 one folder per screen, all lazy-loaded
   ├─ auth/                  Login, Registrierung (reactive form, client + server validation), Passwort vergessen,
   │                         Passwort zurücksetzen and E-Mail bestätigen (email link targets)
   ├─ account/               Konto & Sicherheit (change password, sign out everywhere)
   ├─ shell/                 top bar (org switcher, language, user menu), side navigation (navigation.ts),
   │                         unverified-email banner (email-verification-banner.ts)
   ├─ dashboard/             Übersicht (org status, verification notice, real order counts, quick links)
   ├─ marketplace/           Marktplatz (search with "/" shortcut, filters, dense results, add to cart)
   ├─ cart/                  CartStore (signals, persisted per organization, grouped by seller) + checkout page
   ├─ orders/                Einkauf / Verkauf lists and order detail with allowed actions
   │  ├─ shipping/           "Versand" section (book/hand over/track shipments, buyer receipt + inspection) and
   │  │                      its pure helpers (kg → g, carrier limits, remaining time, form validation) in shipping.logic.ts
   │  ├─ payment/            "Zahlung" section (buyer pays online, seller sees commission/payout) and payment.logic.ts
   │  └─ dispute/            "Reklamation" section, message thread component and dispute.logic.ts (actions per role/status)
   ├─ finance/               Finanzen (seller): open balance, recent payouts, paged ledger
   │  └─ invoices/           Provisionsrechnungen (seller list + detail + PDF) and invoice.logic.ts (Berlin months,
   │                         month validation, date-only/month formatting, issue-run outcome, cent sums)
   ├─ offers/                own offers with inline price/status editing, create offer from catalog
   ├─ inventory/             stock table, adjustment dialog, movements, warehouses
   ├─ imports/               import wizard (upload → preview/mapping → approve/discard) and history
   ├─ team/                  staff list + add staff
   ├─ ankauf/                Ankauf (dealer): model search, preliminary buyback quote, quote history; ankauf.logic.ts
   │                         (IMEI Luhn check, quote form validation, error text); device intake (Ankäufe list +
   │                         intake page) with intake.logic.ts (age, seller/inspect/amount checks, steps per status)
   ├─ pos/                   Kasse (standalone app /kasse): pos-app (top bar), registers, selling screen (cart, numpad,
   │                         product grid, parked orders, payment, receipt), receipts (Storno/Return), cash sessions with
   │                         closing and Z-report; pos.logic.ts (cart totals, % discounts, VAT preview, numpad, split
   │                         payments/change, parked orders, idempotency, returnable quantities, shortcuts),
   │                         receipt-view (printable 80 mm receipt), z-report
   └─ admin/                 platform administration: tabs "Unternehmen" (verify/reject/suspend, module toggles),
      │                      "Reklamationen" (escalated disputes queue, detail + decision), "Ankaufspreise" and
      │                      "Provisionsrechnungen" (issue a month, run result, documents of the month)
      └─ ankauf/             price lists (upload, review, publish/discard) and pricing policy versions;
                             ankauf-admin.logic.ts (basis points ↔ %, change %, selectable rows, default validity,
                             policy form validation)
```

### Tenancy, modules and permissions

`GET /api/me` returns the user's memberships. The selected organization (top-bar switcher, persisted in
`localStorage`) is sent as `X-Organization-Id` on every tenant-scoped request by `authInterceptor`. Navigation items and
routes are declared once in `features/shell/navigation.ts` / `app.routes.ts` with the required module and permission;
they are hidden (and the route guarded) unless the selected organization is **Verified**, has the module and the user has
the permission – mirroring the API's own 403 checks. Unverified organizations see "Ihr Unternehmen wird geprüft" on the
overview instead of module pages. Switching organizations re-creates the current page so all data is reloaded.

### Sessions and account security

Business rules: `../docs/adr/0010-sessions-refresh-tokens-and-account-emails.md`.

- **Storage.** Login, refresh and change-password return an access token (JWT) plus an opaque refresh token. Both live
  in `localStorage` under `hn.auth` (`{accessToken, expiresAt, refreshToken, refreshExpiresAt, userId}`), shared by all
  tabs of the browser profile. Why not elsewhere: the API takes the refresh token in the request body (no HttpOnly
  cookie), `sessionStorage` would give every tab its own token family and sign users out per tab, and memory only would
  sign out on every reload. The trade-off is XSS exposure, the same as for the access token; mitigations are the
  rotation with reuse detection on the server (a stolen, used token revokes the family), "Überall abmelden" and no
  third-party scripts. `StorageService` is a no-op during SSR and `/app` renders client-side only, so the server never
  sees or needs tokens. A stored session is kept while either token is valid.
- **Refresh** (`core/auth/token-refresher.ts`, `auth.interceptor.ts`): a request made less than 60 s before the access
  token expires waits for a refresh first; a 401 triggers a refresh and **one** retry with the new token. There is a
  single in-flight refresh per tab (concurrent callers share the promise) and across tabs a lock (Web Locks API
  `hn.auth.refresh`, fallback: a 10 s lease in `localStorage`). Inside the lock the session is re-read from storage: if
  another tab already rotated it, that pair is used and no second refresh is sent — presenting the used token would
  count as theft and revoke the family. Other tabs follow rotations and sign-outs through the `storage` event.
  A rejected refresh (401 `auth.refresh_token_invalid|expired|reused`) or a second 401 clears the session and opens
  `/login?expired=1&returnUrl=…`; network errors / 5xx during a refresh are passed to the caller and keep the session.
  The refresh call itself bypasses the interceptors; anonymous `/api/auth/*` endpoints are never refreshed or retried.
- **Logout** (user menu) clears the session at once and revokes the token family with `POST /api/auth/logout`
  (best effort). **Konto & Sicherheit** (`/app/konto`, user menu): account data with email status, "Passwort ändern"
  (`/change-password`; the API ends all other sessions and returns a new pair for this one; 422
  `auth.current_password_invalid` / `password.weak` at the fields) and "Überall abmelden" (confirmation,
  `POST /api/auth/logout-all`, then `/login?signedOut=all`). Access tokens already issued elsewhere keep working until
  they expire (≤ 60 min, see ADR); the page says so.
- **Password reset:** "Passwort vergessen?" on the login page → `/passwort-vergessen` (server-rendered). The API answers
  202 for every valid address, so the confirmation is worded identically for existing and unknown accounts ("Falls für
  … ein Konto besteht"). The email link `/passwort-zuruecksetzen#token=…` opens the reset form; success clears any local
  session (the API revoked all) and shows `/login?reset=1`. `auth.token_invalid` / `auth.token_expired` replace the form
  with the message and "Neuen Link anfordern".
- **Email verification:** `/email-bestaetigen#token=…` verifies on load (signed in or not) and reloads `/api/me` when
  signed in. While `/api/me` says `emailVerified: false` the app shell shows a warning banner with "Link erneut senden"
  (`/verify-email/resend`); it can be dismissed for the browser session (`sessionStorage`, per user). After a send or
  `409 auth.verification_recently_sent` the button is disabled for 60 s with the cooldown text; `409
  auth.email_already_verified` reloads `/api/me`, which hides the banner.
- **Tokens in links** are read from the URL fragment (never sent to servers or logs), then removed from the address bar
  with `history.replaceState` (path and query kept). They live only in the page component: reloading the page shows
  "Dieser Link ist unvollständig". Both link pages render client-side only (the fragment does not reach the server).
- **Email language:** registration, reset request and resend send `language`. The API accepts only `de` / `en`
  (anything else is a 422) and defaults to German, so the English UI sends `en` and every other UI language `de`
  (`emailLanguage()` in `core/auth/account-links.ts`).

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

### Commission invoices (Provisionsrechnungen)

Business rules: `../docs/adr/0009-commission-invoices-and-seller-data-retention.md`. Documents are immutable; the UI only
reads them (and lets the platform admin trigger issuing).

- **Rechnungen** (`/app/finanzen/rechnungen`, nav entry under "Verkauf & Lager", also linked from Finanzen; B2BSales +
  `orders.fulfil` like Finanzen): paged list from `GET /api/invoices` – number (`HN-YYYY-nnnnnn`, corrections
  `HN-RK-YYYY-nnnnnn` with "Korrigiert HN-…"), type badge (Rechnung / Rechnungskorrektur), billing month, invoice date,
  line count, net / VAT / gross and a PDF button per row.
- Detail (`/app/finanzen/rechnungen/:id`): header (number, type, dates, service period, VAT rate, link to the corrected
  invoice), issuer and recipient exactly as snapshotted, lines (order link, paid at, net/VAT/gross), totals and
  "PDF herunterladen" (`GET /api/invoices/{id}/pdf`, fetched with the auth headers as a blob, saved as `<number>.pdf`).
  While the issuer data still contain `PLATZHALTER` (dev) a warning says the document is only a sample.
- Administration → **Provisionsrechnungen** (`/app/administration/rechnungen`, platform admins): month picker (default =
  previous month in German time; the current/future month is refused client-side like the API's
  `invoicing.month_not_over`), confirmation dialog (documents are final), run result (sellers checked, new invoices,
  new corrections, already issued, failures with translated codes) and all documents of the month
  (`GET /api/admin/invoices?month=`) with a cent-exact total. The page explains that re-running is safe (idempotent per
  seller and month; corrections land in the refund month) and that the monthly job normally does this on the 1st.
- Dates without time (`issueDate`, service period) are formatted as calendar dates without time-zone shift; months use
  Europe/Berlin like the API. Pure helpers are unit-tested in `invoice.logic.spec.ts`; the detail rendering and the API
  clients in `invoice-detail.page.spec.ts`. Money cells are wrapped in `<bdi dir="ltr">` so negative amounts keep their
  order in ar/fa.

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

### Ankauf (used-device buyback)

Module `Ankauf` (enabled per organization in Administration → Unternehmen → Module; the list comes from
`GET /api/admin/modules`). Business rules: `../docs/adr/0007-ankauf-pricing.md`.

- Administration → **Ankaufspreise** (`/app/administration/ankauf`, platform admins, no `X-Organization-Id`):
  - Upload a monthly XLSX (label, optional sheet, default "Mobilfunk"). Validity defaults to the 1st – last day of next
    month; date and time are local to the browser (the time zone is shown) and sent as ISO instants: "gültig ab" date +
    time, "gültig bis" = end of that day (23:59:59.999). Client checks mirror the API (.xlsx, 10 MB, ≤ 92 days).
  - Review (`/app/administration/ankauf/preislisten/:id`): summary tiles, approval rules, filters (decision / change /
    search), table with previous → new price, Δ % and badges, checkbox multi-select (only Pending/Approved/Rejected
    rows, never auto-accepted ones) with bulk "Freigeben" / "Ablehnen", skipped rows panel. "Veröffentlichen" is
    disabled with an explanation while prices are pending and asks for confirmation (models missing from the list or
    rejected have no price – no carry-over); "Verwerfen" asks as well.
  - **Preisregeln** (`/app/administration/ankauf/preisregeln`): values of the active version in %, version history
    (active / scheduled / superseded), form for a new immutable version prefilled from the active one. Percentages are
    parsed as text into integer basis points (100 bp = 1 %) and validated against the API ranges; brand/model overrides
    are rows (brand or "Marke|Modell|Speicher" key, normalized like the API, + condition + %).
- **Ankauf** (`/app/ankauf`, module Ankauf; creating quotes needs `orders.place`): search models with a current price,
  choose the condition (names only – policy percentages are not exposed to dealers), defects, battery health, optional
  consumer price and IMEI (Luhn-checked client-side). The result card shows the API's numbers (reference price Gut,
  condition adjustment, deductions, reference purchase price, platform offer to the dealer, max. consumer price,
  dealer margin with negative margins highlighted), validity, policy number and the API's notice ("Vorläufiges
  Angebot …"); quote history below. No price calculation happens in the browser.
- **Ankäufe** (device intake; module Ankauf + `inventory.manage`, stock views need the module Inventory as well):
  "Ankauf starten" on an unexpired quote (result card or history row; quotes with a running/finished intake link to it)
  opens `/app/ankauf/ankaeufe/neu/:quoteId`. Steps Annahme → Prüfung → Bezahlung & Einlagerung, derived from the status:
  - Annahme: seller name, address, birth date (age ≥ 18 checked client-side), ID type + number, IMEI (read-only from
    the quote if it had one, otherwise Luhn-checked), privacy note. `409 unit.imei_in_stock` shows "Gerät nicht
    ankaufen, Herkunft prüfen". Afterwards the intake lives at `/app/ankauf/ankaeufe/:id`.
  - Prüfung (repeatable until completed, prefilled from the last inspection or the quote): quoted vs inspected offer
    side by side with the difference, max. consumer price, and the note that the API recalculates with the quote's
    price version and policy, not today's prices.
  - Bezahlung: amount prefilled with the inspected max. consumer price, must be > 0 and ≤ inspected offer (client check
    in cents), Bar / Überweisung, optional warehouse (default warehouse otherwise), confirmation dialog with IMEI,
    amount and the §25a margin-scheme stock note. Completed intakes link to Lager. "Ankauf ablehnen" (reason required)
    while not completed. Completed intakes offer "Ankaufbeleg (PDF)" (`GET /api/ankauf/intakes/{id}/beleg`, fetched
    with the auth headers as a blob and downloaded like the shipping label).
  - `/app/ankauf/ankaeufe` (nav "Ankäufe"): latest 100 intakes with status filter and badges.
- `ankauf.*` error codes are translated; for codes whose API message names sheets/rows/ranges (`sheet_missing`,
  `duplicate_models`, `unreadable`, `pending_decisions`, `policy_range`) the API text is appended.

### Kasse (POS)

Module `Pos` (enabled per organization by the platform admin; sells stock of the Inventory module). Business rules:
`../docs/adr/0008-pos-fiscal-and-receipts.md`. The POS is a **standalone full-screen app** at `/kasse` (not inside
the back-office shell and not in its menu; `pos.sell`: Owner, Manager, Staff), launched from the card "Kasse starten"
on the overview. Old `/app/kasse/...` URLs redirect. Register management and Storno need `pos.manage` (Owner,
Manager) and are hidden otherwise. Benchmark: Odoo 18 Point of Sale.

- App frame (`pos-app.ts`, state in `pos-context.ts`): top bar with register, session state, cashier, online/offline
  indicator (`navigator.onLine`), clock and a menu (selling, receipts, sessions, cash in/out & closing, register
  selection, theme System/Hell/Dunkel stored in `localStorage`, language, back to the back office). Touch-first
  (targets ≥ 44 px), responsive (desktop, tablet landscape/portrait; below 768 px one pane with a products/cart switch),
  RTL via logical properties (the numpads keep the keypad digit order). Dark mode remaps the Tailwind palette variables
  inside `.hn-pos-dark` (`styles.css`, screen only; the receipt stays paper-white).
- `/kasse`: entry screen – registers (session open/closed, warehouse, next receipt number), "Kasse öffnen". With
  `pos.manage`: create (name + warehouse), rename, deactivate/activate (the API refuses while a session is open).
- `/kasse/:registerId` (selling screen, `till.page.*`): without an open session, the opening cash count opens one.
  - Cart (start side): lines with quantity × unit price, discount, line total, selected line highlighted; totals with
    net / VAT 19 % (computed per line exactly like the API) and §25a devices listed without VAT (margin VAT is computed by
    the API from the purchase price and never shown). Numpad with the modes Menge / % Rabatt / Preis (the first key
    replaces the shown value, backspace on an empty quantity removes the line), −/+, C, and a display field for
    keyboard entry. Unit prices are typed by the cashier (the API has no sales prices) and prefilled with the last price
    used in this browser session (`sessionStorage`).
  - Products (end side, needs the module Inventory): search/scan field, category chips and tiles for the stock rows of
    the register's warehouse (`GET /api/inventory/stock`, up to 600 rows; categories via `GET /api/catalog/products/{id}`,
    cached in `localStorage`; no images in the API → category icon or monogram; stock badge; sold-out tiles greyed).
    Text filters the tiles (Enter adds a single match); EAN/IMEI codes (6–20 digits) + Enter are looked up
    (`/api/pos/lookup`): EAN → product line (+1 per scan, limited to the available stock), 15 digits → own in-stock
    device (quantity fixed 1, IMEI shown, §25a marked). Tapping a tile with an EAN uses the same lookup.
  - Parked orders: several open tickets per register (tabs with item counts, "+" parks the current one, discard with
    confirmation), stored in `localStorage` per organization + register (`hn.pos.orders.*`, tolerant to blocked or
    corrupted storage), including the pending checkout attempt.
  - Payment screen: payment-method buttons (Bar, Karte) add lines prefilled with the remaining amount; several lines
    (split) per method are possible, each editable via keyboard or the numpad (+10/+20/+50); quick cash (exact, next
    banknotes), "Karte (Rest)"; remaining/change display; the API's rules are mirrored client-side (`settlePayments()`).
    Validate sends an `Idempotency-Key` created per attempt and reused when the same ticket is retried after a network
    error / 5xx (a changed ticket gets a new key).
  - Receipt screen: change due, receipt preview, print, new order; the next scan starts a new order automatically.
  - Shortcuts (listed on the screen): F2 search/scan field, F4 card for the rest, F8 exact cash, F9 payment / validate,
    Esc step by step (receipt → payment → search text → discard ticket with confirmation), Enter in the numpad display
    returns to scanning. Printable keys typed outside a field go to the scan field (on the payment screen digits feed
    the numpad).
- Receipts show the API's stored document exactly as issued: seller, number, date, lines with tax label (A / §25a), VAT
  breakdown, notes (§25a), payments/tendered/change and, prominently on top, the fiscal notice ("TSE nicht angebunden –
  Testbetrieb" in dev; TSE data is printed only for signed receipts). "Beleg drucken" uses `window.print()`; the print
  stylesheet in `styles.css` prints only the `.hn-print-area` (receipt or Z-report) on an 80 mm roll.
- `/kasse/:registerId/belege` (current session or all) and `/kasse/belege/:id`: Storno (`pos.manage`, reason,
  confirmation; not offered once items were returned) and Return (quantity per line up to sold − returned, refund
  preview allocated exactly like the API, cash or card, reason, confirmation, own Idempotency-Key).
- `/kasse/sitzungen/:id`: live interim figures, deposits/withdrawals with reason, closing with the counted cash
  (difference to the expected cash shown before and in the confirmation, which re-reads the session first); afterwards
  the printable Z-report (receipt number range, counts, totals, by payment method and by taxation, cash
  reconciliation). `/kasse/:registerId/sitzungen` lists the sessions.
- Not yet possible because the API lacks it (see `../docs/roadmap.md`, Phase 2c): product images and sales prices,
  line notes, customers, e-mail receipts, cashier PIN/permissions per cashier.
- Money stays in integer cents (`parseMoneyToMinor`) and is displayed with Intl de-DE. `pos.*`, `stock.insufficient`
  (with the available quantity), `stock.serialized_requires_imei` and `unit.*` codes are translated.

## i18n

`I18nService` holds the active dictionary in a signal; templates use `{{ 'nav.marketplace' | t }}` and
`{{ order.status | tEnum: 'orderStatus' }}` for API enum values. German (`de.json`) is the default, bundled and used for
SSR; other languages are lazy-loaded JSON chunks. Missing keys fall back to German.

| Language | File | State |
| --- | --- | --- |
| Deutsch | `de.json` | complete |
| English | `en.json` | complete |
| Türkçe, Español, Русский | `tr.json`, `es.json`, `ru.json` | complete |
| العربية, فارسی | `ar.json`, `fa.json` | complete, sets `dir="rtl"` (number ranges worded as "from … to …"; two literal input examples keep a left-to-right mark) |

The selected language is stored in `localStorage` and applied to `<html lang dir>`. Layout uses logical CSS properties
(`ms-*`, `start-*`, `border-e`) so RTL mirrors correctly. Run `node scripts/check-i18n.mjs` after adding strings: it fails
when a key used in code is missing in de/en, when any language lacks a key of `de.json` or has extra keys, or when
`{placeholders}` differ from the German text. **New strings must be added to all seven files.**

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
- `e2e/ankauf-flow.spec.ts` – admin uploads a generated XLSX (`exceljs`, devDependency; unique brand per run, valid
  from now so it becomes the current list), approves the pending prices and publishes; admin enables the module Ankauf
  for a shop; the shop searches the model and creates a quote (Neu + housing damage, consumer price 330 €) and sees
  300,00 € → +20 % → −10 % → 324,00 €, max. consumer price 275,40 €, margin −6,00 €. It assumes the default pricing
  policy is active (it only reads it). Price lists are global: each run replaces the current list of the target
  environment.

- `e2e/ankauf-intake.spec.ts` – admin publishes a list via the API (unique brand, right before use) and enables Ankauf +
  Inventory in the UI; the shop quotes (Gut, generated Luhn-valid IMEI), starts the intake with an adult seller,
  inspects worse (Akzeptabel + display damage + battery 70 % → 300,00 € becomes 144,00 €), is refused paying above the
  offer, pays 120,50 € and completes, downloads the Ankaufbeleg (asserts a PDF response); Lager shows the device with
  on-hand 1. A second shop: a 16-year-old seller is refused client-side, the stocked IMEI shows the in-stock warning,
  another intake is rejected with a reason. Same default-policy assumption as ankauf-flow.

- `e2e/pos-flow.spec.ts` – shop with Pos + Inventory + ProductImport; a product with an EAN and 5 units received are
  created via the API. No POS entry in the back-office menu; the overview launches `/kasse`. The shop creates a
  register, opens a session with 100,00 €, scans the EAN, types 12,50 € on the numpad display, quantity 2 via the numpad
  (total 25,00 €, VAT 3,99 €); parks the ticket, adds the product to a second ticket by its tile, switches back and
  reloads (both tickets kept); pays ticket 1 with 50,00 € cash on the payment screen (F9) → receipt no. 1 with 25,00 €
  change and "TSE nicht angebunden – Testbetrieb"; pays ticket 2 split (card 10,00 € + cash 5,00 €) → receipt no. 2
  with 2,50 € change; returns one item of receipt 1 in cash → receipt no. 3 with −12,50 €; closes with counted =
  expected (115,00 €) → difference 0,00 €, Z-report: receipts 1 to 3, 25,00 € gross, cash 15,00 €, card 10,00 €;
  `/app/kasse` redirects to `/kasse`, the menu leads back to the back office.

- `e2e/invoices.spec.ts` – a new seller (B2BSales) opens "Rechnungen" from the navigation and from Finanzen (empty list
  from the real API, only that nav entry active), an unknown id shows "Rechnung nicht gefunden."; a buyer-only company
  has no entry. The admin opens Administration → Provisionsrechnungen, the current month is refused client-side, the
  previous month is issued (confirmation) and issued again: the second run creates nothing new and the month listing
  matches the run. This issues real documents for the previous month in the target environment (as the monthly job does).

- `e2e/account-security.spec.ts` (no admin needed; users self-register via the API, emails are not read) – "Passwort
  vergessen" from the login page: client validation, then the same 202 confirmation for an unknown and an existing
  address (`language: de` sent); `/email-bestaetigen` with an invalid token (message, fragment removed from the URL)
  and without one; `/passwort-zuruecksetzen` with an invalid token (mismatch check, then "Link ungültig" → request
  page). A fresh user sees the unverified-email banner, "resend" hits the cooldown (409) and is disabled; Konto &
  Sicherheit: wrong current password → field error, change succeeds, the old refresh token is revoked (401 from
  `/refresh`); logout revokes the new one; the old password fails, the new one signs in. Two devices: device B's access
  token is made to look expired → one `/refresh` (200) on reload and a rotated token; device A "Überall abmelden" →
  `/login?signedOut=all`, device B's refresh token is revoked.

The login and registration endpoints are rate limited (20/min per IP by default); running the suite repeatedly within a
minute may hit `429`.

Run against the production SSR build instead of the dev server:

```bash
npm run build && PORT=4300 npm run serve:ssr:handynet-web
HN_BASE_URL=http://localhost:4300 HN_ADMIN_EMAIL=… HN_ADMIN_PASSWORD=… npm run e2e
```
