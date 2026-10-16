import { HttpErrorResponse } from '@angular/common/http';
import { PosLookup, ReceiptLine } from '../../core/api/models';
import {
  CartLine,
  LastPrices,
  addToCart,
  allocate,
  attemptFor,
  buildSaleRequest,
  canReturn,
  canStorno,
  cardRest,
  cashSuggestions,
  cartTotals,
  cashDifference,
  changeQuantity,
  checkReturn,
  exactCash,
  keepAttemptAfter,
  lineAmounts,
  minorToPlain,
  parseAmount,
  posProblemMessage,
  refundPreviewMinor,
  returnableQuantity,
  settle,
  tillShortcut,
} from './pos.logic';

const product = (over: Partial<PosLookup> = {}): PosLookup => ({
  kind: 'Product',
  productId: 'p1',
  brand: 'Apple',
  name: 'Ladekabel',
  ean: '4000000000000',
  imei: null,
  stockUnitId: null,
  taxTreatment: 'Standard19',
  warehouseId: 'w1',
  onHand: 5,
  reserved: 0,
  available: 5,
  requiresImei: false,
  ...over,
});

const unit = (imei = '350000000000001'): PosLookup =>
  product({ kind: 'Unit', productId: 'p2', name: 'iPhone 13', ean: null, imei, stockUnitId: 'u1', taxTreatment: 'Margin25a', available: 1 });

const line = (over: Partial<CartLine> = {}): CartLine => ({
  key: 'p1',
  kind: 'Product',
  productId: 'p1',
  imei: null,
  description: 'Apple Ladekabel',
  quantity: 1,
  priceInput: '10,00',
  discountInput: '',
  available: 5,
  taxTreatment: 'Standard19',
  ...over,
});

describe('amounts', () => {
  it('parses money inputs to integer cents', () => {
    expect(parseAmount('12,50')).toBe(1250);
    expect(parseAmount('1.234,56')).toBe(123456);
    expect(parseAmount('0.1')).toBe(10);
    expect(parseAmount('')).toBe(0);
    expect(parseAmount('', null)).toBeNull();
    expect(parseAmount('-1')).toBeNull();
    expect(parseAmount('abc')).toBeNull();
    expect(parseAmount('1000000,01')).toBeNull();
  });

  it('formats cents for inputs', () => {
    expect(minorToPlain(1250)).toBe('12,50');
    expect(minorToPlain(5)).toBe('0,05');
    expect(minorToPlain(-199)).toBe('-1,99');
  });

  it('allocates like the API (half away from zero, telescoping)', () => {
    expect(allocate(1000, 1, 3)).toBe(333);
    expect(allocate(1000, 2, 3)).toBe(667);
    expect(allocate(1000, 3, 3)).toBe(1000);
    expect(allocate(5, 1, 2)).toBe(3);
    expect(allocate(-5, 1, 2)).toBe(-3);
    expect(() => allocate(10, 4, 3)).toThrow();
  });
});

