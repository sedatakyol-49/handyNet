# ADR-0012: Fiscal layer — TSE modes, simulated test TSE, fiskaly adapter, outages, DSFinV-K export

- Status: Accepted (test operation only — **not certified, not reviewed by a tax adviser**, see "Not done")
- Date: 2026-10-18
- Extends: ADR-0008 (POS), which had only a `Disabled` test signer.

## Context

KassenSichV (§146a AO) requires every till transaction to be signed by a certified technical security device (TSE),
receipts to show the TSE data (incl. a QR code per DSFinV-K Anhang I / AEAO), TSE outages to be handled ("Ausfall der
Sicherheitseinrichtung") and the records to be exportable as DSFinV-K. No real TSE or fiskaly credentials exist yet.
The product owner wants a flexible **test TSE** configurable by the platform admin now, and a clean path to a real one.

## Decision

### Fiscal mode per organization and register

- `pos.fiscal_configurations` (RLS, no DELETE grant, audited): one organization row (mode required) and optional
  register rows whose null fields inherit. Modes: `Disabled` (no TSE, "TSE nicht angebunden – Testbetrieb"),
  `SimulatedTse` (test), `FiskalyCloudTse` (real adapter, sandbox/production). Effective mode = register → organization →
  `Pos:FiscalSigner` (default for organizations without a row). Set by the platform admin only
  (`/api/admin/pos/fiscal…`, processed per organization as its system actor so RLS applies, like the invoicing runner).
- **Register ↔ TSE client**: the register row's `client_id` (default: the register id; for fiskaly a UUID). It is the
  Kassen-Seriennummer of the QR code.
- **Production gates**: `Disabled` and `SimulatedTse` are refused at startup as `Pos:FiscalSigner` in Production; the
  simulated TSE only with `Pos:Fiscal:AllowSimulatedTseInProduction=true` (staging). The same gates apply per
  organization at sale time (`pos.fiscal_disabled_in_production`, `pos.fiscal_simulated_not_allowed`) and in the admin
  API. `Pos:FiscalSigner=FiskalyCloudTse` is refused (fiskaly needs per-organization TSS and credentials).
- **Secrets**: fiskaly API key/secret are write-only in the admin API and stored encrypted with ASP.NET Data Protection
  (`[PersonalData]`, so the audit log shows `[personal data]`), or referenced by name from configuration
  (`Pos:Fiskaly:Credentials:{name}:ApiKey|ApiSecret`, user-secrets / environment / secret store). Nothing in the repo.

### Signing flow (`FiscalSealer`, inside the receipt transaction)

- Receipts (sale, storno, return): processType `Kassenbeleg-V1`, processData
  `Beleg^<19 %>_<7 %>_<10,7 %>_<5,5 %>_<0 %>^<Betrag>:Bar_<Betrag>:Unbar` (gross per VAT bucket, two decimals, `.`;
  refunds negative; §25a lines count into the 19 % bucket with their gross — to be confirmed by a tax adviser). Storno
  is a `Beleg` with negative amounts (DSFinV-K `BON_STORNO = 1`), not `AVBelegstorno` — to be reviewed.
- Cash movements and the daily closing: processType `SonstigerVorgang`, HandyNet-defined data
  (`Einlage^ChangeFund^100.00`, `Kassenabschluss^3 Belege^25.00`), stored append-only in `pos.fiscal_records`.
- Start and finish are retried (`Pos:Fiscal:MaxAttempts`, default 3) with the **same transaction key** (receipt /
  event id): adapters are idempotent per key, so a retry after a lost response returns the stored signature and never
  signs twice. Sale retries with the same Idempotency-Key return the stored receipt (no new TSE call).
- **Outage path**: if the TSE stays unavailable (or is down), the sale still completes; the receipt gets status
  `Failed`, notice "Sicherheitseinrichtung ausgefallen" (printed) and the error; `pos.fiscal_outages` records the outage
  per register (start = first failed signature, with session; end = next successful signature; one open outage per
  register, only `ended_at` can be set, once — trigger). DSFinV-K `TSE_TA_FEHLER` carries "TSE ausgefallen".
- Receipt fiscal fields (mode, test flag, client id, process type/data, algorithm, time format, public key, QR payload,
  error, plus the existing serial/counters/signature/times) are columns of `pos.receipts`, protected by the existing
  GoBD triggers (no UPDATE/DELETE even for the owner). The printable document stores them as issued.

### Simulated TSE (`SimulatedTseSigner`, test only)

- One TSE per organization (`pos.simulated_tses`): ECDSA P-256 (`ecdsa-plain-SHA256`) or P-384
  (`ecdsa-plain-SHA384`), serial number = SHA-256 of the public key (hex), public key = uncompressed point (Base64),
  time format `unixTime`; private key PKCS#8 encrypted with Data Protection.
- Transactions (`pos.simulated_tse_transactions`): monotonic transaction number; signature counter +1 for the start
  log and +1 for the finish log; signatures (plain r‖s, Base64) over a documented text message
  `HandyNet-SimTSE-V1|op|serial|client|tx|counter|unixTime|processType|processData` (not the BSI TR-03151 ASN.1 log
  message) — verifiable with the public key (tests do). Triggers: identity and key immutable, counters never decrease,
  a transaction is finished once and never deleted.
- Admin-configurable behaviour (organization or register): latency (ms), failure rate % (each failure randomly before
  processing or after it — a lost response), forced outage.
- Its state lives in the caller's database transaction (row lock on the TSE serializes signatures): unlike a real TSE,
  a rolled-back sale also rolls back its TSE transaction, keeping test data consistent.
- Every signed receipt and export says **"Test-TSE – nicht für den Echtbetrieb"** (`isTest`).

### fiskaly adapter (`FiskalyTseSigner`, **not verified against the real service** — see the 2026-10-26 addendum)

- SIGN DE v2 shape: `POST /auth` (token cached per key, refreshed once on 401), `PUT /tss/{tss}/client/{client}` (once
  per process), `PUT /tss/{tss}/tx/{tx}?tx_revision=1` (`ACTIVE`) and `…=2` (`FINISHED` with
  `schema.standard_v1.receipt` or `schema.raw` for `SonstigerVorgang`), `GET /tss/{tss}/tx/{tx}` to resolve a revision
  conflict after a retry. Typed `HttpClient` with timeout; timeouts/429/5xx retried with the same revision. Fully
  config-driven (`Pos:Fiskaly:*`). Unit-tested with a fake HTTP handler only. TSS setup (admin PIN, initialization)
  and client deregistration are out of scope. A sandbox TSS is treated as test (`isTest`).

### DSFinV-K export (basic, not certified)

`GET /api/pos/exports/dsfinvk?registerId&from&to` (`pos.manage`, business days of closed sessions, ≤ 366 days): ZIP
with `index.xml` (GDPdU description), `LIESMICH.txt` (disclaimer) and CSV (UTF-8, `,`, `"`, `.`, CRLF, header row):
transactions, transactions_vat, transactions_tse, datapayment, references, lines, lines_vat, cashpointclosing,
location, cashregister, vat, tse, businesscases, payment, cash_per_currency. `Z_NR` = closing number of the register in
closing order; `Z_KASSE_ID` = register id. Not implemented: STNR, Bonpos_Preisfindung, Bonpos_Zusatzinfo,
Stamm_Agenturen, Stamm_Terminals, slaves, allocation groups, line-level references, GTIN/Warengruppe; cash movements
appear only as business cases (no own Bon). Simulated/sandbox TSE rows are marked `TEST-TSE`.

### UI

Admin tab "Kassen & TSE" (modes, simulated behaviour, fiskaly settings with write-only credentials, TSE status with
serial/counters/last signature, register overrides, outages). Till: TSE pill in the top bar (green/test/outage),
outage banner, TSE block on the receipt (serial, client, transaction no., signature counter, start/end, algorithm,
shortened signature, test label, outage marker) and a QR code rendered locally (`qrcode-generator`, MIT, no CDN).

## Consequences

- Remote TSE calls inside the DB transaction hold the register lock (as ADR-0008 noted); the simulated latency makes
  that visible in tests.
- A failed DB commit after a fiskaly finish leaves a signed transaction at fiskaly without a receipt (not cancelled yet).
- Losing the Data Protection key ring makes stored fiskaly credentials unreadable (re-enter them) and the simulated
  TSEs unusable (their private keys) — persist the key ring (`DataProtection:KeysPath`).

## Not done (no compliance claimed)

Certified TSE in production (fiskaly verification in the sandbox first), BSI TR-03151 log messages/TAR export, TSE
certificate in `tse.csv`, cancel of orphaned TSE transactions, notification of the till to the tax office (§146a Abs. 4
AO), full DSFinV-K (see above) and its validation with the DSFinV-K validator, offline mode, and an external review by a
tax adviser of process data, storno handling, §25a mapping and the export.

## Addendum (2026-10-26): fiskaly contract review against the SIGN DE v2 spec

**Verified against the published OpenAPI spec** (`https://workspace.fiskaly.com/specs/sign-de-v2.json`) with
spec-shaped recorded responses (`FiskalyContractTests`) and an in-process fake in the integration tests:

- `POST /auth` (`api_key`/`api_secret` → `access_token`, `access_token_expires_in`, `access_token_claims.env` TEST/LIVE);
  a 401 refreshes the token once. One real call was made with deliberately invalid keys: the TEST middleware answered
  `401 {"message":"Invalid user credentials","status_code":401,"error":"Unauthorized"}` (no `code`); this is handled.
- Base URL: the middleware (`kassensichv-middleware.fiskaly.com/api/v2`) serves all operations and is the only host for
  `createClient`, `updateTss` and `upsertTransaction` ("only available via Middleware"); TEST or LIVE is bound to the
  key, not the URL. So there is one base URL setting, not one per environment.
- Fixed mismatches:
  - `createClient` **requires admin authentication**. The till no longer creates clients; it only checks
    `GET …/client/{id}` is `REGISTERED` (otherwise outage path). Registration is an admin action with the TSS admin PIN
    (admin auth → create → logout). A wrong PIN is not retried, because five failures block it.
  - TSS, client and transaction ids must be **UUIDv4**. HandyNet's v7 keys are mapped deterministically, so a retry
    reaches the same fiskaly resource.
  - `signature.counter` is a `BigintCounter` string.
  - A FINISHED response without complete signature data is treated as transient. It is never stored as a signed receipt.
  - `_env: TEST` marks the receipt as a test receipt even if the configuration says Production. Outage receipts of a
    sandbox TSS are marked as test too.
  - Retries now use exponential backoff and honour `Retry-After`. They cover 499 and `503 E_TSS_LOCKED`. 4xx responses
    are not retried and report fiskaly's `code`/`message`. A 423 (TSS defective/deleted/evicted) is refused.
  - Timeouts are set per request. TSS administration calls get at least 30 s.
- `standard_v1.receipt`: `receipt_type` RECEIPT, `amounts_per_vat_rate` (NORMAL / REDUCED_1 / SPECIAL_RATE_1 /
  SPECIAL_RATE_2 / NULL by rate) and `amounts_per_payment_type` (CASH / NON_CASH). Amounts match `^-?\d+(\.\d{2,5})$`.
  Revision 1 carries no schema, as the spec requires. Other events use `raw`. The TSS lifecycle calls (create,
  UNINITIALIZED, admin PIN from the PUK, admin auth, INITIALIZED, DISABLED, logout) are in `FiskalyApiClient` for setup
  tooling and the sandbox test.
- Admin "Verbindung testen" is read-only. It reports the environment, a mismatch, the TSS state and the client
  registration of each till. The till shows "Testumgebung – Belege nicht steuerlich gültig" while on Sandbox or the
  simulated TSE.

**Still needs a real TEST run** (credentials pending) with the opt-in `FiskalySandboxTests` (see operations.md). Open
points include:

- the exact 401/403 bodies of admin auth and createClient;
- the `qr_code_data` format;
- how long the UNINITIALIZED step takes;
- the conflict behaviour of a repeated revision with a different body;
- client limits.

Not used or not implemented: TSE export (TAR); CANCELLED for orphaned transactions (open ACTIVE transactions count
against the 2000 limit); refresh-token auth; client deregistration; TSS provisioning from the UI. HandyNet has no 7 % VAT
treatment yet. The adapter maps by rate, so 7 % totals would land in REDUCED_1.

**Switching an organization to LIVE:**

1. Create new LIVE API keys in fiskaly. TEST keys never work on LIVE.
2. Create and initialize a new LIVE TSS. A TEST TSS, its clients and its transactions cannot be moved: test data is not
   migrated.
3. Enter the LIVE keys and the TSS id, with environment Production.
4. Run "Verbindung testen". It must show LIVE and no mismatch.
5. Register every till with the LIVE TSS admin PIN, then sell.

Receipts signed on TEST stay test receipts.
