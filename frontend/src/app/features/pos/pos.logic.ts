import { HttpErrorResponse } from '@angular/common/http';
import {
  CreateSaleRequest,
  PosLookup,
  PosPaymentMethod,
  Receipt,
  ReceiptLine,
  SalePayment,
  TaxTreatment,
} from '../../core/api/models';
import { formatMoney, parseMoneyToMinor } from '../../core/format/money';

/** Limits of the API (Receipt.MaxQuantity, MaxLines, reason/name lengths, PosLimits.MaxAmountMinor). */
export const POS_LIMITS = {
  maxQuantity: 10_000,
  maxLines: 200,
  maxAmountMinor: 100_000_000,
  receiptReasonLength: 500,
  movementReasonLength: 200,
  closingNoteLength: 500,
  registerNameLength: 100,
  idempotencyKeyLength: 100,
} as const;

// ---------------------------------------------------------------- amounts

/**
 * Parses a money input to cents. Empty → `emptyAs` (default 0). Returns null for invalid or negative input or
 * amounts above the API's plausibility cap. Integer cents only, no float arithmetic.
 */
export function parseAmount(input: string | null | undefined, emptyAs: number | null = 0): number | null {
  if (input === null || input === undefined || input.trim() === '') return emptyAs;
  const minor = parseMoneyToMinor(input);
  if (minor === null || minor < 0 || minor > POS_LIMITS.maxAmountMinor) return null;
  return minor;
}

/**
 * Share `part`/`of` of `total` rounded half away from zero – identical to the API's VatCalculator.Allocate, so a
 * return preview matches the refund the API will book (all returns of a line add up to exactly the line amount).
 */
export function allocate(total: number, part: number, of: number): number {
  if (of <= 0 || part < 0 || part > of) throw new RangeError('part must be between 0 and of');
  const sign = total < 0 ? -1 : 1;
  const magnitude = Math.abs(total) * part;
  // Integer half-up on the magnitude == half away from zero.
  return sign * Math.floor((2 * magnitude + of) / (2 * of));
}

// ---------------------------------------------------------------- cart

export interface CartLine {
  /** productId for products (scanning again adds quantity), `u:<imei>` for devices. */
  key: string;
  kind: 'Product' | 'Unit';
  productId: string;
  imei: string | null;
  description: string;
  quantity: number;
  /** Unit price as typed by the cashier (German or English decimal). */
  priceInput: string;
  /** Line discount in euros (optional). */
  discountInput: string;
  /** Available quantity in the register's warehouse at lookup time (null when unknown). */
  available: number | null;
  taxTreatment: TaxTreatment | null;
}

export type LineError = 'price' | 'discount' | 'discountTooHigh' | null;

export interface LineAmounts {
  unitPriceMinor: number | null;
  discountMinor: number;
  /** quantity × price − discount; null while the line is invalid. */
  grossMinor: number | null;
  error: LineError;
}

export function lineAmounts(line: Pick<CartLine, 'quantity' | 'priceInput' | 'discountInput'>): LineAmounts {
  const unit = parseAmount(line.priceInput, null);
  const discount = parseAmount(line.discountInput, 0);
  if (unit === null || unit <= 0) return { unitPriceMinor: null, discountMinor: discount ?? 0, grossMinor: null, error: 'price' };
  if (discount === null) return { unitPriceMinor: unit, discountMinor: 0, grossMinor: null, error: 'discount' };
  const gross = unit * line.quantity;
  if (discount > gross) return { unitPriceMinor: unit, discountMinor: discount, grossMinor: null, error: 'discountTooHigh' };
  return { unitPriceMinor: unit, discountMinor: discount, grossMinor: gross - discount, error: null };
}

export interface CartTotals {
  totalMinor: number;
  /** Sum of quantities. */
  itemCount: number;
  discountMinor: number;
  /** Keys of lines without a valid price/discount. */
  invalidKeys: string[];
  valid: boolean;
}