describe('cart lines and totals', () => {
  it('computes line gross with discount', () => {
    expect(lineAmounts(line({ quantity: 3, priceInput: '9,99', discountInput: '2' }))).toEqual({
      unitPriceMinor: 999,
      discountMinor: 200,
      grossMinor: 2797,
      error: null,
    });
  });

  it('flags missing price, invalid and too high discounts', () => {
    expect(lineAmounts(line({ priceInput: '' })).error).toBe('price');
    expect(lineAmounts(line({ priceInput: '0' })).error).toBe('price');
    expect(lineAmounts(line({ discountInput: 'x' })).error).toBe('discount');
    expect(lineAmounts(line({ discountInput: '10,01' })).error).toBe('discountTooHigh');
    expect(lineAmounts(line({ discountInput: '10,00' })).grossMinor).toBe(0);
  });

  it('sums the cart and reports invalid lines', () => {
    const t = cartTotals([line({ quantity: 2, discountInput: '1,50' }), line({ key: 'p3', priceInput: '4,99' })]);
    expect(t).toEqual({ totalMinor: 2000 - 150 + 499, itemCount: 3, discountMinor: 150, invalidKeys: [], valid: true });
    const bad = cartTotals([line(), line({ key: 'x', priceInput: '' })]);
    expect(bad.valid).toBe(false);
    expect(bad.invalidKeys).toEqual(['x']);
    expect(bad.totalMinor).toBe(1000);
    expect(cartTotals([]).valid).toBe(false);
  });

  it('adds products, increments on rescan and respects available stock', () => {
    const first = addToCart([], product({ available: 2 }), null);
    expect(first.ok && first.needsPrice).toBe(true);
    if (!first.ok) throw new Error();
    expect(first.lines[0]).toMatchObject({ key: 'p1', quantity: 1, priceInput: '', description: 'Apple Ladekabel' });
    const second = addToCart(first.lines, product({ available: 2 }), null);
    if (!second.ok) throw new Error();
    expect(second.lines).toHaveLength(1);
    expect(second.lines[0].quantity).toBe(2);
    expect(addToCart(second.lines, product({ available: 2 }), null)).toEqual({ ok: false, error: 'notAvailable', available: 2 });
    expect(addToCart([], product({ available: 0 }), null)).toEqual({ ok: false, error: 'notAvailable', available: 0 });
  });

  it('prefills the last used price', () => {
    const r = addToCart([], product(), 1999);
    expect(r.ok && r.lines[0].priceInput).toBe('19,99');
    expect(r.ok && r.needsPrice).toBe(false);
  });

  it('refuses an EAN of a product stocked as devices (the IMEI must be scanned)', () => {
    expect(addToCart([], product({ requiresImei: true }), null)).toEqual({ ok: false, error: 'requiresImei' });
  });

  it('adds devices once with quantity 1', () => {
    const r = addToCart([], unit(), null);
    if (!r.ok) throw new Error();
    expect(r.lines[0]).toMatchObject({ key: 'u:350000000000001', kind: 'Unit', quantity: 1, imei: '350000000000001' });
    expect(addToCart(r.lines, unit(), null)).toEqual({ ok: false, error: 'duplicateUnit' });
    expect(changeQuantity(r.lines, r.lines[0].key, +1)[0].quantity).toBe(1);
  });

  it('changes product quantities within 1..available', () => {
    const lines = [line({ quantity: 1, available: 2 })];
    expect(changeQuantity(lines, 'p1', -1)[0].quantity).toBe(1);
    expect(changeQuantity(lines, 'p1', +1)[0].quantity).toBe(2);
    expect(changeQuantity(changeQuantity(lines, 'p1', +1), 'p1', +1)[0].quantity).toBe(2);
    expect(changeQuantity([line({ available: null })], 'p1', +5)[0].quantity).toBe(6);
  });

  it('builds the sale request', () => {
    const lines = [line({ quantity: 2, discountInput: '1' }), line({ key: 'u:3', kind: 'Unit', productId: 'p2', imei: '3', priceInput: '300' })];
    expect(buildSaleRequest('r1', lines, [{ method: 'Cash', amountMinor: 50000 }])).toEqual({
      registerId: 'r1',
      lines: [
        { productId: 'p1', quantity: 2, unitPriceMinor: 1000, discountMinor: 100 },
        { imei: '3', productId: 'p2', quantity: 1, unitPriceMinor: 30000, discountMinor: null },
      ],
      payments: [{ method: 'Cash', amountMinor: 50000 }],
    });
  });
});

