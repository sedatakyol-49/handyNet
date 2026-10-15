# ADR-0005: Fulfilment lifecycle and background jobs under RLS

- Status: Accepted
- Date: 2026-10-15

## Fulfilment

Order: `Placed → Confirmed → Shipped → Delivered → Completed` (side exits `Cancelled`, `Rejected`; from `Delivered` a
complaint leads to `Disputed`, which ends in `Completed` or `Refunded`).

- **A label is not a handover.** Creating a shipment only books the carrier (`LabelCreated`). The order becomes
  `Shipped` — and the reserved stock is consumed — when the seller declares the handover or the carrier reports a
  pickup scan. The order's `shipped_at` is the handover time, not the processing time.
- **Delivery** comes from carrier tracking or from the buyer ("Ware erhalten"). It starts the B2B inspection window
  (`Ordering:InspectionWindowHours`, default 48 h, subject to legal review) at the *actual* delivery time.
- The buyer can accept early (`Completed`); otherwise a job completes the order when the window has passed.
  A complaint during the window (Disputes module) stops automatic completion; payouts happen only for `Completed`
  orders, and `Refunded` orders refund the buyer.
- Shipping validates parcels against a configurable carrier profile: weight/length, standard liability vs. insured
  value (goods above the liability must be insured; above the insurable maximum they are refused) and lithium
  batteries (devices vs. loose batteries). Defaults are placeholders until real carrier contracts exist.
- Carriers are adapters behind `ICarrierAdapter`. Implemented: `Manual` (tracking number entered by the seller, no
  label, delivery confirmed by buyer) and `Simulated` (clearly marked fake label and tracking, refused at startup in
  Production). DHL/DPD/GLS/UPS adapters need API credentials and will be added and verified against the carriers'
  sandboxes — none is claimed to work today.
- Tracking events are append-only and de-duplicated per shipment by the carrier's event id, so polling is idempotent.

## Background jobs and RLS

Jobs have no user and therefore no tenant, so RLS would hide every tenant-owned row. We do **not** give the app a
`BYPASSRLS` role. Instead:

1. A narrow `SECURITY DEFINER` function owned by the migration role returns **only ids and the owning
   organization** of due work (`shipping.shipments_due_for_tracking`, `ordering.orders_due_for_completion`).
2. For each item the job opens a fresh DI scope and calls `TenantContext.SetSystemActor(organizationId)`; all reads
   and writes then run under that tenant's RLS policies, through the same domain code as user requests.
3. System actors get no endpoint permissions; they exist only inside job code.

The outbox dispatcher and the email worker only touch infrastructure tables (`infra`, `notifications`) and need no
tenant.
