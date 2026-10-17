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
  OrdersStorage,
  PaymentLine,
  activeOrder,
  addOrder,
  addPaymentLine,
  applyNumpad,
  cartTax,
  initialOrders,
  initials,
  looksLikeCode,
  numpadValue,
  orderItemCount,
  parseOrdersState,
  parsePercentToBasisPoints,
  percentOf,
  pressNumpadKey,
  remainingFor,
  removeOrder,
  sanitizeNumpadText,
  settlePayments,
  standardVatFromGross,
  updateOrder,
  gridSearch,
  lookupFromProduct,
  mergeProductPages,
  setLineNote,
} from './pos.logic';
import { PosProduct } from '../../core/api/models';
import { formatMoney } from '../../core/format/money';

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

describe('percent discounts and VAT preview', () => {
  it('parses percentages to basis points (0..100 %)', () => {
    expect(parsePercentToBasisPoints('10')).toBe(1000);
    expect(parsePercentToBasisPoints('12,5')).toBe(1250);
    expect(parsePercentToBasisPoints('7.25')).toBe(725);
    expect(parsePercentToBasisPoints('10 %')).toBe(1000);
    expect(parsePercentToBasisPoints('')).toBe(0);
    expect(parsePercentToBasisPoints('100')).toBe(10000);
    expect(parsePercentToBasisPoints('100,01')).toBeNull();
    expect(parsePercentToBasisPoints('1000')).toBeNull();
    expect(parsePercentToBasisPoints('1,234')).toBeNull();
    expect(parsePercentToBasisPoints('abc')).toBeNull();
  });

  it('computes percentages in integer cents, half away from zero, without float overflow', () => {
    expect(percentOf(2500, 1000)).toBe(250);
    expect(percentOf(1999, 1250)).toBe(250); // 249.875
    expect(percentOf(5, 5000)).toBe(3); // 2.5
    expect(percentOf(-5, 5000)).toBe(-3);
    expect(percentOf(1_000_000_000_000, 3333)).toBe(333_300_000_000);
  });

  it('applies a percent discount to quantity × price', () => {
    const l = line({ quantity: 2, priceInput: '12,50', discountInput: '10', discountKind: 'percent' });
    expect(lineAmounts(l)).toEqual({ unitPriceMinor: 1250, discountMinor: 250, grossMinor: 2250, error: null });
    expect(lineAmounts({ ...l, discountInput: '120' }).error).toBe('discount');
    expect(lineAmounts({ ...l, discountInput: '100' }).grossMinor).toBe(0);
    expect(lineAmounts({ ...l, priceInput: '' }).error).toBe('price');
    expect(buildSaleRequest('r1', [l], []).lines[0]).toEqual({ productId: 'p1', quantity: 2, unitPriceMinor: 1250, discountMinor: 250 });
    // Amount discounts keep working for lines without a kind (older stored tickets).
    expect(lineAmounts(line({ quantity: 2, priceInput: '12,50', discountInput: '2,50' })).grossMinor).toBe(2250);
  });

  it('mirrors the API VAT calculation (per line, 19 % contained in gross)', () => {
    expect(standardVatFromGross(1190)).toBe(190);
    expect(standardVatFromGross(100)).toBe(16);
    expect(standardVatFromGross(2500)).toBe(399);
    expect(standardVatFromGross(-2500)).toBe(-399);
    expect(standardVatFromGross(0)).toBe(0);
  });

  it('separates §25a devices (no VAT shown) from standard lines and skips invalid lines', () => {
    const tax = cartTax([
      line({ quantity: 2, priceInput: '12,50' }),
      line({ key: 'u:1', kind: 'Unit', imei: '1', priceInput: '300,00', taxTreatment: 'Margin25a' }),
      line({ key: 'p9', productId: 'p9', priceInput: '' }),
    ]);
    expect(tax).toEqual({ standardGrossMinor: 2500, standardNetMinor: 2101, standardVatMinor: 399, marginGrossMinor: 30000 });
  });
});