describe('payment', () => {
  it('cash with change', () => {
    const s = settle(1250, '20', '');
    expect(s).toMatchObject({ error: null, paidMinor: 2000, changeMinor: 750, remainingMinor: 0 });
    expect(s.payments).toEqual([{ method: 'Cash', amountMinor: 2000 }]);
  });

  it('card only, exact', () => {
    expect(settle(1250, '', '12,50')).toMatchObject({ error: null, changeMinor: 0, payments: [{ method: 'Card', amountMinor: 1250 }] });
  });

  it('split payment with change from cash', () => {
    expect(settle(10000, '50', '100,01')).toMatchObject({ error: 'cardExceedsTotal' });
    expect(settle(10000, '50', '60')).toMatchObject({ error: null, changeMinor: 1000 });
    expect(settle(10000, '50', '40')).toMatchObject({ error: 'insufficient', remainingMinor: 1000 });
    expect(settle(10000, '50', '50')).toMatchObject({ error: null, changeMinor: 0, paidMinor: 10000 });
    const change = settle(10000, '70', '40');
    expect(change).toMatchObject({ error: null, changeMinor: 1000 });
    expect(change.payments).toEqual([
      { method: 'Cash', amountMinor: 7000 },
      { method: 'Card', amountMinor: 4000 },
    ]);
  });

  it('rejects what the API rejects', () => {
    expect(settle(0, '1', '').error).toBe('emptyCart');
    expect(settle(1000, '', '').error).toBe('noPayment');
    expect(settle(1000, '5', '').error).toBe('insufficient');
    expect(settle(1000, '5', '').remainingMinor).toBe(500);
    expect(settle(1000, 'x', '').error).toBe('invalidAmount');
    expect(settle(1000, '', '10,01').error).toBe('cardExceedsTotal');
    // Card covers the total: cash would only be change.
    expect(settle(1000, '5', '10').error).toBe('cashNotNeeded');
  });

  it('suggests tendered cash amounts', () => {
    expect(cashSuggestions(1250)).toEqual([1250, 1500, 2000, 5000]);
    expect(cashSuggestions(2000)).toEqual([2000, 5000, 10000, 20000]);
    expect(cashSuggestions(0)).toEqual([]);
  });

  it('computes exact cash and the card rest', () => {
    expect(exactCash(1000, '4')).toBe(600);
    expect(exactCash(1000, '20')).toBe(0);
    expect(cardRest(1000, '4')).toBe(600);
    expect(cardRest(1000, '15')).toBe(0);
  });
});

describe('idempotency', () => {
  it('reuses the key for the same body and creates a new one otherwise', () => {
    let n = 0;
    const gen = () => `k${++n}`;
    const a = attemptFor(null, '{"a":1}', gen);
    expect(a.key).toBe('k1');
    expect(attemptFor(a, '{"a":1}', gen)).toBe(a);
    expect(attemptFor(a, '{"a":2}', gen).key).toBe('k2');
  });

  it('keeps the attempt only when the outcome is unknown', () => {
    expect(keepAttemptAfter(0)).toBe(true);
    expect(keepAttemptAfter(503)).toBe(true);
    expect(keepAttemptAfter(409)).toBe(false);
    expect(keepAttemptAfter(422)).toBe(false);
  });
});

