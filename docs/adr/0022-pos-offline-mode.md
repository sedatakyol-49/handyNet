# ADR-0022: Offline mode — sales recorded on the till and booked afterwards

- Status: Accepted (backend implemented and tested; device queue built separately — **not reviewed by a tax adviser**, see "Not done / not verified")
- Date: 2026-11-11
- Extends: ADR-0008 (gapless numbers, idempotency), ADR-0012 (TSE signing)

## Context

The till must keep selling when the API is unreachable (internet outage). Receipt numbers are gapless per register and
allocated in the receipt transaction, and every receipt is signed by the TSE through the server, so a device cannot
issue final receipts on its own.

## Decision

- Per register `offline_mode` (bool, default false; `UpdateRegisterRequest.offlineMode`, `pos.manage`; omitted = unchanged).
- The device queues sales with their Idempotency-Key and `offline: { recordedAt, reference }` (reference 1–40 of
  `[A-Za-z0-9-]`) and sends them on reconnect. The server:
  - refuses them unless the register has offline mode (`409 pos.offline_not_enabled`);
  - checks `recordedAt` is at most 5 minutes in the future and not older than `Pos:OfflineMaxAgeHours` (72)
    (`422 pos.offline_time_invalid`), the reference shape (`422 pos.offline_reference`);
  - allows cash and card only, no gift card sale or payment, no points redemption (`422 pos.offline_payment_not_allowed`,
    `pos.offline_gift_card_not_allowed`);
  - books the sale like any other: **server time, the next gapless number, the TSE signature at booking time** — numbers
    are never allocated offline;
  - stores `offline_recorded_at` and `offline_reference` on the immutable receipt (both or neither, sales only — check)
    and prints "Offline erfasst am <Berlin time> · Offline-Nr. <reference> · nachträglich gebucht und signiert";
  - keeps the reference unique per register (unique index; `409 pos.offline_reference_used` for a new key with a used
    reference — tested with 8 parallel requests, fails without the index), while a retry with the original
    Idempotency-Key returns the stored receipt.
- The offline record is part of the request fingerprint only when present (existing fingerprints unchanged).
- `ReceiptDto.offline { recordedAt, reference } | null`, `ReceiptSummaryDto.offline: bool`.
- DSFinV-K and the Z-report need no special handling: the receipt belongs to the session open at booking time.

## Consequences

- A sale recorded offline appears in the session and Z number of the day it is booked, not the day it was recorded.
- Stock is checked at booking; a sale whose stock is gone is refused and stays in the device queue for a decision.

## Not done / not verified

- **To be reviewed by a tax adviser**: the time between the offline record and its signature is a period without TSE
  protection; whether a provisional slip handed to the customer is acceptable; whether the booking date or the recording
  date is the relevant one for the cash book and DSFinV-K (`BON_START` is the booking time today); the 72-hour window.
- No server-side record of the device's provisional slips, no detection of devices that never sync, no limit on the
  amount sold offline.
- The device queue, reachability detection, provisional slip and service worker are frontend work outside this ADR.