describe('numpad', () => {
  const press = (keys: string[], mode: 'quantity' | 'discount' | 'price' | 'amount', start = { buffer: '', fresh: true }) => {
    let s = { ...start, remove: false };
    for (const k of keys) s = pressNumpadKey(s, k as never, mode);
    return s;
  };

  it('replaces the shown value on the first key, then appends', () => {
    expect(press(['5'], 'quantity', { buffer: '1', fresh: true }).buffer).toBe('5');
    expect(press(['5', '2'], 'quantity', { buffer: '1', fresh: true }).buffer).toBe('52');
    expect(press(['0', '7'], 'quantity').buffer).toBe('7');
  });

  it('accepts one comma with two decimals for amounts, none for quantities', () => {
    expect(press(['1', '2', ',', '5', '0', '9'], 'price').buffer).toBe('12,50');
    expect(press([',', '5'], 'price').buffer).toBe('0,5');
    expect(press(['1', ',', ','], 'amount').buffer).toBe('1,');
    expect(press(['1', ','], 'quantity').buffer).toBe('1');
  });

  it('limits the number of digits per mode', () => {
    expect(press(['1', '2', '3', '4', '5', '6', '7', '8'], 'price').buffer).toBe('1234567');
    expect(press(['1', '2', '3', '4', '5', '6'], 'quantity').buffer).toBe('12345');
    expect(press(['1', '0', '0', '0'], 'discount').buffer).toBe('100');
  });

  it('backspace clears a fresh value, deletes digits and finally removes the line (quantity only)', () => {
    expect(pressNumpadKey({ buffer: '12', fresh: true }, 'backspace', 'quantity')).toEqual({ buffer: '', fresh: false, remove: false });
    expect(pressNumpadKey({ buffer: '12', fresh: false }, 'backspace', 'quantity')).toEqual({ buffer: '1', fresh: false, remove: false });
    expect(pressNumpadKey({ buffer: '', fresh: false }, 'backspace', 'quantity').remove).toBe(true);
    expect(pressNumpadKey({ buffer: '', fresh: false }, 'backspace', 'price').remove).toBe(false);
    expect(pressNumpadKey({ buffer: '12,5', fresh: false }, 'clear', 'price').buffer).toBe('');
  });

  it('sanitizes typed text like numpad input', () => {
    expect(sanitizeNumpadText('12.505', 'price')).toBe('12,50');
    expect(sanitizeNumpadText('a1b2', 'quantity')).toBe('12');
    expect(sanitizeNumpadText('1,2,3', 'amount')).toBe('1,23');
  });

  it('shows the line value per mode', () => {
    const l = line({ quantity: 2, priceInput: '10,00', discountInput: '1,00' });
    expect(numpadValue(l, 'quantity')).toBe('2');
    expect(numpadValue(l, 'price')).toBe('10,00');
    expect(numpadValue(l, 'discount')).toBe('');
    expect(numpadValue({ ...l, discountKind: 'percent', discountInput: '10' }, 'discount')).toBe('10');
  });

  it('writes the buffer into the line: clamped quantity, price, percent discount', () => {
    const lines = [line({ available: 5 }), line({ key: 'u:1', kind: 'Unit', imei: '1', available: 1 })];
    expect(applyNumpad(lines, 'p1', 'quantity', '3')).toEqual({ lines: [{ ...lines[0], quantity: 3 }, lines[1]], clamped: false });
    const clamped = applyNumpad(lines, 'p1', 'quantity', '9');
    expect(clamped.lines[0].quantity).toBe(5);
    expect(clamped.clamped).toBe(true);
    expect(applyNumpad(lines, 'p1', 'quantity', '').lines[0]).toBe(lines[0]);
    expect(applyNumpad(lines, 'u:1', 'quantity', '4').lines[1].quantity).toBe(1);
    expect(applyNumpad(lines, 'p1', 'price', '12,').lines[0].priceInput).toBe('12');
    expect(applyNumpad(lines, 'p1', 'discount', '15').lines[0]).toMatchObject({ discountInput: '15', discountKind: 'percent' });
  });
});