export function cartTotals(lines: readonly CartLine[]): CartTotals {
  let total = 0;
  let items = 0;
  let discount = 0;
  const invalid: string[] = [];
  for (const line of lines) {
    const a = lineAmounts(line);
    items += line.quantity;
    if (a.grossMinor === null) invalid.push(line.key);
    else {
      total += a.grossMinor;
      discount += a.discountMinor;
    }
  }
  return { totalMinor: total, itemCount: items, discountMinor: discount, invalidKeys: invalid, valid: lines.length > 0 && invalid.length === 0 };
}

export type AddResult =
  | { ok: true; lines: CartLine[]; key: string; needsPrice: boolean }
  | { ok: false; error: 'duplicateUnit' | 'notAvailable' | 'tooManyLines' | 'quantityLimit'; available?: number };

/**
 * Adds a lookup result to the cart. Products already in the cart get +1 (up to the available stock when known);
 * devices are unique. `lastPriceMinor` (last price used for this product in this browser session) prefills the price.
 */
export function addToCart(lines: readonly CartLine[], lookup: PosLookup, lastPriceMinor: number | null): AddResult {
  if (lookup.kind === 'Unit') {
    const key = `u:${lookup.imei}`;
    if (lines.some((l) => l.key === key)) return { ok: false, error: 'duplicateUnit' };
    if (lines.length >= POS_LIMITS.maxLines) return { ok: false, error: 'tooManyLines' };
    const line: CartLine = {
      key,
      kind: 'Unit',
      productId: lookup.productId,
      imei: lookup.imei,
      description: describe(lookup),
      quantity: 1,
      priceInput: lastPriceMinor ? minorToPlain(lastPriceMinor) : '',
      discountInput: '',
      available: 1,
      taxTreatment: lookup.taxTreatment,
    };
    return { ok: true, lines: [...lines, line], key, needsPrice: !lastPriceMinor };
  }

  const existing = lines.find((l) => l.key === lookup.productId);
  const available = lookup.available;
  if (existing) {
    const next = existing.quantity + 1;
    if (available !== null && next > available) return { ok: false, error: 'notAvailable', available };
    if (next > POS_LIMITS.maxQuantity) return { ok: false, error: 'quantityLimit' };
    return {
      ok: true,
      lines: lines.map((l) => (l === existing ? { ...l, quantity: next, available } : l)),
      key: existing.key,
      needsPrice: lineAmounts(existing).unitPriceMinor === null,
    };
  }
  if (available !== null && available < 1) return { ok: false, error: 'notAvailable', available };
  if (lines.length >= POS_LIMITS.maxLines) return { ok: false, error: 'tooManyLines' };
  const line: CartLine = {
    key: lookup.productId,
    kind: 'Product',
    productId: lookup.productId,
    imei: null,
    description: describe(lookup),
    quantity: 1,
    priceInput: lastPriceMinor ? minorToPlain(lastPriceMinor) : '',
    discountInput: '',
    available,
    taxTreatment: lookup.taxTreatment,
  };
  return { ok: true, lines: [...lines, line], key: line.key, needsPrice: !lastPriceMinor };
}

/** Upper bound for the quantity of a line (devices: 1; products: available stock if known, API maximum). */
export function maxQuantity(line: Pick<CartLine, 'kind' | 'available'>): number {
  if (line.kind === 'Unit') return 1;
  return line.available === null ? POS_LIMITS.maxQuantity : Math.min(Math.max(line.available, 1), POS_LIMITS.maxQuantity);
}

/** Changes the quantity of a product line by `delta`, clamped to 1..maxQuantity. Devices keep quantity 1. */
export function changeQuantity(lines: readonly CartLine[], key: string, delta: number): CartLine[] {
  return lines.map((l) => {
    if (l.key !== key || l.kind === 'Unit') return l;
    const q = Math.min(Math.max(l.quantity + delta, 1), maxQuantity(l));
    return q === l.quantity ? l : { ...l, quantity: q };
  });
}

