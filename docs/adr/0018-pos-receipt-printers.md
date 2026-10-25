# ADR-0018: Receipt printers for the till (network, USB, integrated) configured by the tenant

- Status: Accepted (generic support; not verified on specific printer models — see "Not done")
- Date: 2026-10-26

## Context

Until now the till printed only through the browser's print dialog (`window.print()` on an 80 mm print layout, ADR-0008).
Customers asked to configure their own receipt printers: network printers by IP, USB printers and the "normal"
(integrated / system) printer. No hardware models were named, so support must be generic. SEHA runs in the browser
and the server is in a data centre: it cannot (and must not) reach printers in the shop's LAN.

## Decision

### 1. Printer profiles per register, stored server-side

`pos.printer_profiles` (tenant-owned, RLS `tenant_isolation`, composite FK to the organization's register): name,
connection, paper width (58 / 80 mm), characters per line (24–64), auto-print, open the cash drawer on cash payment,
copies (1–5), cut paper, print the TSE QR code, code page (CP858 / PC437) and connection-specific fields (host, port,
https, ePOS device id, bridge URL, USB vendor/product id, baud rate). Fields that do not belong to the connection type are
cleared by the domain (`PrinterProfile.Update`), check constraints guard the shapes in the database. Exactly one profile
per register is the **default** (partial unique index `ux_printer_profiles_one_default`): the first profile becomes the
default, "make default" moves the flag in one transaction, deleting the default promotes the remaining profile with the
first name. Optimistic concurrency with `version` (`xmin`) on update.

Endpoints (module `Pos`):

| Method | Route | Permission |
|---|---|---|
| GET | `/api/pos/registers/{id}/printers` | `pos.sell` |
| GET | `/api/pos/printers/{id}` | `pos.sell` |
| POST | `/api/pos/registers/{id}/printers` | `pos.manage` |
| PUT | `/api/pos/printers/{id}` (body incl. `version`) | `pos.manage` |
| POST | `/api/pos/printers/{id}/default` | `pos.manage` |
| DELETE | `/api/pos/printers/{id}` | `pos.manage` |

Printer profiles are **configuration, not business records**: the app role has `DELETE` on this table (unlike receipts
and documents); every change is in the audit log (`IAuditable`).

**Which profile a device uses** is a per-device choice in `localStorage` (`hn.pos.printer.<registerId>`: empty = the
register's default, `browser` = always the print dialog, or a profile id). Two devices at the same register can use
different printers; a device whose chosen profile was deleted falls back to the register default. Without any profile the
till behaves as before (print dialog, no auto-print).

### 2. The server never connects to printers

All printing happens **in the till's browser**. The API stores configuration only; it never opens a connection to a
printer, a bridge or any address taken from a profile (no SSRF surface). Host and bridge URL are validated as an address
(no scheme/path/credentials) and an http(s) URL without credentials, so a profile cannot smuggle a URL into other fields.

### 3. Connection types and browser limits

| Connection | How | Limits |
|---|---|---|
| `Browser` (system / integrated printer) | Print dialog. Receipts: the on-screen receipt (`hn-print-area`), 58 mm via a named `@page`. Test page: hidden iframe with the layout sized to the paper | Any printer the OS knows (also the integrated printer of POS terminals/tablets). Cannot open the drawer; dialog unless the browser runs in kiosk printing mode |
| `EpsonEpos` | `POST http(s)://<host>[:port]/cgi-bin/epos/service.cgi?devid=…` with ePOS-Print XML (SOAP envelope) | ePOS-Print must be enabled on the printer. CORS is answered by the printer. **Mixed content:** an https SEHA page cannot call an http printer — use the printer's https endpoint (accept its self-signed certificate once at `https://<ip>`) |
| `StarWebPrnt` | `POST http(s)://<host>[:port]/StarWebPRNT/SendMessage` with WebPRNT builder XML | Same CORS / mixed-content caveats as ePOS |
| `EscPosBridge` | ESC/POS bytes to the **SEHA Print Bridge** on the till PC, which forwards them to `host:port` (raw TCP, default 9100) | Browsers cannot open TCP sockets; the bridge is required. `http://localhost` is not mixed content |
| `WebUsb` | ESC/POS over WebUSB (bulk OUT endpoint, printer class preferred, 16 KiB chunks) | Chromium only (feature-detected), secure context. Pairing per browser via `navigator.usb.requestDevice` (vendor/product id remembered in the profile). **Windows:** the installed printer driver usually owns the device → claim fails ("device busy"); then use WebSerial (virtual COM port driver) or the print dialog |
| `WebSerial` | ESC/POS over WebSerial (`navigator.serial`, baud rate from the profile) | Chromium only, secure context; serial printers or USB printers with a virtual COM port driver |

On **any** printer error the till falls back to the print dialog and says why (toast with the reason; the printer page
shows the status of the test print). Error classes: unsupported browser, insecure context, mixed content (detected
before sending), unreachable (network/CORS — the browser does not tell them apart), timeout (15 s), printer error
(ePOS/WebPRNT response code, e.g. `EPTR_COVER_OPEN`), bridge error, no paired device, device busy, chooser cancelled.

### 4. One layout, four renderers (frontend, pure, unit-tested)

`receipt-layout.ts` turns the **stored receipt document** (unchanged fiscal data, ADR-0008/0012) into lines that fit the
characters per line (word wrap, right-aligned amounts, double size for the total), rules, feeds and the TSE QR payload,
in the same order as the on-screen receipt. Renderers: ESC/POS bytes (`escpos.ts`: `ESC @`, `ESC t` code page, `ESC a`,
`ESC E`, `GS !`, `ESC d`, `GS V 66` partial cut, `ESC p` drawer kick on pin 2, `GS ( k` QR model 2 / level M),
ePOS-Print XML (`<text align em dw dh>`, `<symbol type="qrcode_model_2">`, `<cut>`, `<pulse>`), Star WebPRNT XML
(`<alignment>`, `<text emphasis width height>`, `<qrcode>`, `<cutpaper>`, `<peripheral>`) and HTML for the iframe.

**Characters.** CP858 (= CP850 with € at 0xD5) prints German umlauts, ß, § and €; PC437 has the umlauts but no €/§
(printed as `EUR` / `Par.`). Other characters are transliterated (ş → s, – → -, … → ...), no-break spaces from `Intl`
become spaces, unknown glyphs become `?`. Layout is computed on the transliterated text, so columns match the paper.
Thermal receipts are printed with **German labels**: the code pages have no Cyrillic/Arabic glyphs and the document's
own texts are German. The browser print keeps the UI language. Copies (1–5) repeat the receipt with a cut after each;
the drawer is kicked once before the first copy, only for receipts with a cash payment. With auto-print off and
"open drawer" on, a drawer-only job is sent after a cash sale.

### 5. SEHA Print Bridge contract

A small local HTTP service on the till PC (reference implementation without dependencies: `tools/print-bridge/seha-print-bridge.mjs`):

- listens on **127.0.0.1 only** (default port 9123);
- `POST /print`: body = raw ESC/POS (`application/octet-stream`, ≤ 1 MiB); target in `X-Printer-Host` / `X-Printer-Port`
  (default 9100); answers `200 {"ok":true}` or `4xx/5xx {"ok":false,"error":"…"}`; `GET /status` lists the printers;
- **security:** answers CORS only for `ALLOWED_ORIGINS` (other origins get 403 and no CORS headers), forwards only to
  `ALLOWED_PRINTERS` (`host:port` allow list — the bridge is not an open TCP relay for any web page), optional
  `BRIDGE_TOKEN` (Bearer); preflights include `Access-Control-Allow-Private-Network: true` for Chrome's local network
  access checks (Chrome may additionally ask the user for permission to access local devices).

The SEHA client sends to the bridge URL from the profile; the server never calls it.

### 6. UI

Kasse → register → **Drucker** (`/kasse/:registerId/drucker`, tab for everyone with `pos.sell`): device choice,
WebUSB/WebSerial availability, profile cards with **Testdruck**, "pair on this device" for USB/serial, and with
`pos.manage` create / edit / make default / delete. Help texts per connection type explain the https/mixed-content,
CORS, driver and browser limits. The till's "print receipt", auto-print after a sale and the reprint in the receipt
detail use the device's profile.

## Consequences

- Shops can use network printers without any software on the till (Epson ePOS / Star WebPRNT), raw-9100 printers with
  the bridge, USB printers in Chromium, and every other printer through the print dialog as before.
- A printer failure never blocks a sale: the receipt is already stored; printing falls back to the dialog.
- USB pairing and the device choice live in the browser: a new till PC must pair once and pick its printer.

## Not done

- Verification on real devices: the ePOS-Print and WebPRNT requests follow the published formats and the ESC/POS
  commands the Epson reference; they are unit-tested as bytes/XML, not on hardware. Code page numbers (`ESC t 19` for
  CP858) differ on some non-Epson printers.
- Logo printing on thermal paper (raster `GS v 0`), receipt languages other than German on thermal paper, printing
  Z-reports, invoices and Ankauf documents through the profile.
- Printer status polling (paper low, cover open) before printing; Epson ePOS SDK / Star SDK features beyond printing;
  ePOS "device id" discovery; WebPRNT `checkedblock`.
- An installer, autostart or update mechanism for the bridge; TLS on the bridge; Windows/macOS/Linux packaging.
- Silent printing for the browser connection (Chrome `--kiosk-printing` is a browser setting, not configurable here).
- Any certification or legal review of printed receipts (no compliance claim).
