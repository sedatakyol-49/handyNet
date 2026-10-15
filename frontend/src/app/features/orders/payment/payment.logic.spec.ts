import { Payment, PaymentStatus, Payout } from '../../../core/api/models';
import {
  buyerPaymentAction,
  ledgerKind,
  ledgerKindKey,
  openPayment,
  payoutForOrder,
  relevantPayment,
  sellerPayoutState,
  sellerSignedMinor,
} from './payment.logic';

const payment = (status: PaymentStatus, over: Partial<Payment> = {}): Payment => ({
  id: status + Math.random(),
  orderId: 'o1',
  orderNumber: 'B2B-001042',
  provider: 'Simulated',
  status,
  amountMinor: 71400,
  currency: 'EUR',
  checkoutUrl: status === 'Open' ? 'http://localhost:5170/api/payments/simulated/x' : null,
  createdAt: '2026-10-15T10:00:00Z',
  paidAt: null,
  refundedAt: null,
  commissionNetMinor: null,
  commissionVatMinor: null,
  sellerShareMinor: null,
  ...over,
});

const payout = (orderId: string): Payout => ({
  id: 'p-' + orderId,
  orderId,
  orderNumber: 'B2B-001042',
  amountMinor: 66402,
  currency: 'EUR',
  providerTransferId: 'SIMTR-1',
  createdAt: '2026-10-15T12:00:00Z',
});

describe('buyerPaymentAction', () => {
  it('offers a new payment for placed and confirmed orders without payments', () => {
    expect(buyerPaymentAction('Placed', [])).toBe('pay');
    expect(buyerPaymentAction('Confirmed', [])).toBe('pay');
  });

  it('offers no payment once the order is beyond Confirmed or closed', () => {
    for (const s of ['Shipped', 'Delivered', 'Disputed', 'Completed', 'Refunded', 'Cancelled', 'Rejected'] as const) {
      expect(buyerPaymentAction(s, [])).toBeNull();
    }
  });

  it('resumes an open payment', () => {
    expect(buyerPaymentAction('Placed', [payment('Open')])).toBe('resume');
    // An open payment without checkout link (seller view / provider gone) cannot be resumed – start again.
    expect(buyerPaymentAction('Placed', [payment('Open', { checkoutUrl: null })])).toBe('pay');
  });

  it('never offers payment when already paid or refunded', () => {
    expect(buyerPaymentAction('Confirmed', [payment('Paid')])).toBeNull();
    expect(buyerPaymentAction('Placed', [payment('Open'), payment('Paid')])).toBeNull();
    expect(buyerPaymentAction('Placed', [payment('Refunded')])).toBeNull();
  });

  it('allows a new attempt after failed, cancelled or expired payments', () => {
    expect(buyerPaymentAction('Placed', [payment('Failed'), payment('Canceled'), payment('Expired')])).toBe('pay');
  });
});

describe('relevantPayment', () => {
  it('prefers paid/refunded, then open, then the newest attempt', () => {
    const failed = payment('Failed');
    const open = payment('Open');
    const paid = payment('Paid');
    expect(relevantPayment([failed, paid])).toBe(paid);
    expect(relevantPayment([failed, open])).toBe(open);
    expect(relevantPayment([failed, payment('Expired')])).toBe(failed);
    expect(relevantPayment([])).toBeNull();
    expect(openPayment([failed, open])).toBe(open);
    expect(openPayment([failed])).toBeNull();
  });
});

describe('seller payout state', () => {
  it('is pending until the order is completed', () => {
    expect(sellerPayoutState('Confirmed', payment('Paid'), null)).toBe('pending');
    expect(sellerPayoutState('Delivered', payment('Paid'), null)).toBe('pending');
  });

  it('is processing after completion until the payout is visible', () => {
    expect(sellerPayoutState('Completed', payment('Paid'), null)).toBe('processing');
    expect(sellerPayoutState('Completed', payment('Paid'), payout('o1'))).toBe('paidOut');
  });

  it('is on hold while the order is disputed and refunding after a refund decision', () => {
    expect(sellerPayoutState('Disputed', payment('Paid'), null)).toBe('onHold');
    expect(sellerPayoutState('Refunded', payment('Paid'), null)).toBe('refunding');
    // Once the background job refunded the payment.
    expect(sellerPayoutState('Refunded', payment('Refunded'), null)).toBe('refunded');
    // Dispute withdrawn / decided for the seller: completed like any other order.
    expect(sellerPayoutState('Completed', payment('Paid'), null)).toBe('processing');
  });

  it('handles refunds and unpaid orders', () => {
    expect(sellerPayoutState('Cancelled', payment('Refunded'), null)).toBe('refunded');
    expect(sellerPayoutState('Placed', payment('Open'), null)).toBe('none');
    expect(sellerPayoutState('Completed', null, null)).toBe('none');
  });

  it('matches the payout by order id', () => {
    expect(payoutForOrder([payout('a'), payout('o1')], 'o1')?.id).toBe('p-o1');
    expect(payoutForOrder([payout('a')], 'o1')).toBeNull();
    expect(payoutForOrder(null, 'o1')).toBeNull();
  });
});

describe('ledger lines', () => {
  it('labels negative amounts as credits and positive ones as payouts', () => {
    expect(ledgerKind(-66402)).toBe('credit');
    expect(ledgerKind(66402)).toBe('debit');
    expect(ledgerKindKey({ amountMinor: -1 })).toBe('finance.kind.credit');
    expect(ledgerKindKey({ amountMinor: 1 })).toBe('finance.kind.debit');
  });

  it("shows amounts from the seller's perspective (credit +, payout −) with integer negation", () => {
    expect(sellerSignedMinor(-66402)).toBe(66402);
    expect(sellerSignedMinor(66402)).toBe(-66402);
    expect(Object.is(sellerSignedMinor(0), 0)).toBe(true);
  });
});