export function setQuantity(lines: readonly CartLine[], key: string, quantity: number): CartLine[] {
  return lines.map((l) => {
    if (l.key !== key || l.kind === 'Unit' || !Number.isInteger(quantity)) return l;
    return { ...l, quantity: Math.min(Math.max(quantity, 1), maxQuantity(l)) };
  });
}

function describe(lookup: PosLookup): string {
  return [lookup.brand, lookup.name].filter((s) => !!s && s.trim()).join(' ');
}

/** Cents → "12,50" (input format). */
export function minorToPlain(minor: number): string {
  const sign = minor < 0 ? '-' : '';
  const abs = Math.abs(minor);
  return `${sign}${Math.floor(abs / 100)},${String(abs % 100).padStart(2, '0')}`;
}

// ---------------------------------------------------------------- payment

export type SettleError = 'emptyCart' | 'invalidAmount' | 'noPayment' | 'cardExceedsTotal' | 'insufficient' | 'cashNotNeeded' | null;

export interface Settlement {
  cashMinor: number;
  cardMinor: number;
  paidMinor: number;
  /** Still to pay (0 when covered). */
  remainingMinor: number;
  /** Change to hand back (only from cash). */
  changeMinor: number;
  payments: SalePayment[];
  error: SettleError;
}

/**
 * Mirrors the API rules: card ≤ total; cash + card ≥ total; cash may exceed (change), but cash is refused when the card
 * already covers the total. Inputs are the cashier's texts (empty = 0).
 */
export function settle(totalMinor: number, cashInput: string, cardInput: string): Settlement {
  const cash = parseAmount(cashInput);
  const card = parseAmount(cardInput);
  const base: Settlement = { cashMinor: cash ?? 0, cardMinor: card ?? 0, paidMinor: 0, remainingMinor: totalMinor, changeMinor: 0, payments: [], error: null };
  if (totalMinor <= 0) return { ...base, error: 'emptyCart' };
  if (cash === null || card === null) return { ...base, error: 'invalidAmount' };
  const paid = cash + card;
  const result: Settlement = {
    ...base,
    paidMinor: paid,
    remainingMinor: Math.max(totalMinor - paid, 0),
    changeMinor: Math.max(paid - totalMinor, 0),
    payments: [
      ...(cash > 0 ? [{ method: 'Cash' as PosPaymentMethod, amountMinor: cash }] : []),
      ...(card > 0 ? [{ method: 'Card' as PosPaymentMethod, amountMinor: card }] : []),
    ],
  };
  if (paid === 0) return { ...result, error: 'noPayment' };
  if (card > totalMinor) return { ...result, changeMinor: 0, error: 'cardExceedsTotal' };
  if (paid < totalMinor) return { ...result, error: 'insufficient' };
  if (cash > 0 && cash - result.changeMinor <= 0) return { ...result, changeMinor: 0, error: 'cashNotNeeded' };
  return result;
}

/** Cash amount that settles the rest exactly ("Bar passend"). */
export function exactCash(totalMinor: number, cardInput: string): number {
  const card = parseAmount(cardInput) ?? 0;
  return Math.max(totalMinor - Math.min(card, totalMinor), 0);
}

/**
 * Touch shortcuts for the tendered cash: the exact amount, then the next round amounts above the total
 * (5 € steps and common notes), at most `count` values in ascending order.
 */
export function cashSuggestions(totalMinor: number, count = 4): number[] {
  if (totalMinor <= 0) return [];
  const out = new Set<number>([totalMinor]);
  const step = 500;
  out.add(Math.ceil(totalMinor / step) * step);
  for (const note of [1000, 2000, 5000, 10000, 20000, 50000]) {
    if (note >= totalMinor) out.add(note);
    out.add(Math.ceil(totalMinor / note) * note);
  }
  return [...out].filter((v) => v >= totalMinor).sort((a, b) => a - b).slice(0, count);
}

/** Card amount for the rest after the tendered cash ("Karte (Rest)"); never more than the total. */
export function cardRest(totalMinor: number, cashInput: string): number {
  const cash = parseAmount(cashInput) ?? 0;
  return Math.max(totalMinor - cash, 0);
}

