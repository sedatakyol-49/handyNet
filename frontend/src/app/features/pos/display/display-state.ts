import { CartLine, lineAmounts } from '../pos.logic';

/**
 * Customer display (second screen / tablet): the state the till publishes and the display renders.
 * It never contains personal data — no customer name, email, phone, no line notes, no cashier.
 */
export type DisplayPhase = 'idle' | 'cart' | 'payment' | 'done';

export interface DisplayLine {
  description: string;
  quantity: number;
  unitPriceMinor: number;
  discountMinor: number;
  lineGrossMinor: number;
}

export interface DisplayPayment {
  /** Amount to pay (total incl. cash rounding). */
  dueMinor: number;
  tenderedMinor: number;
  changeMinor: number;
  /** 'Cash', 'Card', later 'GiftCard'; 'Mixed' for split payments; null while nothing is chosen. */
  method: string | null;
}

export interface DisplayLoyalty {
  points: number;
  redeemValueMinor: number;
}

export interface DisplayState {
  phase: DisplayPhase;
  lines: DisplayLine[];
  totalMinor: number;
  discountTotalMinor: number;
  payment?: DisplayPayment;
  loyalty?: DisplayLoyalty;
  /** Shop text for the idle / done screen (company receipt texts); the display has defaults. */
  message?: string;
  /** Public e-receipt link shown as a QR code after the sale. */
  receiptUrl?: string;
  currency: 'EUR';
  /** UI language of the till (the display follows it). */
  locale: string;
}

/** Limits that keep a state well below the API's 16 KiB (remote display) and readable on a screen. */
export const DISPLAY_LIMITS = { lines: 40, description: 80, message: 240, receiptUrl: 500, bytes: 16 * 1024 } as const;

export function idleDisplayState(locale: string, message?: string | null): DisplayState {
  return withMessage({ phase: 'idle', lines: [], totalMinor: 0, discountTotalMinor: 0, currency: 'EUR', locale }, message);
}

/** What the till screen shows right now (input of the reducer). */
export interface TillSnapshot {
  /** False without an open cash session (or when the till page is not shown). */
  sessionOpen: boolean;
  screen: 'products' | 'payment' | 'receipt';
  lines: readonly CartLine[];
  /** Payment screen: figures of `settlePayments()`. */
  settlement?: { paidMinor: number; changeMinor: number; cashMinor: number; cardMinor: number; roundingMinor?: number } | null;
  /** Receipt screen: the issued receipt (or the provisional offline one). */
  receipt?: {
    totalGrossMinor: number;
    changeMinor: number;
    roundingMinor?: number;
    lines: readonly { description: string; quantity: number; unitPriceMinor: number; discountMinor: number; lineGrossMinor: number }[];
    payments: readonly { method: string; amountMinor: number; tenderedMinor?: number | null }[];
  } | null;
  receiptUrl?: string | null;
  /** Loyalty points redeemed on the ticket and the price reduction they give. */
  loyalty?: { points: number; valueMinor: number } | null;
  idleMessage?: string | null;
  doneMessage?: string | null;
  locale: string;
}

/** Pure reducer: till screen → display state. */
export function displayStateFromTill(t: TillSnapshot): DisplayState {
  if (!t.sessionOpen) return idleDisplayState(t.locale, t.idleMessage);

  if (t.screen === 'receipt' && t.receipt) {
    const r = t.receipt;
    const lines = r.lines.map((l) => line(l.description, l.quantity, l.unitPriceMinor, l.discountMinor, l.lineGrossMinor));
    const paid = r.payments.reduce((sum, p) => sum + (p.tenderedMinor ?? p.amountMinor), 0);
    const state: DisplayState = {
      phase: 'done',
      lines: lastLines(lines),
      totalMinor: r.totalGrossMinor,
      discountTotalMinor: lines.reduce((sum, l) => sum + l.discountMinor, 0),
      payment: {
        dueMinor: r.totalGrossMinor + (r.roundingMinor ?? 0),
        tenderedMinor: paid,
        changeMinor: r.changeMinor,
        method: methodOf(r.payments.filter((p) => p.amountMinor !== 0).map((p) => p.method)),
      },
      currency: 'EUR',
      locale: t.locale,
    };
    const url = t.receiptUrl?.trim();
    if (url && url.length <= DISPLAY_LIMITS.receiptUrl) state.receiptUrl = url;
    return withMessage(state, t.doneMessage);
  }

  const lines = t.lines.map((l) => {
    const a = lineAmounts(l);
    return line(l.description, l.quantity, a.unitPriceMinor ?? 0, a.error ? 0 : a.discountMinor, a.grossMinor ?? 0);
  });
  if (!lines.length) return idleDisplayState(t.locale, t.idleMessage);

  const state: DisplayState = {
    phase: 'cart',
    lines: lastLines(lines),
    totalMinor: lines.reduce((sum, l) => sum + l.lineGrossMinor, 0),
    discountTotalMinor: lines.reduce((sum, l) => sum + l.discountMinor, 0),
    currency: 'EUR',
    locale: t.locale,
  };
  const redeemed = t.loyalty && t.loyalty.points > 0 ? t.loyalty : null;
  if (redeemed) state.loyalty = { points: redeemed.points, redeemValueMinor: redeemed.valueMinor };
  if (t.screen !== 'payment') return state;
  const s = t.settlement;
  return {
    ...state,
    phase: 'payment',
    payment: {
      dueMinor: state.totalMinor - (redeemed?.valueMinor ?? 0) + (s?.roundingMinor ?? 0),
      tenderedMinor: s?.paidMinor ?? 0,
      changeMinor: s?.changeMinor ?? 0,
      method: methodOf([...(s?.cashMinor ? ['Cash'] : []), ...(s?.cardMinor ? ['Card'] : [])]),
    },
  };
}