describe('split payments', () => {
  const pay = (id: string, method: 'Cash' | 'Card', input: string): PaymentLine => ({ id, method, input });

  it('sums card and cash lines; cash above the rest becomes change', () => {
    const s = settlePayments(2500, [pay('a', 'Card', '10,00'), pay('b', 'Cash', '20,00')]);
    expect(s.error).toBeNull();
    expect(s.changeMinor).toBe(500);
    expect(s.payments).toEqual([
      { method: 'Cash', amountMinor: 2000 },
      { method: 'Card', amountMinor: 1000 },
    ]);
  });

  it('adds several lines of the same method', () => {
    const s = settlePayments(2500, [pay('a', 'Cash', '10'), pay('b', 'Cash', '15')]);
    expect(s).toMatchObject({ error: null, changeMinor: 0, paidMinor: 2500, payments: [{ method: 'Cash', amountMinor: 2500 }] });
  });

  it('applies the API rules', () => {
    expect(settlePayments(2500, [pay('a', 'Cash', '10')])).toMatchObject({ error: 'insufficient', remainingMinor: 1500 });
    expect(settlePayments(2500, [pay('a', 'Card', '30')]).error).toBe('cardExceedsTotal');
    expect(settlePayments(2500, [pay('a', 'Card', '25'), pay('b', 'Cash', '5')]).error).toBe('cashNotNeeded');
    expect(settlePayments(2500, []).error).toBe('noPayment');
    expect(settlePayments(0, [pay('a', 'Cash', '5')]).error).toBe('emptyCart');
  });

  it('flags empty or invalid lines', () => {
    const s = settlePayments(2500, [pay('a', 'Cash', ''), pay('b', 'Card', '25,00')]);
    expect(s.invalidIds).toEqual(['a']);
    expect(s.error).toBe('invalidAmount');
    expect(settlePayments(2500, [pay('a', 'Cash', 'x')]).error).toBe('invalidAmount');
  });

  it('prefills a new payment with the remaining amount', () => {
    expect(addPaymentLine([], 'Card', 2500, 'a')).toEqual([pay('a', 'Card', '25,00')]);
    const edited = [pay('a', 'Card', '10,00')];
    expect(remainingFor(2500, edited)).toBe(1500);
    expect(remainingFor(2500, edited, 'a')).toBe(2500);
    expect(addPaymentLine(edited, 'Cash', 2500, 'b')[1]).toEqual(pay('b', 'Cash', '15,00'));
    expect(addPaymentLine([pay('a', 'Cash', '30')], 'Card', 2500, 'b')[1].input).toBe('');
  });
});

