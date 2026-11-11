# ADR-0021: Card terminals — terminal profiles and card transactions linked to receipts

- Status: Accepted (backend implemented and tested; bridge and UI built separately — **not verified on hardware**, see "Not done / not verified")
- Date: 2026-11-11
- Extends: ADR-0008 (card payments "confirmed by the cashier"), ADR-0018 (printer profiles, SEHA bridge)

## Context

Card payments were recorded as confirmed by the cashier; the terminal was a separate device. The owner wants ZVT
terminals driven from the till. As with printers (ADR-0018) the server runs in a data centre and must not reach devices
in the shop's network: the browser (through the local SEHA bridge) talks to the terminal. The server's job is to record
every terminal attempt so that a card charge without a receipt is never lost, and to link a charge to exactly one receipt.

## Decision

### Terminal profiles (configuration)

`pos.terminal_profiles` per register, modelled on printer profiles: `name`, `connection` (`Manual` | `Simulated` |
`ZvtBridge`), for ZvtBridge `host`, `port` (default 20007), `bridgeUrl`, `zvtPassword` (6 digits, default `000000`;
`[PersonalData]` so the audit log never holds it), `isDefault` (one per register, partial unique index), `xmin`.
Configuration: DELETE allowed and audited. Read `pos.sell`, write `pos.manage`. Host and bridge URL are validated like
printer addresses; the server never connects to them.

**Simulated** approves without any card. It is refused in Production (`409 pos.terminal_simulated_not_allowed`) unless
`Pos:Terminal:AllowSimulatedInProduction` is set (staging), on create, on update and when a card transaction is started
with an existing simulated profile — so a profile created before the environment became Production cannot approve
payments there. Transactions made with it carry `isTest = true`. Tested both ways with a Production host.

### Card transactions (business records)

`pos.card_transactions`: one row per terminal attempt, written **before** the terminal is started (`Pending`), with
`kind` (Payment/Refund), `amountMinor`, the profile id and name (snapshot, no FK — profiles may be deleted), then the
final result reported by the till (`Approved` | `Declined` | `Aborted` | `Failed`) with terminal id, trace number,
terminal receipt number, card name, masked PAN, auth code, result code/text and the customer/merchant receipt texts
(≤ 4000). A PAN with more than the first 6 and last 4 digits is refused (`422 pos.card_pan_not_masked`) and never stored.

- Final results are immutable; a repeated identical report is accepted without change, a different one is
  `409 pos.card_transaction_final`. `Approved → Reversed` only while no receipt uses it
  (`pos.card_transaction_linked`, `pos.card_transaction_not_approved`). No DELETE (grant + trigger). The guard trigger
  enforces the same state machine for every role, also the owner.
- Starting a Refund needs `pos.refund` (or an approval), like a return.
- `GET /api/pos/card-transactions?registerId&status&unlinked&page&pageSize` lists them; `status=Approved&unlinked=true`
  is the managers' "card payments without a receipt" list.

### Linkage to receipts

`SalePaymentRequest.cardTransactionId` (method Card) and `ReturnRequest.cardTransactionId` (refund method Card): the
transaction is row-locked after the register and session locks and must be an Approved transaction of the **same
register**, of the right kind, over **exactly** the card amount, not linked yet — otherwise `409 pos.card_transaction_invalid`
with `reason` (`not_found`, `register`, `kind`, `status`, `amount`, `linked`). It is linked in the receipt's transaction;
a unique index on `receipt_id` and the guard trigger make a transaction settle **at most one receipt** (tested with 8
parallel sales; fails when the check is removed). The same applies to invoices paid at the till. Without an id the card
payment is "confirmed by the cashier" exactly as before. The receipt DTO returns `payments[].card` and the stored
document prints card name, masked PAN, trace number, auth code, terminal id and terminal receipt number under the payment.

## Consequences

- If the sale fails after the terminal approved, the approved transaction stays unlinked and visible; the till must
  reverse it on the terminal and report the reversal, or the manager handles it from the list.
- Request fingerprints include the card transaction only when present (unchanged for existing requests).

## Not done / not verified

- **Not verified on any hardware.** The ZVT bridge (`tools/print-bridge`) and the UI are built separately; this ADR
  covers the server side only. No ZVT or OPI certification, no PCI DSS assessment (the server only ever sees a masked PAN).
- The server cannot confirm that a reported result really came from a terminal: a till user with `pos.sell` can report
  "Approved". The record is an audit trail, not proof of payment; reconciliation with the acquirer's settlement is not built.
- No automatic expiry or alerting for `Pending` and unlinked `Approved` transactions; end-of-day (ZVT 06 50) totals are
  not reconciled with the Z-report.
- Tip, DCC, partial approvals and pre-authorisations are not supported.
