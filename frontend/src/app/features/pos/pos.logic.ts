import { HttpErrorResponse } from '@angular/common/http';
import {
  CreateSaleRequest,
  PosLookup,
  PosPaymentMethod,
  PosProduct,
  PriceSource,
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
  lineNoteLength: 200,
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
  /** Line discount (optional): euros, or a percentage of quantity × price when `discountKind` is 'percent'. */
  discountInput: string;
  /** Missing (older stored orders) = 'amount'. */
  discountKind?: 'amount' | 'percent';
  /** Available quantity in the register's warehouse at lookup time (null when unknown). */
  available: number | null;
  taxTreatment: TaxTreatment | null;
  /** The till's price when the line was added (null/missing = none known: the cashier types it freely). */
  listPriceMinor?: number | null;
  priceSource?: PriceSource;
  /** Free text printed under the line on the receipt (max 200 characters). */
  note?: string;
}

export type LineError = 'price' | 'discount' | 'discountTooHigh' | null;

export interface LineAmounts {
  unitPriceMinor: number | null;
  discountMinor: number;
  /** quantity × price − discount; null while the line is invalid. */
  grossMinor: number | null;
  error: LineError;
}

export function lineAmounts(line: Pick<CartLine, 'quantity' | 'priceInput' | 'discountInput' | 'discountKind'>): LineAmounts {
  const unit = parseAmount(line.priceInput, null);
  const percent = line.discountKind === 'percent';
  if (unit === null || unit <= 0) {
    const fallback = percent ? 0 : (parseAmount(line.discountInput, 0) ?? 0);
    return { unitPriceMinor: null, discountMinor: fallback, grossMinor: null, error: 'price' };
  }
  const gross = unit * line.quantity;
  let discount: number | null;
  if (percent) {
    const bp = parsePercentToBasisPoints(line.discountInput);
    discount = bp === null ? null : percentOf(gross, bp);
  } else {
    discount = parseAmount(line.discountInput, 0);
  }
  if (discount === null) return { unitPriceMinor: unit, discountMinor: 0, grossMinor: null, error: 'discount' };
  if (discount > gross) return { unitPriceMinor: unit, discountMinor: discount, grossMinor: null, error: 'discountTooHigh' };
  return { unitPriceMinor: unit, discountMinor: discount, grossMinor: gross - discount, error: null };
}

/** "10", "12,5", "7.25" → basis points (1 % = 100 bp), 0..100 %. Empty → 0. Null for invalid input. */
export function parsePercentToBasisPoints(input: string | null | undefined): number | null {
  const s = (input ?? '').trim().replace(/\s|%/g, '').replace(',', '.');
  if (!s) return 0;
  if (!/^\d{1,3}(\.\d{1,2})?$/.test(s)) return null;
  const [whole, frac = ''] = s.split('.');
  const bp = Number(whole) * 100 + Number((frac + '00').slice(0, 2));
  return bp > 10_000 ? null : bp;
}

/** `bp` basis points of `amountMinor`, rounded half away from zero (BigInt: quantity × price × bp can exceed 2^53). */
export function percentOf(amountMinor: number, bp: number): number {
  const sign = amountMinor < 0 ? -1n : 1n;
  const magnitude = BigInt(Math.abs(amountMinor)) * BigInt(bp);
  return Number(sign * ((2n * magnitude + 10_000n) / 20_000n));
}

/** VAT contained in a 19 % gross amount, exactly like the API's VatCalculator.StandardVatFromGross. */
export function standardVatFromGross(grossMinor: number): number {
  const sign = grossMinor < 0 ? -1 : 1;
  const g = BigInt(Math.abs(grossMinor));
  const net = (2n * g * 10_000n + 11_900n) / 23_800n;
  return sign * Number(g - net);
}

export interface CartTax {
  /** Gross of lines with regular 19 % VAT and their net/VAT (per line, summed like the receipt). */
  standardGrossMinor: number;
  standardNetMinor: number;
  standardVatMinor: number;
  /** Gross of §25a devices: VAT only on the margin and never shown separately (the API computes it). */
  marginGrossMinor: number;
}