describe('parked orders', () => {
  const t = '2026-10-16T10:00:00.000Z';

  it('starts with one ticket and parks the current one when a new ticket starts', () => {
    const s0 = initialOrders('a', t);
    expect(s0).toEqual({ nextSeq: 2, activeId: 'a', orders: [{ id: 'a', seq: 1, createdAt: t, lines: [], payments: [], pending: null }] });
    const s1 = updateOrder(addOrder(s0, 'b', t)!, 'b', { lines: [line({ quantity: 2 })] });
    expect(s1.activeId).toBe('b');
    expect(s1.orders.map((o) => o.seq)).toEqual([1, 2]);
    expect(orderItemCount(activeOrder(s1))).toBe(2);
    expect(activeOrder({ ...s1, activeId: 'gone' }).id).toBe('a');
  });

  it('removes a ticket and activates its neighbour, or starts a fresh one', () => {
    const s = addOrder(addOrder(initialOrders('a', t), 'b', t)!, 'c', t)!;
    const withoutB = removeOrder({ ...s, activeId: 'b' }, 'b', 'n', t);
    expect(withoutB.orders.map((o) => o.id)).toEqual(['a', 'c']);
    expect(withoutB.activeId).toBe('c');
    expect(removeOrder({ ...s, activeId: 'a' }, 'c', 'n', t).activeId).toBe('a');
    const last = removeOrder(initialOrders('a', t), 'a', 'n', t);
    expect(last.orders).toEqual([{ id: 'n', seq: 2, createdAt: t, lines: [], payments: [], pending: null }]);
    expect(last.nextSeq).toBe(3);
  });

  it('caps the number of open tickets', () => {
    let s = initialOrders('o0', t);
    for (let i = 1; i < 20; i++) s = addOrder(s, `o${i}`, t)!;
    expect(s.orders.length).toBe(20);
    expect(addOrder(s, 'too-many', t)).toBeNull();
  });

  it('parses stored tickets defensively', () => {
    const good = updateOrder(addOrder(initialOrders('a', t), 'b', t)!, 'a', { lines: [line()], pending: { key: 'k', body: '{}' } });
    expect(parseOrdersState(JSON.parse(JSON.stringify(good)))).toEqual(good);
    expect(parseOrdersState(null)).toBeNull();
    expect(parseOrdersState({ orders: 'x' })).toBeNull();
    const broken = { nextSeq: 1, activeId: 'zz', orders: [{ ...good.orders[0], lines: [{ key: 1 }] }, good.orders[1]] };
    const parsed = parseOrdersState(broken)!;
    expect(parsed.orders.map((o) => o.id)).toEqual(['b']);
    expect(parsed.activeId).toBe('b');
    expect(parsed.nextSeq).toBe(3);
  });

  it('persists per scope and tolerates blocked storage', () => {
    const data = new Map<string, string>();
    const storage = {
      getItem: (k: string) => data.get(k) ?? null,
      setItem: (k: string, v: string) => void data.set(k, v),
      removeItem: (k: string) => void data.delete(k),
    };
    const store = new OrdersStorage(storage, 'org.reg1');
    const state = updateOrder(initialOrders('a', t), 'a', { lines: [line()] });
    store.save(state);
    expect(data.has('hn.pos.orders.org.reg1')).toBe(true);
    expect(store.load()).toEqual(state);
    expect(new OrdersStorage(storage, 'org.reg2').load()).toBeNull();
    store.save(initialOrders('x', t));
    expect(data.has('hn.pos.orders.org.reg1')).toBe(false);
    data.set('hn.pos.orders.org.reg1', '{not json');
    expect(store.load()).toBeNull();
    const fail = (): never => {
      throw new Error('blocked');
    };
    const broken = new OrdersStorage({ getItem: fail, setItem: fail, removeItem: fail }, 's');
    expect(broken.load()).toBeNull();
    expect(() => broken.save(state)).not.toThrow();
  });
});

