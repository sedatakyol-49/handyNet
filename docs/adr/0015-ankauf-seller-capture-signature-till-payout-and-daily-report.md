# ADR-0015: Ankauf seller capture (MRZ, QR self check-in), on-screen signature, till payout and the daily report

- Status: Accepted (no legal compliance claimed — see "For legal review")
- Date: 2026-10-25
- Extends: ADR-0007 (Ankauf), ADR-0008/0012 (POS, fiscal layer), ADR-0009 (seller-data retention)

## Context

The product owner asked for (1) structured customer data at the Ankauf, captured from the ID card/passport or by the
customer via QR code, with the customer signing; (2) cash purchases taken out of the till and visible in the daily
figures — what the company bought and sold; (3) friendlier Ankauf and Einkauf screens.

## Decision

### Seller data (`ankauf.intakes.seller`, jsonb, `[PersonalData]`)

- New optional members: address `country` (ISO alpha-2, null = DE), `nationality` and `idIssuingCountry` (ICAO codes as in
  the MRZ, e.g. `D`, `TUR`), `idIssuingAuthority`, `idExpiryDate`. They are omitted from the JSON while null, so older
  intakes and the anonymized placeholder keep their exact stored form (the database check `ck_intakes_anonymized` is
  unchanged; anonymization therefore also removes the new members).