/** Tax preview of the valid lines; the receipt's figures always come from the API. */
export function cartTax(lines: readonly CartLine[]): CartTax {
  const out: CartTax = { standardGrossMinor: 0, standardNetMinor: 0, standardVatMinor: 0, marginGrossMinor: 0 };
  for (const line of lines) {
    const gross = lineAmounts(line).grossMinor;
    if (gross === null) continue;
    if (line.taxTreatment === 'Margin25a') {
      out.marginGrossMinor += gross;
    } else {
      const vat = standardVatFromGross(gross);
      out.standardGrossMinor += gross;
      out.standardVatMinor += vat;
      out.standardNetMinor += gross - vat;
    }
  }
  return out;
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
  | { ok: false; error: 'duplicateUnit' | 'notAvailable' | 'tooManyLines' | 'quantityLimit' | 'requiresImei'; available?: number };

/**
 * Adds a lookup result to the cart. Products already in the cart get +1 (up to the available stock when known);
 * devices are unique. The price is prefilled with the till price from the API (own POS price or offer price); without
 * one, with `lastPriceMinor` (last price used for this product in this browser session).
 */
export function addToCart(lines: readonly CartLine[], lookup: PosLookup, lastPriceMinor: number | null): AddResult {
  const listPrice = lookup.priceMinor && lookup.priceMinor > 0 ? lookup.priceMinor : null;
  const prefill = listPrice ?? lastPriceMinor;
  const priced = { listPriceMinor: listPrice, priceSource: listPrice ? (lookup.priceSource ?? 'PosPrice') : ('None' as PriceSource) };
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
      priceInput: prefill ? minorToPlain(prefill) : '',
      discountInput: '',
      available: 1,
      taxTreatment: lookup.taxTreatment,
      ...priced,
    };
    return { ok: true, lines: [...lines, line], key, needsPrice: !prefill };
  }

  // Serialized devices are sold by IMEI only: the EAN does not say which device leaves the shop.
  if (lookup.requiresImei) return { ok: false, error: 'requiresImei' };
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
    priceInput: prefill ? minorToPlain(prefill) : '',
    discountInput: '',
    available,
    taxTreatment: lookup.taxTreatment,
    ...priced,
  };
  return { ok: true, lines: [...lines, line], key: line.key, needsPrice: !prefill };
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

/** Sets the note of a line (trimmed, at most 200 characters; empty removes it). */
export function setLineNote(lines: readonly CartLine[], key: string, note: string): CartLine[] {
  const text = note.trim().slice(0, POS_LIMITS.lineNoteLength);
  return lines.map((l) => (l.key !== key ? l : text ? { ...l, note: text } : withoutNote(l)));
}

function withoutNote(l: CartLine): CartLine {
  const { note: _removed, ...rest } = l;
  return rest;
}

function describe(lookup: PosLookup): string {
  const brand = lookup.brand?.trim() ?? '';
  const name = lookup.name?.trim() ?? '';
  // Many catalog names already start with the brand ("Apple Sim Display"): do not print "Apple Apple Sim Display".
  if (brand && name.toLocaleLowerCase().startsWith(brand.toLocaleLowerCase()) && /^(\s|$)/.test(name.slice(brand.length))) return name;
  return [brand, name].filter(Boolean).join(' ');
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
  return settleMinor(totalMinor, parseAmount(cashInput), parseAmount(cardInput));
}

