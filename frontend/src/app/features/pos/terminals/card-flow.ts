import { ReceiptPaymentCard, SalePayment } from '../../../core/api/models';
import { CardTransaction, TerminalProfile } from './terminal-api';
import { TerminalOutcome } from './terminal-transports';

/**
 * Pure rules of the card payment flow at the till (terminal → card transaction → sale). The point of every rule:
 * a card charge is never lost silently, and the cashier is always told what happened and what to do.
 */

/** Device choice stored per register: '' = the register's default profile, 'manual' = always confirm by hand, else a profile id. */
export const DEVICE_MANUAL = 'manual';

/** The profile a device uses for card payments; null = manual (the cashier confirms the payment as before). */
export function effectiveTerminal(profiles: readonly TerminalProfile[], choice: string | null): TerminalProfile | null {
  if (choice === DEVICE_MANUAL) return null;
  const p = (choice && profiles.find((x) => x.id === choice)) || profiles.find((x) => x.isDefault) || null;
  return p && p.connection !== 'Manual' ? p : null;
}

/** An approved card payment attached to an open ticket until the sale is booked (stored with the ticket). */
export interface OrderCard {
  transactionId: string;
  amountMinor: number;
  profileId: string | null;
  /** The terminal's receipt number of the payment — needed to reverse it over ZVT. */
  terminalReceiptNumber: string | null;
  cardName: string | null;
  maskedPan: string | null;
  /** False while the API does not know the result yet (it was unreachable after the terminal approved). */
  reported: boolean;
  /** The result to report again (only kept while `reported` is false). */
  pendingResult?: TerminalOutcome | null;
  test: boolean;
}

export function isOrderCard(v: unknown): v is OrderCard {
  if (!v || typeof v !== 'object') return false;
  const c = v as OrderCard;
  return typeof c.transactionId === 'string' && Number.isSafeInteger(c.amountMinor) && c.amountMinor > 0 && typeof c.reported === 'boolean';
}

export function orderCardFrom(transactionId: string, amountMinor: number, profile: TerminalProfile, outcome: TerminalOutcome, reported: boolean): OrderCard {
  return {
    transactionId,
    amountMinor,
    profileId: profile.id,
    terminalReceiptNumber: outcome.terminalReceiptNumber ?? null,
    cardName: outcome.cardName ?? null,
    maskedPan: outcome.maskedPan ?? null,
    reported,
    pendingResult: reported ? null : outcome,
    test: outcome.test,
  };
}

/** Sum of the card payments of a sale (the request carries at most one Card payment). */
export function cardAmount(payments: readonly SalePayment[]): number {
  return payments.filter((p) => p.method === 'Card').reduce((sum, p) => sum + p.amountMinor, 0);
}

/**
 * What validating the payment has to do about the card:
 *   none     – no card amount, nothing attached;
 *   manual   – card amount, no terminal: the cashier confirms (as before, no card transaction);
 *   charge   – run the terminal for the card amount;
 *   reuse    – an approved payment of exactly this amount is attached (retry after a failed sale request);
 *   mismatch – an approved payment of another amount is attached: restore the amount or reverse the payment first;
 *   orphan   – an approved payment is attached but the sale has no card amount any more: reverse it first.
 */
export type CardStep = 'none' | 'manual' | 'charge' | 'reuse' | 'mismatch' | 'orphan';

export function cardStep(card: OrderCard | null | undefined, cardMinor: number, terminal: TerminalProfile | null, manualConfirmed = false): CardStep {
  if (card) return cardMinor === 0 ? 'orphan' : card.amountMinor === cardMinor ? 'reuse' : 'mismatch';
  if (cardMinor === 0) return 'none';
  return terminal && !manualConfirmed ? 'charge' : 'manual';
}

/** The sale's payments with the card transaction on the Card payment. */
export function withCardTransaction(payments: readonly SalePayment[], transactionId: string | null): SalePayment[] {
  return payments.map((p) => (p.method === 'Card' && transactionId ? { ...p, cardTransactionId: transactionId } : { ...p }));
}

/**
 * After the sale request failed although the card was approved:
 *   keep    – outcome unknown (network error, 5xx): keep the payment attached and retry with the same Idempotency-Key;
 *   reverse – the API refused the sale: reverse the card payment on the terminal.
 */
export function afterSaleFailure(httpStatus: number): 'keep' | 'reverse' {
  return httpStatus === 0 || httpStatus >= 500 || httpStatus < 0 ? 'keep' : 'reverse';
}

/**
 * May the till reverse this transaction on the terminal? Only an approved payment that no receipt uses — a linked
 * one belongs to a booked sale (reverse that with a Storno instead).
 */
export function reversible(t: Pick<CardTransaction, 'status' | 'receiptId' | 'kind'>): boolean {
  return t.status === 'Approved' && !t.receiptId && t.kind === 'Payment';
}

/** Receipt number for a ZVT reversal (0–9999), or null when the terminal did not report one. */
export function reversalReceiptNumber(value: string | null | undefined): number | null {
  const text = (value ?? '').trim();
  if (!/^\d{1,4}$/.test(text)) return null;
  return Number(text);
}

/** "girocard ····0010" for messages; falls back to the card name or an empty string. */
export function cardLabel(c: { cardName?: string | null; maskedPan?: string | null }): string {
  const last4 = (c.maskedPan ?? '').replace(/\D/g, '').slice(-4);
  return [c.cardName, last4 ? `····${last4}` : ''].filter(Boolean).join(' ');
}

/** Translation key for a run that did not end in an approval (shown in the payment dialog). */
export function outcomeMessageKey(o: Pick<TerminalOutcome, 'status' | 'uncertain' | 'errorCode'>, operation: 'payment' | 'refund' | 'reversal' = 'payment'): string {
  if (operation === 'reversal') {
    if (o.uncertain) return 'pos.terminals.flow.reversalUncertain';
    if (o.status === 'Declined' || o.status === 'Aborted') return 'pos.terminals.flow.reversalRefused';
  }
  if (o.uncertain) return 'pos.terminals.flow.uncertain';
  if (o.status === 'Declined') return 'pos.terminals.flow.declined';
  if (o.status === 'Aborted') return 'pos.terminals.flow.aborted';
  return o.errorCode ? `pos.terminals.errors.${o.errorCode}` : 'pos.terminals.errors.terminal';
}

type Translate = (key: string, params?: Record<string, string | number | null | undefined>) => string;

/** Lines printed under a card payment on the receipt (screen and thermal): card, then terminal / trace / receipt / authorisation. */
export function receiptCardLines(card: ReceiptPaymentCard | null | undefined, t: Translate): string[] {
  if (!card) return [];
  const out: string[] = [];
  if (card.isTest) out.push(t('pos.receipt.card.test'));
  const label = cardLabel(card);
  if (label) out.push(label);
  const details = [
    card.terminalId ? t('pos.receipt.card.terminal', { id: card.terminalId }) : '',
    card.traceNumber ? t('pos.receipt.card.trace', { trace: card.traceNumber }) : '',
    card.terminalReceiptNumber ? t('pos.receipt.card.receipt', { number: card.terminalReceiptNumber }) : '',
    card.authCode ? t('pos.receipt.card.auth', { code: card.authCode }) : '',
  ].filter(Boolean);
  if (details.length) out.push(details.join(' · '));
  return out;
}