- Domain rules (`SellerRules.Validate`, on the shop's day in Europe/Berlin): required fields, plausible birth date, minimum age
  18 (`ankauf.seller_minor`, details `latestBirthDate`), expiry date required (`ankauf.id_expiry_required`), an expired
  document is refused (`ankauf.id_expired`; valid through its last day), document number letters/digits 2–30, ICAO codes.
- `capture_method` (`Manual` | `Mrz` | `SelfCheckIn`) and `check_in_id` are stored on the intake (identity columns,
  trigger-protected).
- **Returning sellers**: `GET /api/ankauf/sellers?search=` searches the organization's own, not anonymized intakes by name
  (≥ 2 characters, newest intake per person, RLS + organization filter) and returns person and address only — never the ID
  document. The document is presented and captured again for every purchase. No separate seller master table: retention
  and anonymization of intakes cover it automatically.

### MRZ (ICAO 9303)

- `Mrz.Parse` (domain, C#) and `parseMrz` (frontend, TypeScript) implement TD1 (3×30, Personalausweis / Aufenthaltstitel),
  TD2 (2×36) and TD3 (2×44, Reisepass) with all check digits (document number incl. the long-number extension of TD1,
  birth date, expiry date, TD3 personal number, composite). Input as typed by keyboard-wedge document scanners (with or
  without line breaks) or pasted. Unit-tested with the ICAO and Bundesdruckerei specimens; a mutation check (check digits
  disabled) fails 10 tests.
- The frontend fills the form live; the request carries the MRZ text and the API parses it again and refuses data that
  differs from it (`ankauf.mrz_mismatch`, `ankauf.mrz_check_digit`, `ankauf.mrz_format`).
- **Camera OCR was tried and not shipped.** tesseract.js 7 (Apache-2.0) with the bundled `eng` traineddata (MIT package,
  Apache-2.0 data) misread rendered MRZ text in a test (`<` runs read as `KLLL…`, digits doubled) — the check digits would
  reject it, so it would only frustrate users, and it adds ~50 MB of assets. No OCR-B/MRZ-trained model with a clear
  licence was available offline. Keyboard-wedge scanners and paste cover the counter; camera capture stays open.

### QR self check-in (`ankauf.checkins`)

- `POST /api/ankauf/checkins` (inventory.manage) creates a check-in with a 256-bit random token (only its SHA-256 is stored),
  valid `Ankauf:CheckInMinutes` (15). The counter shows a QR code of `/checkin/{token}` and polls
  `GET /api/ankauf/checkins/{id}`.
- Public, anonymous, rate-limited per IP (`RateLimits:AnkaufCheckInPerMinute`, 20): `GET/POST /api/public/ankauf/checkins/{token}`
  through SECURITY DEFINER functions `ankauf.checkin_public` (shop name, expiry, submitted flag — nothing else) and
  `ankauf.checkin_submit` (writes once, only before expiry: 1 = written, 2 = already submitted, 0 = unknown/expired/used).
  The API validates like at the counter (age, expiry, PNG signature) and requires a privacy consent.
- Only the organization sees the data (RLS). The employee verifies it against the ID and receives the intake with
  `checkInId`; consuming is single-use (row version + trigger + unique index on `intakes.check_in_id`) and **removes the
  personal data from the check-in** (it now lives in the intake). The optional phone signature is attached to the intake as
  a `CheckInDeclaration` signature (confirms the data, not the price).
- Unused check-ins: their personal data is purged `Ankauf:CheckInPurgeHours` (24) after expiry by the retention job
  (`ankauf.checkins_due_for_purge`), independently of `SellerDataRetentionYears`.

### Signature (`ankauf.intake_signatures`)

- `POST /api/ankauf/intakes/{id}/signature {paidAmount, paymentMethod, image}` (Inspected intakes): PNG (signature + IHDR
  check, ≤ 300 kB, plausible size) drawn with pointer events (touch, pen with pressure, mouse).
- Stored: image, SHA-256 of the image, **data hash** = SHA-256 of a canonical text (version, declaration version, intake,
  organization, all seller fields, device + IMEI, inspection findings + offer + inspection time, agreed amount, currency,
  payment method), **binding hash** = SHA-256(data hash | image hash | signing time), signing time, user.
  Times are hashed with microsecond precision (what PostgreSQL stores).
- Completing requires the latest purchase signature to confirm exactly the current intake, amount and method
  (`ankauf.signature_required` / `ankauf.signature_invalid`); a re-inspection or another amount/method needs a new
  signature. `Ankauf:RequireSellerSignature` (default true) can be switched off for shops that sign on paper.
- Append-only: no DELETE, update only by the one-way anonymization that removes the image (hashes stay as evidence).
- Shown on the intake (seller card, signature state, timeline), in the HTML print view and **in the Ankaufbeleg PDF**
  (PNG converted to JPEG with `IImageCodec.ToJpeg`, drawn above the seller's signature line; section "Elektronische
  Unterschrift" with time, place and checksum).

### Payout from the till (Ankauf → POS contract)

- New contract `Pos.Contracts.ICashPayouts` (dependency map: Ankauf → Pos contracts only; POS never depends on Ankauf;
  architecture tests). For organizations with the POS module a cash purchase needs `registerId`
  (`ankauf.register_required`); the payout runs **inside the intake's completion transaction**: exclusive lock of the open
  session (`pos.no_open_session`), right `pos.cash_movement`, expected cash ≥ amount (`pos.insufficient_cash`), cash movement
  `Withdrawal` with the new system reason `Ankauf` and `source_reference = ankauf:{intakeId}` (unique per organization →
  one payout per intake), fiscal signature as `SonstigerVorgang` (`Entnahme^Ankauf^…`). Any failure rolls back stock and
  payout together. Bank transfers do not touch the till. Organizations without the POS module pay cash without a till
  (as before).
- `Ankauf` is not selectable for manual movements (`pos.reason_code_reserved`) and not listed in the reasons.
- Z-report / interim report: `summary.ankaufCount` and `ankaufPayoutsMinor` (subset of withdrawals; expected cash already
  reduced). DSFinV-K maps the movement to `Auszahlung` (to be confirmed by a tax adviser). The cashier token is not
  resolved on Ankauf endpoints; the movement records the signed-in user.

### Daily business report (Tagesbericht) — new module `Reporting`

- `GET /api/reports/daily?date=` (new permission `reports.read`, owner + manager). Owns no data; composes read contracts
  under the caller's RLS: `IPosDailyFigures` (sales/refunds gross, VAT by treatment, payment methods, deposits/withdrawals,
  Ankauf payouts, POS invoices, sessions), `IAnkaufDailyFigures` (received/completed/rejected, total and by payment method,
  devices — no seller data), `IOrderDailyFigures` (B2B purchases placed/received, sales received/shipped/completed),
  `IPaymentDailyFigures` (paid/refunded as buyer and seller). Sections appear only for enabled modules. Calendar day in
  Europe/Berlin (23/25-hour days handled). Totals: sold = till gross + B2B payments received; bought = Ankauf paid + B2B
  payments made. Printable (A4) in the back office (`/app/berichte/tagesbericht`) and the POS app (`/kasse/berichte/tag`).
- Nothing depends on Reporting; it may not use any module's Domain/Application (architecture tests).

### ID copies

**No ID photo or copy is stored** (data minimization, PAuswG §20: copies only with consent and marked "Kopie"). The UI says
so; the employee checks the original document. An optional copy was deliberately not built.

### UI

Intake wizard with four steps (customer & ID → inspection → price & signature → payout & stock), capture tabs (scan/MRZ,
QR self check-in, returning customer, manual), seller card, price breakdown, timeline, signature pad, till selection with
expected cash, HTML Ankaufbeleg; card-based intake list with summary tiles; the same pages run inside the POS app
(`/kasse/:registerId/ankauf/…`, Petrol design via the token remap, register preselected). Einkauf/Verkauf: status chips,
cards on phone/tablet, order timeline on the detail page. All texts in de, en, tr, es, ru, ar, fa; RTL via logical
properties.

## For legal review (no compliance claimed)

- Validity of a simple electronic signature (image + hash binding, no qualified signature) for the Eigentumserklärung and
  as receipt confirmation; whether paper signing must remain available (configuration exists).
- Retention period of seller data and signature images (`Ankauf:SellerDataRetentionYears`, still disabled), whether the
  stored hashes may outlive the images, and the 24-hour purge of unused check-ins.
- Whether nationality/issuing authority must be recorded at all (they are optional), and whether the document number must
  be stored in full (it is; the receipt masks it).
- Consent text of the self check-in form and the privacy notice; legal basis of processing.
- DSFinV-K business case for Ankauf payouts and the cash-book treatment of purchases from private persons (§25a).
- The decision not to store ID copies (PAuswG §20, GDPR).
