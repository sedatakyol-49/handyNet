import { LedgerLine, OrderStatus, Payment, Payout } from '../../../core/api/models';

/** Order states in which the buyer may (still) pay online – mirrors the API (`payment.order_not_payable`). */
export const PAYABLE_ORDER_STATUSES: readonly OrderStatus[] = ['Placed', 'Confirmed'];

/** Which payment button the buyer sees: start a new payment, resume the open one, or none. */
export type BuyerPaymentAction = 'pay' | 'resume' | null;

/**
 * - an order that is already paid (or refunded) never offers payment again;
 * - an Open payment with a checkout link is resumed (the API would return the same payment anyway);
 * - otherwise a new payment can be started while the order is Placed/Confirmed (failed, cancelled and expired
 *   attempts do not block a new one).
 * Online payment is optional: orders may also be settled by invoice/transfer outside the platform.
 */
export function buyerPaymentAction(status: OrderStatus, payments: readonly Payment[]): BuyerPaymentAction {
  if (payments.some((p) => p.status === 'Paid' || p.status === 'Refunded')) return null;
  const payable = PAYABLE_ORDER_STATUSES.includes(status);
  if (!payable) return null;
  if (payments.some((p) => p.status === 'Open' && !!p.checkoutUrl)) return 'resume';
  return 'pay';
}

/** The payment that describes the order's money state: Paid/Refunded wins, then Open, else the newest attempt. */
export function relevantPayment(payments: readonly Payment[]): Payment | null {
  return (
    payments.find((p) => p.status === 'Paid' || p.status === 'Refunded') ??
    payments.find((p) => p.status === 'Open') ??
    payments[0] ??
    null
  );
}

/** Open payment of the order (newest first input), if any. */
export function openPayment(payments: readonly Payment[]): Payment | null {
  return payments.find((p) => p.status === 'Open') ?? null;
}

/** Seller's view of the payout for a paid order. */
export type SellerPayoutState =
  /** No paid online payment – settled outside the platform or not paid yet. */
  | 'none'
  /** Paid, order not completed yet: paid out after completion (end of inspection / acceptance). */
  | 'pending'
  /** Paid, but the buyer opened a dispute: no automatic completion and no payout until it is resolved. */
  | 'onHold'
  /** Dispute ended with a full refund; the payment is refunded by a background job shortly afterwards. */
  | 'refunding'
  /** Order completed, payout not yet visible (background processing, or older than the recent payouts list). */
  | 'processing'
  | 'paidOut'
  | 'refunded';

export function sellerPayoutState(
  orderStatus: OrderStatus,
  payment: Pick<Payment, 'status'> | null,
  payout: Payout | null,
): SellerPayoutState {
  if (payout) return 'paidOut';
  if (!payment) return 'none';
  if (payment.status === 'Refunded') return 'refunded';
  if (payment.status !== 'Paid') return 'none';
  if (orderStatus === 'Disputed') return 'onHold';
  if (orderStatus === 'Refunded') return 'refunding';
  return orderStatus === 'Completed' ? 'processing' : 'pending';
}

/** Payout of this order among the seller's recent payouts. */
export function payoutForOrder(payouts: readonly Payout[] | null | undefined, orderId: string): Payout | null {
  return payouts?.find((p) => p.orderId === orderId) ?? null;
}

// ---------------------------------------------------------------- Ledger

/** Ledger lines of the seller-payable account: negative = credit to the seller, positive = debit (payout). */
export type LedgerKind = 'credit' | 'debit';

export function ledgerKind(amountMinor: number): LedgerKind {
  return amountMinor < 0 ? 'credit' : 'debit';
}

/** Translation key of the type label ("Gutschrift" / "Auszahlung"). */
export function ledgerKindKey(line: Pick<LedgerLine, 'amountMinor'>): string {
  return ledgerKind(line.amountMinor) === 'credit' ? 'finance.kind.credit' : 'finance.kind.debit';
}

/**
 * Amount from the seller's perspective in cents: credits increase what the seller is owed (+),
 * payouts reduce it (−). Integer negation only – no floating-point math.
 */
export function sellerSignedMinor(amountMinor: number): number {
  return amountMinor === 0 ? 0 : -amountMinor;
}
