# ADR-0006: Marketplace payments through a licensed PSP, immutable double-entry ledger

- Status: Accepted (provider integration pending a PSP contract)
- Date: 2026-10-15

## Context

Online B2B sales move money from buyer to seller with a platform commission. HandyNet must not hold third-party
money in an ordinary bank account (German payment services regulation, ZAG), and every amount must be traceable.

## Decision

1. **Money flow through a licensed PSP** with marketplace/split support (planned: Mollie Connect, subject to
   contract). The buyer pays the PSP; the PSP releases the seller's share and keeps the platform fee. HandyNet only
   instructs the PSP and records obligations. Physical store (POS) sales are the dealer's own money and will not
   pass through this flow.
2. **Provider abstraction** `IPaymentProvider`: create payment, authoritative status lookup, cancel, refund,
   release-to-seller (idempotent per payment). Implemented today: `Simulated` (development/tests only, refused in
   Production, checkout page lets a developer pick the outcome). No real provider is claimed to work yet.
3. **Webhooks are hints.** The webhook carries only the provider's payment id; status is always fetched from the
   provider. Payment states are terminal (Paid/Failed/Canceled/Expired/Refunded never change back), so replays and
   out-of-order webhooks are harmless.
4. **Commission** = configured basis points (default 3 %) of the order's net goods value, plus 19 % VAT on the
   commission; both are deducted from the seller's payout and snapshotted on the payment.
5. **Payout only for `Completed` orders** (after the buyer's inspection window, ADR-0005), triggered by the order
   event via the outbox. Cancelled/rejected orders: paid payments are refunded, open ones cancelled. A payment that
   succeeds after the order was cancelled is refunded immediately.
6. **Ledger** (`ledger` schema): accounts (`PspClearing`, `SellerPayable` per seller, `CommissionRevenue`,
   `VatPayable`), journal entries and postings in integer cents (debit positive, credit negative).
   - The app role can only `SELECT`; the **only write path** is `ledger.post_entry(...)`, which rejects unbalanced
     entries, zero postings, unknown accounts and postings to another organization's seller account, and is
     idempotent by key (`payment:{id}:capture|refund|payout`).
   - Triggers forbid `UPDATE`/`DELETE` on entries and postings even for the owner role. Corrections are reversing
     entries (refund = exact negation of capture).
   - RLS: a tenant sees only its own accounts and postings.

Entries per paid order: capture `Dr PspClearing G / Cr SellerPayable (G−C−V) / Cr CommissionRevenue C / Cr VatPayable V`;
payout `Dr SellerPayable / Cr PspClearing`; refund = reverse capture.

## Not yet done (no compliance claimed)

Commission invoices to sellers (and their numbering/GoBD archive), reconciliation against PSP settlement reports,
disputes/chargebacks, partial refunds, seller onboarding/KYC at the PSP, DATEV export, and the real Mollie adapter.
