# ADR-0007: Ankauf price lists, versioned pricing policy and preliminary quotes

- Status: Accepted (business parameters are sample values pending confirmation)
- Date: 2026-10-15

## Decision

- **Monthly list** (XLSX, sheet "Mobilfunk" by default; header row auto-detected; German/English column names).
  The list price is the reference price for condition **Gut**. Rows without a usable price are reported as skipped.
- **Approval rules** per model, compared with the most recent earlier published price:
  new model → approval; any increase → approval; decrease > 30 % → approval; decrease ≤ 30 % or unchanged →
  automatic. A list can only be published when no entry is pending (approve or reject each).
- **Immutable price versions.** Publishing turns accepted/approved entries into `price_versions` rows; they are never
  edited (trigger-protected). **Only the current list counts**: the most recently starting published list whose
  validity covers today. A model missing from it, or whose new price was rejected, has *no* price — prices never
  silently carry over from a previous month, and expired lists offer nothing.
- **Versioned pricing policy** (`condition_policies`, immutable): condition adjustments (Neu +20 %, Wie neu +7 %,
  Sehr gut +5 %, Gut 0 %, Akzeptabel −20 %), brand and model overrides (model beats brand beats default), defect
  deductions (screen, housing, camera, Face ID/fingerprint, missing function), battery-health deduction below a
  threshold, platform margin, minimum dealer margin. Version 1 is seeded by migration from a literal.
- **Quote** = reference purchase price (Gut × condition × (1 − deductions), capped at 0), platform-to-dealer offer
  (− platform margin), maximum dealer-to-consumer price (− minimum dealer margin), and the dealer's gross margin for a
  given consumer price. A quote stores the price version and policy it used, is immutable, expires with its price
  (max. 7 days) and always states that the final payout is confirmed after physical inspection. Nothing is
  guaranteed for models without a current price.

## Open

Device intake (Ankauf transaction with seller identity/ID check, §25a margin-scheme documentation), inspection
workflow that turns a quote into a final price, stock entry of bought devices with IMEI, individual offers for
unpriced models, and confirmation of all percentages by the business.