/** `settle()` on cents (null = invalid input). */
export function settleMinor(totalMinor: number, cash: number | null, card: number | null): Settlement {
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

/** Note and customer are only sent when present (the API's idempotency fingerprint ignores absent ones). */
export function buildSaleRequest(
  registerId: string,
  lines: readonly CartLine[],
  payments: SalePayment[],
  customerId: string | null = null,
): CreateSaleRequest {
  const body: CreateSaleRequest = {
    registerId,
    lines: lines.map((l) => {
      const a = lineAmounts(l);
      const note = l.note?.trim() ? { note: l.note.trim().slice(0, POS_LIMITS.lineNoteLength) } : {};
      return l.kind === 'Unit'
        ? { imei: l.imei, productId: l.productId, quantity: 1, unitPriceMinor: a.unitPriceMinor!, discountMinor: a.discountMinor || null, ...note }
        : { productId: l.productId, quantity: l.quantity, unitPriceMinor: a.unitPriceMinor!, discountMinor: a.discountMinor || null, ...note };
    }),
    payments,
  };
  if (customerId) body.customerId = customerId;
  return body;
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

/** POS codes added with the Odoo-parity API, translated under `pos.errors.*` (texts without data). */
const POS_ERROR_KEYS: Record<string, string> = {
  'pos.customer_not_found': 'pos.errors.customerNotFound',
  'pos.customer_name': 'pos.errors.customerName',
  'pos.customer_email': 'pos.errors.customerEmail',
  'pos.customer_vat_id': 'pos.errors.customerVatId',
  'pos.customer_country': 'pos.errors.customerCountry',
  'pos.customer_field_length': 'pos.errors.customerFieldLength',
  'pos.line_note': 'pos.errors.lineNote',
  'pos.date_range': 'pos.errors.dateRange',
  'pos.amount_range': 'pos.errors.amountRange',
  'pos.reason': 'pos.errors.reason',
  'pos.reason_code': 'pos.errors.reasonCode',
  'pos.cash_amount_required': 'pos.errors.cashAmountRequired',
  'pos.denomination_unknown': 'pos.errors.denominationInvalid',
  'pos.denomination_duplicate': 'pos.errors.denominationInvalid',
  'pos.denomination_count': 'pos.errors.denominationInvalid',
  'pos.price': 'pos.errors.price',
  'pos.product_not_found': 'pos.errors.productNotFound',
  'permission.denied': 'pos.errors.permissionDenied',
};

/**
 * Translation key + params for POS problems whose text needs data from the problem body; other codes use the generic
 * `errors.codes.<code>` lookup.
 */
export function posProblemMessage(code: string | null, error: unknown): { key: string; params: Record<string, string | number> } | null {
  const line = problemDetail(error, 'lineNumber');
  const lineNumber = typeof line === 'number' ? line : '?';
  if (code === 'pos.price_override_forbidden') {
    const expected = problemDetail(error, 'expectedUnitPriceMinor');
    return { key: 'pos.errors.priceOverrideForbidden', params: { line: lineNumber, price: typeof expected === 'number' ? formatMoney(expected) : '–' } };
  }
  if (code === 'pos.discount_forbidden') return { key: 'pos.errors.discountForbidden', params: { line: lineNumber } };
  if (code === 'pos.discount_limit_exceeded') {
    const max = problemDetail(error, 'maxDiscountPercent');
    return { key: 'pos.errors.discountLimitExceeded', params: { line: lineNumber, percent: typeof max === 'number' ? max : '?' } };
  }
  if (code === 'pos.denominations_mismatch') {
    const total = problemDetail(error, 'denominationTotalMinor');
    const amount = problemDetail(error, 'amountMinor');
    if (typeof total === 'number' && typeof amount === 'number') {
      return { key: 'pos.errors.denominationsMismatch', params: { total: formatMoney(total), amount: formatMoney(amount) } };
    }
  }
  if (code && POS_ERROR_KEYS[code]) return { key: POS_ERROR_KEYS[code], params: {} };
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

// ---------------------------------------------------------------- numpad (Odoo-style: Qty / % Disc / Price)

export type NumpadMode = 'quantity' | 'discount' | 'price';
export type NumpadKey = '0' | '1' | '2' | '3' | '4' | '5' | '6' | '7' | '8' | '9' | ',' | 'backspace' | 'clear';

export interface NumpadState {
  buffer: string;
  /** The next digit replaces the shown value (after selecting a line or a mode) instead of appending to it. */
  fresh: boolean;
}

export interface NumpadResult extends NumpadState {
  /** Backspace on an already empty quantity: remove the line (like Odoo). */
  remove: boolean;
}

/** Max. integer digits: amounts (PosLimits.MaxAmountMinor = 1.000.000,00 €), quantities, percentages. */
const AMOUNT_DIGITS = 7;
const QUANTITY_DIGITS = 5;
const PERCENT_DIGITS = 3;

/**
 * Applies one numpad key to the input buffer. Amount modes accept one decimal comma with up to two decimals; the
 * quantity accepts digits only. Pure; the caller writes the buffer into the line or payment.
 */
export function pressNumpadKey(state: NumpadState, key: NumpadKey, mode: NumpadMode | 'amount'): NumpadResult {
  const decimals = mode !== 'quantity';
  if (key === 'clear') return { buffer: '', fresh: false, remove: false };
  if (key === 'backspace') {
    if (state.fresh) return { buffer: '', fresh: false, remove: false };
    if (state.buffer === '') return { buffer: '', fresh: false, remove: mode === 'quantity' };
    return { buffer: state.buffer.slice(0, -1), fresh: false, remove: false };
  }
  const current = state.fresh ? '' : state.buffer;
  if (key === ',') {
    if (!decimals || current.includes(',')) return { buffer: current, fresh: false, remove: false };
    return { buffer: (current || '0') + ',', fresh: false, remove: false };
  }
  const [whole, frac] = current.split(',');
  if (frac !== undefined) {
    return { buffer: frac.length >= 2 ? current : current + key, fresh: false, remove: false };
  }
  const maxDigits = mode === 'quantity' ? QUANTITY_DIGITS : mode === 'discount' ? PERCENT_DIGITS : AMOUNT_DIGITS;
  if (whole.length >= maxDigits) return { buffer: current, fresh: false, remove: false };
  return { buffer: whole === '0' ? key : whole + key, fresh: false, remove: false };
}

/** Value of a line shown on the numpad display for a mode. */
export function numpadValue(line: CartLine, mode: NumpadMode): string {
  if (mode === 'quantity') return String(line.quantity);
  if (mode === 'price') return line.priceInput;
  return line.discountKind === 'percent' ? line.discountInput : '';
}

/** Keeps typed text numpad-compatible (digits and one comma; a '.' typed on a keyboard counts as the comma). */
export function sanitizeNumpadText(text: string, mode: NumpadMode | 'amount'): string {
  let state: NumpadState = { buffer: '', fresh: false };
  for (const ch of text.replace(/\./g, ',')) {
    if (/[0-9,]/.test(ch)) state = pressNumpadKey(state, ch as NumpadKey, mode);
  }
  return state.buffer;
}

export interface NumpadApply {
  lines: CartLine[];
  /** The typed quantity exceeded the limit (available stock / API maximum) and was clamped. */
  clamped: boolean;
}

/** Writes a numpad buffer into a line. An empty quantity keeps the line until backspace removes it. */
export function applyNumpad(lines: readonly CartLine[], key: string, mode: NumpadMode, buffer: string): NumpadApply {
  let clamped = false;
  const value = buffer.replace(/,$/, '');
  const next = lines.map((l) => {
    if (l.key !== key) return l;
    if (mode === 'quantity') {
      if (l.kind === 'Unit' || value === '') return l;
      const wanted = Number(value);
      if (!Number.isInteger(wanted)) return l;
      const q = Math.min(Math.max(wanted, 1), maxQuantity(l));
      clamped = wanted > q;
      return q === l.quantity ? l : { ...l, quantity: q };
    }
    if (mode === 'price') return { ...l, priceInput: value };
    return { ...l, discountInput: value, discountKind: 'percent' as const };
  });
  return { lines: next, clamped };
}

// ---------------------------------------------------------------- payment lines (split payments)

export interface PaymentLine {
  id: string;
  method: PosPaymentMethod;
  /** Amount as typed (numpad/keyboard), German decimal. */
  input: string;
}

export interface PaymentLinesSettlement extends Settlement {
  /** Lines whose amount is empty, zero or invalid. */
  invalidIds: string[];
}

function paymentMinor(p: PaymentLine): number | null {
  const minor = parseAmount(p.input.replace(/,$/, ''), null);
  return minor !== null && minor > 0 ? minor : null;
}

/** Sums the payment lines per method and applies the API's rules (see `settle`). */
export function settlePayments(totalMinor: number, lines: readonly PaymentLine[]): PaymentLinesSettlement {
  let cash = 0;
  let card = 0;
  const invalid: string[] = [];
  for (const p of lines) {
    const minor = paymentMinor(p);
    if (minor === null) invalid.push(p.id);
    else if (p.method === 'Cash') cash += minor;
    else card += minor;
  }
  const result = settleMinor(totalMinor, cash, card);
  if (invalid.length && result.error !== 'emptyCart') return { ...result, invalidIds: invalid, error: 'invalidAmount' };
  return { ...result, invalidIds: invalid };
}

/** Amount still due, ignoring the line `excludeId` (e.g. the one being edited). */
export function remainingFor(totalMinor: number, lines: readonly PaymentLine[], excludeId: string | null = null): number {
  let paid = 0;
  for (const p of lines) if (p.id !== excludeId) paid += paymentMinor(p) ?? 0;
  return Math.max(totalMinor - paid, 0);
}

/** Adds a payment line prefilled with the remaining amount like Odoo (empty when nothing is left). */
export function addPaymentLine(lines: readonly PaymentLine[], method: PosPaymentMethod, totalMinor: number, id: string): PaymentLine[] {
  const rest = remainingFor(totalMinor, lines);
  return [...lines, { id, method, input: rest > 0 ? minorToPlain(rest) : '' }];
}

// ---------------------------------------------------------------- parked orders (several open tickets per register)

export const MAX_OPEN_ORDERS = 20;

/** Customer attached to a ticket (stored with parked tickets; the receipt prints the API's snapshot). */
export interface OrderCustomer {
  id: string;
  name: string;
  company: string | null;
}

export interface PosOrder {
  id: string;
  /** Display number per register ("Bestellung 3"). */
  seq: number;
  createdAt: string;
  lines: CartLine[];
  payments: PaymentLine[];
  /** Checkout attempt whose outcome is unknown (network error / 5xx): retried with the same key. */
  pending: PendingAttempt | null;
  /** Missing on tickets stored by older versions. */
  customer?: OrderCustomer | null;
}

export interface OrdersState {
  nextSeq: number;
  activeId: string;
  orders: PosOrder[];
}

export function emptyOrder(id: string, seq: number, now: string): PosOrder {
  return { id, seq, createdAt: now, lines: [], payments: [], pending: null };
}

export function initialOrders(id: string, now: string): OrdersState {
  return { nextSeq: 2, activeId: id, orders: [emptyOrder(id, 1, now)] };
}

/** Starts a new ticket; the current one stays parked. Null when MAX_OPEN_ORDERS are already open. */
export function addOrder(state: OrdersState, id: string, now: string): OrdersState | null {
  if (state.orders.length >= MAX_OPEN_ORDERS) return null;
  return { nextSeq: state.nextSeq + 1, activeId: id, orders: [...state.orders, emptyOrder(id, state.nextSeq, now)] };
}

/** Removes a ticket (sold or discarded). Its neighbour becomes active; a new empty ticket starts when none is left. */
export function removeOrder(state: OrdersState, id: string, newId: string, now: string): OrdersState {
  const index = state.orders.findIndex((o) => o.id === id);
  if (index < 0) return state;
  const orders = state.orders.filter((o) => o.id !== id);
  if (!orders.length) return { nextSeq: state.nextSeq + 1, activeId: newId, orders: [emptyOrder(newId, state.nextSeq, now)] };
  const activeId = state.activeId === id ? orders[Math.min(index, orders.length - 1)].id : state.activeId;
  return { ...state, activeId, orders };
}

export function updateOrder(state: OrdersState, id: string, patch: Partial<Omit<PosOrder, 'id' | 'seq'>>): OrdersState {
  return { ...state, orders: state.orders.map((o) => (o.id === id ? { ...o, ...patch } : o)) };
}

export function activeOrder(state: OrdersState): PosOrder {
  return state.orders.find((o) => o.id === state.activeId) ?? state.orders[0];
}

export function orderItemCount(order: Pick<PosOrder, 'lines'>): number {
  return order.lines.reduce((n, l) => n + l.quantity, 0);
}

/** Defensive parse of the stored tickets (storage may hold anything): invalid orders are dropped. */
export function parseOrdersState(raw: unknown): OrdersState | null {
  if (!raw || typeof raw !== 'object') return null;
  const r = raw as Partial<OrdersState>;
  if (!Array.isArray(r.orders) || typeof r.activeId !== 'string' || typeof r.nextSeq !== 'number') return null;
  const orders = r.orders.filter(isOrder).slice(0, MAX_OPEN_ORDERS);
  if (!orders.length) return null;
  const maxSeq = Math.max(...orders.map((o) => o.seq));
  const activeId = orders.some((o) => o.id === r.activeId) ? r.activeId : orders[0].id;
  return { nextSeq: Math.max(r.nextSeq, maxSeq + 1), activeId, orders };
}

function isOrder(o: unknown): o is PosOrder {
  if (!o || typeof o !== 'object') return false;
  const x = o as PosOrder;
  return (
    typeof x.id === 'string' &&
    typeof x.seq === 'number' &&
    typeof x.createdAt === 'string' &&
    Array.isArray(x.lines) &&
    x.lines.every(isCartLine) &&
    Array.isArray(x.payments) &&
    x.payments.every((p) => !!p && typeof p.id === 'string' && (p.method === 'Cash' || p.method === 'Card') && typeof p.input === 'string') &&
    (x.pending === null || (typeof x.pending === 'object' && typeof x.pending.key === 'string' && typeof x.pending.body === 'string')) &&
    (x.customer === undefined || x.customer === null || (typeof x.customer === 'object' && typeof x.customer.id === 'string' && typeof x.customer.name === 'string'))
  );
}

function isCartLine(l: unknown): l is CartLine {
  if (!l || typeof l !== 'object') return false;
  const x = l as CartLine;
  return (
    typeof x.key === 'string' &&
    (x.kind === 'Product' || x.kind === 'Unit') &&
    typeof x.productId === 'string' &&
    typeof x.description === 'string' &&
    Number.isInteger(x.quantity) &&
    x.quantity >= 1 &&
    typeof x.priceInput === 'string' &&
    typeof x.discountInput === 'string' &&
    (x.note === undefined || typeof x.note === 'string') &&
    (x.listPriceMinor === undefined || x.listPriceMinor === null || (Number.isInteger(x.listPriceMinor) && x.listPriceMinor > 0))
  );
}

/** Persists the tickets of one register in localStorage (tolerant to blocked storage and corrupted data). */
export class OrdersStorage {
  constructor(
    private readonly storage: Pick<Storage, 'getItem' | 'setItem' | 'removeItem'> | null,
    private readonly scope: string,
  ) {}

  load(): OrdersState | null {
    try {
      const raw = this.storage?.getItem(this.key());
      return raw ? parseOrdersState(JSON.parse(raw)) : null;
    } catch {
      return null;
    }
  }

  save(state: OrdersState): void {
    try {
      // Nothing worth keeping: a single empty ticket without an open checkout attempt.
      const only = state.orders.length === 1 ? state.orders[0] : null;
      if (only && !only.lines.length && !only.payments.length && !only.pending && !only.customer) this.storage?.removeItem(this.key());
      else this.storage?.setItem(this.key(), JSON.stringify(state));
    } catch {
      /* storage unavailable or full */
    }
  }

  private key(): string {
    return `hn.pos.orders.${this.scope}`;
  }
}

// ---------------------------------------------------------------- product tiles (GET /api/pos/registers/{id}/products)

/** Server-side search of the product grid: trimmed, at most 100 characters (API limit); codes are looked up instead. */
export function gridSearch(text: string): string {
  const s = text.trim();
  return looksLikeCode(s) ? '' : s.slice(0, 100);
}

/** Appends the next page of products, dropping duplicates (stock can shift rows between pages while scrolling). */
export function mergeProductPages(current: readonly PosProduct[], next: readonly PosProduct[]): PosProduct[] {
  const seen = new Set(current.map((p) => p.productId));
  return [...current, ...next.filter((p) => !seen.has(p.productId))];
}

/** Two-letter monogram for tiles without an image. */
export function initials(brand: string, name: string): string {
  const words = `${brand} ${name}`.split(/\s+/).map((w) => w.replace(/[^\p{L}\p{N}]/gu, '')).filter(Boolean);
  return words.slice(0, 2).map((w) => [...w][0]).join('').toLocaleUpperCase('de') || '?';
}

/**
 * Lookup-like entry for a tapped product tile: the grid already carries availability, the IMEI requirement
 * (`serialized`) and the till price, so no extra lookup is needed.
 */
export function lookupFromProduct(p: PosProduct): PosLookup {
  return {
    kind: 'Product',
    productId: p.productId,
    brand: p.brand,
    name: p.name,
    ean: p.ean,
    imei: null,
    stockUnitId: null,
    taxTreatment: 'Standard19',
    warehouseId: null,
    onHand: p.onHand,
    reserved: p.reserved,
    available: p.available,
    requiresImei: p.serialized,
    priceMinor: p.priceMinor,
    priceSource: p.priceSource,
  };
}

/** Scanner/keyboard entry that is looked up as EAN/IMEI instead of being used as a search text. */
export function looksLikeCode(text: string): boolean {
  return /^\d{6,20}$/.test(text.trim());
}