export function buildSaleRequest(registerId: string, lines: readonly CartLine[], payments: SalePayment[]): CreateSaleRequest {
  return {
    registerId,
    lines: lines.map((l) => {
      const a = lineAmounts(l);
      return l.kind === 'Unit'
        ? { imei: l.imei, productId: l.productId, quantity: 1, unitPriceMinor: a.unitPriceMinor!, discountMinor: a.discountMinor || null }
        : { productId: l.productId, quantity: l.quantity, unitPriceMinor: a.unitPriceMinor!, discountMinor: a.discountMinor || null };
    }),
    payments,
  };
}

// ---------------------------------------------------------------- idempotency

export interface PendingAttempt {
  key: string;
  /** Serialized request body the key was created for. */
  body: string;
}

/**
 * One key per checkout attempt: a retry of the same body (after a network error / 5xx) reuses the key so the API
 * returns the original receipt instead of selling twice; a changed body gets a new key (the API would answer
 * `pos.idempotency_key_reused` otherwise).
 */
export function attemptFor(pending: PendingAttempt | null, body: string, newKey: () => string): PendingAttempt {
  return pending && pending.body === body ? pending : { key: newKey(), body };
}

/** After a failed request: keep the key only when the outcome is unknown (network error, 5xx). */
export function keepAttemptAfter(status: number): boolean {
  return status === 0 || status >= 500;
}

export function newIdempotencyKey(): string {
  const c = globalThis.crypto;
  if (c && typeof c.randomUUID === 'function') return c.randomUUID();
  return 'k-' + Date.now().toString(36) + '-' + Math.random().toString(36).slice(2);
}

// ---------------------------------------------------------------- receipts: storno and returns

/** Quantity of a sale line that can still be returned. */
export function returnableQuantity(line: Pick<ReceiptLine, 'quantity' | 'returnedQuantity'>): number {
  return Math.max(line.quantity - line.returnedQuantity, 0);
}

export function canReturn(receipt: Pick<Receipt, 'kind' | 'cancelledByReceiptId' | 'lines'>): boolean {
  return receipt.kind === 'Sale' && !receipt.cancelledByReceiptId && receipt.lines.some((l) => returnableQuantity(l) > 0);
}

/** Storno only for a sale that is not cancelled and has no returns yet (the API answers `pos.has_returns`). */
export function canStorno(receipt: Pick<Receipt, 'kind' | 'cancelledByReceiptId' | 'lines'>): boolean {
  return receipt.kind === 'Sale' && !receipt.cancelledByReceiptId && receipt.lines.every((l) => l.returnedQuantity === 0);
}

/** Refund (positive cents) for returning `quantity` more units of a line, allocated like the API. */
export function refundPreviewMinor(line: Pick<ReceiptLine, 'quantity' | 'returnedQuantity' | 'lineGrossMinor'>, quantity: number): number {
  if (quantity <= 0) return 0;
  const to = line.returnedQuantity + quantity;
  if (to > line.quantity) return 0;
  return allocate(line.lineGrossMinor, to, line.quantity) - allocate(line.lineGrossMinor, line.returnedQuantity, line.quantity);
}

export type ReturnSelection = Record<number, number>;

export interface ReturnCheck {
  lines: { lineNumber: number; quantity: number }[];
  refundMinor: number;
  error: 'nothingSelected' | 'exceeds' | 'reason' | null;
}

export function checkReturn(receipt: Pick<Receipt, 'lines'>, selection: ReturnSelection, reason: string): ReturnCheck {
  const lines: { lineNumber: number; quantity: number }[] = [];
  let refund = 0;
  for (const line of receipt.lines) {
    const q = selection[line.lineNumber] ?? 0;
    if (!q) continue;
    if (!Number.isInteger(q) || q < 0 || q > returnableQuantity(line)) return { lines: [], refundMinor: 0, error: 'exceeds' };
    lines.push({ lineNumber: line.lineNumber, quantity: q });
    refund += refundPreviewMinor(line, q);
  }
  if (!lines.length) return { lines, refundMinor: 0, error: 'nothingSelected' };
  const r = reason.trim();
  if (!r || r.length > POS_LIMITS.receiptReasonLength) return { lines, refundMinor: refund, error: 'reason' };
  return { lines, refundMinor: refund, error: null };
}