describe('product tiles (POS products API)', () => {
  const p = (over: Partial<PosProduct> = {}): PosProduct => ({
    productId: 'p1',
    brand: 'Apple',
    name: 'Ladekabel USB-C',
    category: 'Accessory',
    quality: 'Original',
    ean: '4000000000000',
    mpn: null,
    imageUrl: null,
    serialized: false,
    onHand: 5,
    reserved: 1,
    available: 4,
    priceMinor: 1999,
    priceSource: 'PosPrice',
    posPriceMinor: 1999,
    ...over,
  });

  it('turns a tile into a lookup with stock, IMEI requirement and till price', () => {
    const l = lookupFromProduct(p());
    expect(l).toMatchObject({ kind: 'Product', productId: 'p1', available: 4, requiresImei: false, priceMinor: 1999, priceSource: 'PosPrice' });
    expect(lookupFromProduct(p({ serialized: true })).requiresImei).toBe(true);
  });

  it('prefills the till price and marks it as list price; without one the last price is used', () => {
    const r = addToCart([], lookupFromProduct(p()), 1500);
    expect(r.ok && r.lines[0]).toMatchObject({ priceInput: '19,99', listPriceMinor: 1999, priceSource: 'PosPrice' });
    expect(r.ok && r.needsPrice).toBe(false);
    const free = addToCart([], lookupFromProduct(p({ priceMinor: null, priceSource: 'None' })), 1500);
    expect(free.ok && free.lines[0]).toMatchObject({ priceInput: '15,00', listPriceMinor: null, priceSource: 'None' });
    const none = addToCart([], lookupFromProduct(p({ priceMinor: null, priceSource: 'None' })), null);
    expect(none.ok && none.needsPrice).toBe(true);
    expect(addToCart([], lookupFromProduct(p({ serialized: true })), null)).toEqual({ ok: false, error: 'requiresImei' });
  });

  it('merges pages without duplicates and searches only text (codes are looked up)', () => {
    expect(mergeProductPages([p()], [p(), p({ productId: 'p2' })]).map((x) => x.productId)).toEqual(['p1', 'p2']);
    expect(gridSearch('  kabel ')).toBe('kabel');
    expect(gridSearch('4006381333931')).toBe('');
    expect(gridSearch('x'.repeat(150)).length).toBe(100);
  });

  it('sets, trims, limits and removes line notes; notes and customer go into the sale request only when present', () => {
    let lines = setLineNote([line()], 'p1', '  Displayschutz aufgeklebt  ');
    expect(lines[0].note).toBe('Displayschutz aufgeklebt');
    expect(setLineNote([line()], 'p1', 'x'.repeat(250))[0].note!.length).toBe(200);
    const body = buildSaleRequest('r1', lines, [{ method: 'Cash', amountMinor: 1000 }], 'c1');
    expect(body.lines[0].note).toBe('Displayschutz aufgeklebt');
    expect(body.customerId).toBe('c1');
    lines = setLineNote(lines, 'p1', '   ');
    expect('note' in lines[0]).toBe(false);
    const plain = buildSaleRequest('r1', lines, [{ method: 'Cash', amountMinor: 1000 }]);
    expect('note' in plain.lines[0]).toBe(false);
    expect('customerId' in plain).toBe(false);
  });

  it('keeps the attached customer of parked tickets and tolerates tickets stored without one', () => {
    const state = updateOrder(initialOrders('o1', '2026-10-17T10:00:00Z'), 'o1', { customer: { id: 'c1', name: 'Erika Muster', company: null } });
    expect(parseOrdersState(JSON.parse(JSON.stringify(state)))!.orders[0].customer).toEqual({ id: 'c1', name: 'Erika Muster', company: null });
    const legacy = { nextSeq: 2, activeId: 'o1', orders: [{ id: 'o1', seq: 1, createdAt: 'x', lines: [line()], payments: [], pending: null }] };
    expect(parseOrdersState(legacy)!.orders).toHaveLength(1);
    const broken = { ...legacy, orders: [{ ...legacy.orders[0], customer: { id: 5 } }] };
    expect(parseOrdersState(broken)).toBeNull();
  });

  it('translates the cashier-rights problems with their data', () => {
    const err = (body: object) => new HttpErrorResponse({ status: 403, error: body });
    expect(posProblemMessage('pos.price_override_forbidden', err({ lineNumber: 2, expectedUnitPriceMinor: 1999 }))).toEqual({
      key: 'pos.errors.priceOverrideForbidden',
      params: { line: 2, price: formatMoney(1999) },
    });
    expect(posProblemMessage('pos.discount_limit_exceeded', err({ lineNumber: 1, maxDiscountPercent: 10 }))).toEqual({
      key: 'pos.errors.discountLimitExceeded',
      params: { line: 1, percent: 10 },
    });
    expect(posProblemMessage('pos.discount_forbidden', err({ lineNumber: 3 }))!.key).toBe('pos.errors.discountForbidden');
    expect(posProblemMessage('pos.customer_not_found', err({}))!.key).toBe('pos.errors.customerNotFound');
  });

  it('derives monograms and recognizes scanner codes', () => {
    expect(initials('Apple', 'iPhone 13')).toBe('AI');
    expect(initials('', '(USB) Kabel')).toBe('UK');
    expect(initials('', '')).toBe('?');
    expect(looksLikeCode('4006381333931')).toBe(true);
    expect(looksLikeCode(' 350000000000001 ')).toBe(true);
    expect(looksLikeCode('12345')).toBe(false);
    expect(looksLikeCode('Kabel')).toBe(false);
  });
});