describe('returns and storno', () => {
  const rl = (over: Partial<ReceiptLine> = {}): ReceiptLine => ({
    lineNumber: 1,
    productId: 'p1',
    stockUnitId: null,
    imei: null,
    description: 'Kabel',
    quantity: 3,
    unitPriceMinor: 1000,
    discountMinor: 100,
    lineGrossMinor: 2900,
    taxTreatment: 'Standard19',
    vatRateBasisPoints: 1900,
    vatMinor: 463,
    originalLineNumber: null,
    returnedQuantity: 0,
    ...over,
  });
  const receipt = (lines: ReceiptLine[], over: { kind?: 'Sale' | 'Return'; cancelledByReceiptId?: string | null } = {}) => ({
    kind: over.kind ?? ('Sale' as const),
    cancelledByReceiptId: over.cancelledByReceiptId ?? null,
    lines,
  });

  it('returnable quantity', () => {
    expect(returnableQuantity(rl())).toBe(3);
    expect(returnableQuantity(rl({ returnedQuantity: 2 }))).toBe(1);
    expect(returnableQuantity(rl({ returnedQuantity: 3 }))).toBe(0);
  });

  it('refund previews add up to the line amount', () => {
    const a = refundPreviewMinor(rl(), 1);
    const b = refundPreviewMinor(rl({ returnedQuantity: 1 }), 1);
    const c = refundPreviewMinor(rl({ returnedQuantity: 2 }), 1);
    expect([a, b, c]).toEqual([967, 966, 967]);
    expect(a + b + c).toBe(2900);
    expect(refundPreviewMinor(rl({ returnedQuantity: 2 }), 2)).toBe(0);
  });

  it('storno and return availability', () => {
    expect(canStorno(receipt([rl()]))).toBe(true);
    expect(canStorno(receipt([rl({ returnedQuantity: 1 })]))).toBe(false);
    expect(canStorno(receipt([rl()], { cancelledByReceiptId: 'x' }))).toBe(false);
    expect(canStorno(receipt([rl()], { kind: 'Return' }))).toBe(false);
    expect(canReturn(receipt([rl({ returnedQuantity: 1 })]))).toBe(true);
    expect(canReturn(receipt([rl({ returnedQuantity: 3 })]))).toBe(false);
    expect(canReturn(receipt([rl()], { cancelledByReceiptId: 'x' }))).toBe(false);
  });

  it('validates a return selection', () => {
    const r = receipt([rl(), rl({ lineNumber: 2, quantity: 1, lineGrossMinor: 500, returnedQuantity: 0 })]);
    expect(checkReturn(r, {}, 'defekt').error).toBe('nothingSelected');
    expect(checkReturn(r, { 1: 4 }, 'defekt').error).toBe('exceeds');
    expect(checkReturn(r, { 1: 1 }, '  ').error).toBe('reason');
    expect(checkReturn(r, { 1: 1, 2: 1 }, 'defekt')).toEqual({
      lines: [
        { lineNumber: 1, quantity: 1 },
        { lineNumber: 2, quantity: 1 },
      ],
      refundMinor: 967 + 500,
      error: null,
    });
  });
});

describe('closing, shortcuts, errors, last prices', () => {
  it('cash difference', () => {
    expect(cashDifference(10000, '100,00')).toBe(0);
    expect(cashDifference(10000, '99,50')).toBe(-50);
    expect(cashDifference(10000, '')).toBeNull();
    expect(cashDifference(10000, 'abc')).toBeNull();
  });

  it('maps till shortcuts', () => {
    expect(tillShortcut({ key: 'F2' })).toBe('focusCode');
    expect(tillShortcut({ key: 'F4' })).toBe('cardRest');
    expect(tillShortcut({ key: 'F7' })).toBeNull();
    expect(tillShortcut({ key: 'F8' })).toBe('cashExact');
    expect(tillShortcut({ key: 'F9' })).toBe('pay');
    expect(tillShortcut({ key: 'Escape' })).toBe('cancel');
    expect(tillShortcut({ key: 'F9', ctrlKey: true })).toBeNull();
    expect(tillShortcut({ key: 'a' })).toBeNull();
  });

  it('reads stock details from the problem body', () => {
    const err = new HttpErrorResponse({ status: 409, error: { code: 'stock.insufficient', availableQuantity: 2 } });
    expect(posProblemMessage('stock.insufficient', err)).toEqual({ key: 'pos.errors.insufficientStock', params: { available: 2 } });
    expect(posProblemMessage('pos.no_open_session', err)).toBeNull();
  });

  it('remembers last prices per scope and ignores broken storage', () => {
    const data = new Map<string, string>();
    const storage = { getItem: (k: string) => data.get(k) ?? null, setItem: (k: string, v: string) => void data.set(k, v) };
    const prices = new LastPrices(storage, 'org1');
    expect(prices.get('p1')).toBeNull();
    prices.remember({ p1: 1999 });
    prices.remember({ p2: 500 });
    expect(prices.get('p1')).toBe(1999);
    expect(new LastPrices(storage, 'org2').get('p1')).toBeNull();
    const broken = new LastPrices({ getItem: () => { throw new Error('blocked'); }, setItem: () => { throw new Error('blocked'); } }, 'x');
    expect(broken.get('p1')).toBeNull();
    expect(() => broken.remember({ p1: 1 })).not.toThrow();
  });
});