function line(description: string, quantity: number, unitPriceMinor: number, discountMinor: number, lineGrossMinor: number): DisplayLine {
  return { description: clip(description, DISPLAY_LIMITS.description), quantity, unitPriceMinor, discountMinor, lineGrossMinor };
}

/** The display shows the newest lines; totals always cover the whole cart. */
function lastLines(lines: DisplayLine[]): DisplayLine[] {
  return lines.length > DISPLAY_LIMITS.lines ? lines.slice(lines.length - DISPLAY_LIMITS.lines) : lines;
}

function methodOf(methods: string[]): string | null {
  const unique = [...new Set(methods)];
  return unique.length === 0 ? null : unique.length === 1 ? unique[0] : 'Mixed';
}

function withMessage(state: DisplayState, message: string | null | undefined): DisplayState {
  const text = message?.trim();
  return text ? { ...state, message: clip(text, DISPLAY_LIMITS.message) } : state;
}

function clip(text: string, max: number): string {
  return text.length > max ? text.slice(0, max - 1).trimEnd() + '…' : text;
}

/** Size of the state as sent to the API (UTF-8 JSON); the API accepts at most 16 KiB. */
export function displayStateBytes(state: DisplayState): number {
  return new TextEncoder().encode(JSON.stringify(state)).length;
}

const PHASES: readonly string[] = ['idle', 'cart', 'payment', 'done'];
const isInt = (v: unknown): v is number => typeof v === 'number' && Number.isSafeInteger(v);

/**
 * Defensive parse of a state that arrived over BroadcastChannel or from the public API: anything malformed yields
 * null (the display keeps its previous screen). Unknown fields are dropped, texts are clipped.
 */
export function parseDisplayState(raw: unknown): DisplayState | null {
  if (!raw || typeof raw !== 'object') return null;
  const r = raw as Record<string, unknown>;
  if (typeof r['phase'] !== 'string' || !PHASES.includes(r['phase'])) return null;
  if (!Array.isArray(r['lines']) || !isInt(r['totalMinor'])) return null;
  const lines: DisplayLine[] = [];
  for (const item of r['lines'].slice(0, 200)) {
    if (!item || typeof item !== 'object') return null;
    const l = item as Record<string, unknown>;
    if (typeof l['description'] !== 'string' || !isInt(l['quantity']) || !isInt(l['unitPriceMinor']) || !isInt(l['lineGrossMinor'])) return null;
    lines.push(line(l['description'], l['quantity'], l['unitPriceMinor'], isInt(l['discountMinor']) ? l['discountMinor'] : 0, l['lineGrossMinor']));
  }
  const state: DisplayState = {
    phase: r['phase'] as DisplayPhase,
    lines,
    totalMinor: r['totalMinor'],
    discountTotalMinor: isInt(r['discountTotalMinor']) ? r['discountTotalMinor'] : 0,
    currency: 'EUR',
    locale: typeof r['locale'] === 'string' ? r['locale'].slice(0, 20) : 'de',
  };
  const p = r['payment'] as Record<string, unknown> | null | undefined;
  if (p && typeof p === 'object' && isInt(p['dueMinor']) && isInt(p['tenderedMinor']) && isInt(p['changeMinor'])) {
    state.payment = { dueMinor: p['dueMinor'], tenderedMinor: p['tenderedMinor'], changeMinor: p['changeMinor'], method: typeof p['method'] === 'string' ? p['method'].slice(0, 30) : null };
  }
  const lo = r['loyalty'] as Record<string, unknown> | null | undefined;
  if (lo && typeof lo === 'object' && isInt(lo['points']) && isInt(lo['redeemValueMinor'])) {
    state.loyalty = { points: lo['points'], redeemValueMinor: lo['redeemValueMinor'] };
  }
  if (typeof r['message'] === 'string' && r['message'].trim()) state.message = clip(r['message'].trim(), DISPLAY_LIMITS.message);
  if (typeof r['receiptUrl'] === 'string' && isHttpUrl(r['receiptUrl'])) state.receiptUrl = r['receiptUrl'];
  return state;
}

/** Only http(s) links are turned into a QR code on the display. */
export function isHttpUrl(value: string): boolean {
  if (value.length > DISPLAY_LIMITS.receiptUrl) return false;
  try {
    const u = new URL(value);
    return u.protocol === 'https:' || u.protocol === 'http:';
  } catch {
    return false;
  }
}

// ---------------------------------------------------------------- local transport (same browser, second screen)

export function displayChannelName(registerId: string): string {
  return `hn.pos.display.${registerId}`;
}

/** Messages on the BroadcastChannel: the till sends states; a display that just opened asks for the current one. */
export type DisplayMessage = { type: 'state'; state: DisplayState } | { type: 'hello' };

export function parseDisplayMessage(raw: unknown): DisplayMessage | null {
  if (!raw || typeof raw !== 'object') return null;
  const m = raw as Record<string, unknown>;
  if (m['type'] === 'hello') return { type: 'hello' };
  if (m['type'] === 'state') {
    const state = parseDisplayState(m['state']);
    return state ? { type: 'state', state } : null;
  }
  return null;
}

/** First lines of a company receipt text (header/footer) as one display message. */
export function messageFromReceiptText(text: string | null | undefined, maxLines = 3): string | null {
  const lines = (text ?? '').split(/\r?\n/).map((l) => l.trim()).filter(Boolean).slice(0, maxLines);
  return lines.length ? lines.join('\n') : null;
}