// ---------------------------------------------------------------- closing

/** Counted − expected (negative = shortage). Null while the counted input is invalid or empty. */
export function cashDifference(expectedMinor: number, countedInput: string): number | null {
  const counted = parseAmount(countedInput, null);
  return counted === null ? null : counted - expectedMinor;
}

// ---------------------------------------------------------------- keyboard

export type TillShortcut = 'focusCode' | 'cardRest' | 'cashExact' | 'pay' | 'cancel';

/** Till shortcuts (documented on the till screen). Modifier combinations are left to the browser. */
export const TILL_SHORTCUTS: { key: string; action: TillShortcut }[] = [
  { key: 'F2', action: 'focusCode' },
  { key: 'F4', action: 'cardRest' },
  { key: 'F8', action: 'cashExact' },
  { key: 'F9', action: 'pay' },
  { key: 'Escape', action: 'cancel' },
];

export function tillShortcut(e: { key: string; ctrlKey?: boolean; altKey?: boolean; metaKey?: boolean }): TillShortcut | null {
  if (e.ctrlKey || e.altKey || e.metaKey) return null;
  return TILL_SHORTCUTS.find((s) => s.key === e.key)?.action ?? null;
}

// ---------------------------------------------------------------- errors

/** Reads a root-level extension of a problem+json error body (e.g. `availableQuantity`). */
export function problemDetail(error: unknown, name: string): unknown {
  if (error instanceof HttpErrorResponse && error.error && typeof error.error === 'object') {
    return (error.error as Record<string, unknown>)[name];
  }
  return undefined;
}

export function problemStatus(error: unknown): number {
  return error instanceof HttpErrorResponse ? error.status : -1;
}

/**
 * Translation key + params for POS problems whose text needs data from the problem body; other codes use the generic
 * `errors.codes.<code>` lookup.
 */
export function posProblemMessage(code: string | null, error: unknown): { key: string; params: Record<string, string | number> } | null {
  if (code === 'stock.insufficient') {
    const available = problemDetail(error, 'availableQuantity');
    if (typeof available === 'number') return { key: 'pos.errors.insufficientStock', params: { available } };
  }
  // The generic text is worded for Ankauf intakes; on a return it means the device is already back in stock.
  if (code === 'unit.imei_in_stock') return { key: 'pos.errors.returnImeiInStock', params: {} };
  if (code === 'pos.insufficient_cash') {
    const expected = problemDetail(error, 'expectedCashMinor');
    if (typeof expected === 'number') return { key: 'pos.errors.insufficientCash', params: { amount: formatMoney(expected) } };
  }
  return null;
}

// ---------------------------------------------------------------- last prices (browser session)

/** Remembers the last unit price per product for this browser session (sessionStorage; tolerant to blocked storage). */
export class LastPrices {
  constructor(
    private readonly storage: Pick<Storage, 'getItem' | 'setItem'> | null,
    private readonly scope: string,
  ) {}

  private read(): Record<string, number> {
    try {
      const raw = this.storage?.getItem(this.key());
      const parsed = raw ? (JSON.parse(raw) as unknown) : null;
      return parsed && typeof parsed === 'object' ? (parsed as Record<string, number>) : {};
    } catch {
      return {};
    }
  }

  get(productId: string): number | null {
    const v = this.read()[productId];
    return typeof v === 'number' && Number.isInteger(v) && v > 0 ? v : null;
  }

  remember(prices: Record<string, number>): void {
    try {
      this.storage?.setItem(this.key(), JSON.stringify({ ...this.read(), ...prices }));
    } catch {
      /* storage unavailable */
    }
  }

  private key(): string {
    return `hn.pos.prices.${this.scope}`;
  }
}
