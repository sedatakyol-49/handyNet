import { displayStateFromTill } from './display/display-state';
import {
  GIFT_CARD_LIMITS,
  dateOnly,
  formatGiftCardCode,
  giftCardAmountError,
  giftCardPaymentAmount,
  maskedGiftCard,
  normalizeGiftCardCode,
  typingGiftCardCode,
  voucherLayout,
} from './giftcards/gift-card.logic';
import { loyaltySettingsInput, maxRedeemPoints, parseAdjustment, parsePoints, pointsEarned, redeemError, redeemValueMinor } from './loyalty/loyalty.logic';
import { offlineBlock } from './offline/offline-queue.logic';
import {
  CartLine,
  buildSaleRequest,
  giftCardLine,
  giftCardLinesMinor,
  loyaltyEligibleMinor,
  parseOrdersState,
  returnableQuantity,
  settlePayments,
} from './pos.logic';
import { ResolvedPrice, basisPointsToPercent, discountedPrice, isOverridden, pricelistInput, repriceLines, repriceTargets } from './pricelists/pricing.logic';

function line(over: Partial<CartLine> = {}): CartLine {
  return { key: 'p1', kind: 'Product', productId: 'p1', imei: null, description: 'Panzerglas', quantity: 2, priceInput: '19,90', discountInput: '', available: 5, taxTreatment: 'Standard19', listPriceMinor: 1990, priceSource: 'PosPrice', ...over };
}

describe('gift card codes and amounts', () => {
  it('normalizes typed and scanned codes like the API (case, dashes, spaces; no 0/O/1/I)', () => {
    expect(normalizeGiftCardCode('abcd-efgh-jkmn-pq23')).toBe('ABCDEFGHJKMNPQ23');
    expect(normalizeGiftCardCode(' ABCD EFGH JKMN PQ23 ')).toBe('ABCDEFGHJKMNPQ23');
    expect(normalizeGiftCardCode('ABCD-EFGH-JKMN-PQ2')).toBeNull(); // 15 characters
    expect(normalizeGiftCardCode('ABCD-EFGH-JKMN-PQ20')).toBeNull(); // 0 is not in the alphabet
    expect(normalizeGiftCardCode('ABCD-EFGH-IKMN-PQ23')).toBeNull(); // I is not either
    expect(normalizeGiftCardCode(null)).toBeNull();
    expect(normalizeGiftCardCode('x'.repeat(65))).toBeNull();
  });

  it('formats in groups of four, while typing too, and masks to the last four', () => {
    expect(formatGiftCardCode('ABCDEFGHJKMNPQ23')).toBe('ABCD-EFGH-JKMN-PQ23');
    expect(typingGiftCardCode('abcdef')).toBe('ABCD-EF');
    expect(typingGiftCardCode('abcd-efgh-jkmn-pq23-xyz')).toBe('ABCD-EFGH-JKMN-PQ23');
    expect(typingGiftCardCode('ab cd!')).toBe('ABCD');
    expect(maskedGiftCard('pq23')).toBe('••••-PQ23');
    expect(dateOnly('2029-12-31')).toBe('31.12.2029');
    expect(dateOnly(null)).toBe('');
  });

  it('checks the sale amount against the API limits (5,00 € – 500,00 €)', () => {
    expect(giftCardAmountError(GIFT_CARD_LIMITS.minMinor)).toBeNull();
    expect(giftCardAmountError(GIFT_CARD_LIMITS.maxMinor)).toBeNull();
    expect(giftCardAmountError(499)).toBe('tooLow');
    expect(giftCardAmountError(50_001)).toBe('tooHigh');
    expect(giftCardAmountError(null)).toBe('invalid');
    expect(giftCardAmountError(0)).toBe('invalid');
  });

  it('a payment takes the balance, at most what may still be paid by gift card', () => {
    expect(giftCardPaymentAmount(5000, 1990)).toBe(1990);
    expect(giftCardPaymentAmount(1000, 1990)).toBe(1000);
    expect(giftCardPaymentAmount(1000, -5)).toBe(0);
  });

  it('prints the full code on the voucher only when it is known; otherwise the masked one', () => {
    const t = (k: string) => k;
    const text = (code: string | null) =>
      voucherLayout({ id: 'g', code, codeLast4: 'PQ23', amountMinor: 2500, expiresOn: '2029-12-31' }, { width: 42, codepage: 'Cp858', t, sellerLines: ['Shop'], receiptNumber: 7 })
        .items.map((i) => (i.kind === 'text' ? i.text : i.kind))
        .join('\n');
    expect(text('ABCDEFGHJKMNPQ23')).toContain('ABCD-EFGH-JKMN-PQ23');
    expect(text('ABCDEFGHJKMNPQ23')).toContain('25,00');
    expect(text('ABCDEFGHJKMNPQ23')).toContain('31.12.2029');
    expect(text(null)).toContain('PQ23');
    expect(text(null)).not.toContain('ABCD-EFGH');
  });
});

describe('gift cards on the ticket', () => {
  it('a gift card line is sent as giftCardAmountMinor, without product, quantity 1', () => {
    const g = giftCardLine('g:1', 2500, 'Gutschein');
    const body = buildSaleRequest('r1', [line(), g], [{ method: 'Cash', amountMinor: 6480 }]);
    expect(body.lines[1]).toEqual({ giftCardAmountMinor: 2500, quantity: 1, unitPriceMinor: 2500 });
    expect(giftCardLinesMinor([line(), g])).toBe(2500);
    expect(loyaltyEligibleMinor([line(), g])).toBe(3980);
  });

  it('gift card payments: each card with its code, non-cash, never above its balance', () => {
    const s = settlePayments(3980, [
      { id: 'a', method: 'GiftCard', input: '20,00', giftCard: { code: 'ABCDEFGHJKMNPQ23', last4: 'PQ23', balanceMinor: 2500 } },
      { id: 'b', method: 'Cash', input: '20,00' },
    ]);
    expect(s.error).toBeNull();
    expect(s.giftCardMinor).toBe(2000);
    expect(s.changeMinor).toBe(20);
    expect(s.payments).toEqual([
      { method: 'Cash', amountMinor: 2000 },
      { method: 'GiftCard', amountMinor: 2000, giftCardCode: 'ABCDEFGHJKMNPQ23' },
    ]);
    const over = settlePayments(3980, [{ id: 'a', method: 'GiftCard', input: '30,00', giftCard: { code: 'X', last4: 'X', balanceMinor: 2500 } }]);
    expect(over.error).toBe('giftCardBalance');
    expect(over.invalidIds).toEqual(['a']);
  });

  it('a gift card cannot pay for a gift card sold on the same ticket', () => {
    const s = settlePayments(2500, [{ id: 'a', method: 'GiftCard', input: '25,00', giftCard: { code: 'X', last4: 'X', balanceMinor: 9000 } }], false, 2500);
    expect(s.error).toBe('giftCardForGiftCard');
  });

  it('cash and card behave as before (no gift card)', () => {
    const s = settlePayments(2500, [{ id: 'a', method: 'Card', input: '10,00' }, { id: 'b', method: 'Cash', input: '20,00' }]);
    expect(s).toMatchObject({ error: null, cardMinor: 1000, changeMinor: 500, giftCardMinor: 0 });
  });

  it('parked tickets keep gift card lines, gift card payments and the redemption', () => {
    const stored = {
      nextSeq: 2,
      activeId: 'o1',
      orders: [{ id: 'o1', seq: 1, createdAt: 'now', lines: [giftCardLine('g:1', 2500, 'Gutschein')], payments: [{ id: 'p', method: 'GiftCard', input: '5,00', giftCard: { code: 'C', last4: 'C', balanceMinor: 500 } }], pending: null, redeemPoints: 50 }],
    };
    const parsed = parseOrdersState(JSON.parse(JSON.stringify(stored)));
    expect(parsed?.orders[0].lines[0].kind).toBe('GiftCard');
    expect(parsed?.orders[0].redeemPoints).toBe(50);
    expect(parseOrdersState({ ...stored, orders: [{ ...stored.orders[0], payments: [{ id: 'p', method: 'GiftCard', input: '5' }] }] })).toBeNull(); // no card data
  });

  it('gift card lines of a receipt cannot be returned', () => {
    expect(returnableQuantity({ quantity: 1, returnedQuantity: 0, giftCardId: 'g' })).toBe(0);
    expect(returnableQuantity({ quantity: 2, returnedQuantity: 1 })).toBe(1);
  });

  it('offline: gift cards and points need the API', () => {
    const ok = { offlineMode: true, sessionOpen: true, persistent: true, methods: ['Cash'] };
    expect(offlineBlock({ ...ok, sellsGiftCard: true })).toBe('giftCard');
    expect(offlineBlock({ ...ok, methods: ['GiftCard'] })).toBe('payment');
    expect(offlineBlock({ ...ok, redeemsPoints: true })).toBe('loyalty');
    expect(offlineBlock(ok)).toBeNull();
  });
});

describe('loyalty points', () => {
  const rules = { enabled: true, pointValueMinor: 1, minRedeemPoints: 100 };

  it('value, maximum and earning like the API', () => {
    expect(redeemValueMinor(250, 2)).toBe(500);
    expect(redeemValueMinor(-1, 2)).toBe(0);
    expect(maxRedeemPoints(1000, 1, 399)).toBe(399); // limited by the eligible total
    expect(maxRedeemPoints(120, 5, 10_000)).toBe(120); // limited by the balance
    expect(maxRedeemPoints(120, 0, 10_000)).toBe(0);
    expect(pointsEarned(3999, 1)).toBe(39);
    expect(pointsEarned(3999, 3)).toBe(117);
    expect(pointsEarned(99, 5)).toBe(0);
  });

  it('redemption limits: enabled, customer, minimum, balance, eligible total', () => {
    const base = { rules, hasCustomer: true, balance: 500, eligibleMinor: 3980 };
    expect(redeemError(200, base)).toBeNull();
    expect(redeemError(200, { ...base, rules: { ...rules, enabled: false } })).toBe('disabled');
    expect(redeemError(200, { ...base, rules: null })).toBe('disabled');
    expect(redeemError(200, { ...base, hasCustomer: false })).toBe('noCustomer');
    expect(redeemError(null, base)).toBe('invalid');
    expect(redeemError(99, base)).toBe('belowMin');
    expect(redeemError(501, base)).toBe('exceedsBalance');
    expect(redeemError(500, { ...base, eligibleMinor: 499 })).toBe('exceedsTotal');
  });

  it('parses points and signed adjustments', () => {
    expect(parsePoints(' 150 ')).toBe(150);
    expect(parsePoints('0')).toBeNull();
    expect(parsePoints('1,5')).toBeNull();
    expect(parseAdjustment('+50')).toBe(50);
    expect(parseAdjustment('-20')).toBe(-20);
    expect(parseAdjustment('−20')).toBe(-20);
    expect(parseAdjustment('0')).toBeNull();
    expect(parseAdjustment('abc')).toBeNull();
  });

  it('settings form: ranges like the API, value per point in cents', () => {
    const money = (s: string) => (/^\d+(,\d{1,2})?$/.test(s.trim()) ? Math.round(Number(s.trim().replace(',', '.')) * 100) : null);
    expect(loyaltySettingsInput({ enabled: true, pointsPerEuro: '1', pointValue: '0,01', minRedeemPoints: '' }, money)).toEqual({
      ok: true,
      value: { enabled: true, pointsPerEuro: 1, pointValueMinor: 1, minRedeemPoints: 0 },
    });
    expect(loyaltySettingsInput({ enabled: true, pointsPerEuro: '101', pointValue: '2,00', minRedeemPoints: '-1' }, money)).toEqual({ ok: false, errors: ['pointsPerEuro', 'pointValue', 'minRedeem'] });
  });

  it('redeemPoints is sent only with a customer', () => {
    expect(buildSaleRequest('r1', [line()], [], 'c1', 100).redeemPoints).toBe(100);
    expect(buildSaleRequest('r1', [line()], [], null, 100).redeemPoints).toBeUndefined();
    expect(buildSaleRequest('r1', [line()], [], 'c1', null).redeemPoints).toBeUndefined();
  });

  it('the customer display shows the redemption and the reduced amount due', () => {
    const s = displayStateFromTill({ sessionOpen: true, screen: 'payment', lines: [line()], settlement: { paidMinor: 0, changeMinor: 0, cashMinor: 0, cardMinor: 0 }, loyalty: { points: 200, valueMinor: 200 }, locale: 'de' });
    expect(s.loyalty).toEqual({ points: 200, redeemValueMinor: 200 });
    expect(s.payment?.dueMinor).toBe(3780);
    expect(s.totalMinor).toBe(3980);
  });
});

describe('pricelists: re-pricing on a customer change', () => {
  const list: ResolvedPrice = { priceMinor: 1500, priceSource: 'Pricelist', basePriceMinor: 1990, pricelistName: 'Werkstatt' };

  it('lines with the till price follow the list price; the base price and list name are kept for display', () => {
    const r = repriceLines([line()], new Map([['p1', list]]));
    expect(r.changed).toBe(1);
    expect(r.lines[0]).toMatchObject({ priceInput: '15,00', listPriceMinor: 1500, priceSource: 'Pricelist', basePriceMinor: 1990, pricelistName: 'Werkstatt' });
  });

  it('manual price overrides stay, only the reference price changes', () => {
    const overridden = line({ priceInput: '17,00' });
    expect(isOverridden(overridden)).toBe(true);
    const r = repriceLines([overridden], new Map([['p1', list]]));
    expect(r.changed).toBe(0);
    expect(r.lines[0]).toMatchObject({ priceInput: '17,00', listPriceMinor: 1500 });
  });

  it('back to the regular price when the customer is removed', () => {
    const onList = repriceLines([line()], new Map([['p1', list]])).lines;
    const back = repriceLines(onList, new Map([['p1', { priceMinor: 1990, priceSource: 'PosPrice', basePriceMinor: null, pricelistName: null }]]));
    expect(back.lines[0]).toMatchObject({ priceInput: '19,90', priceSource: 'PosPrice', basePriceMinor: null, pricelistName: null });
  });

  it('gift card lines, unknown products and lines without a price are left alone', () => {
    const g = giftCardLine('g:1', 2500, 'Gutschein');
    const typed = line({ key: 'p2', productId: 'p2', listPriceMinor: null, priceInput: '' });
    const r = repriceLines([g, typed, line({ key: 'p3', productId: 'p3' })], new Map([['p2', { priceMinor: null, priceSource: 'None', basePriceMinor: null, pricelistName: null }]]));
    expect(r.lines[0]).toBe(g);
    expect(r.lines[1].priceInput).toBe('');
    expect(r.lines[2]).toEqual(line({ key: 'p3', productId: 'p3' }));
    expect(repriceTargets([g, line(), line({ key: 'u:1', kind: 'Unit', productId: 'p1' })])).toEqual(['p1']);
  });

  it('pricelist form: name, discount percent, validity order', () => {
    expect(pricelistInput({ name: ' Werkstatt ', discountPercent: '12,5', isActive: true, validFrom: '2026-01-01', validTo: '' }, null)).toEqual({
      errors: {},
      input: { name: 'Werkstatt', discountBasisPoints: 1250, isActive: true, validFrom: '2026-01-01', validTo: null, version: null },
    });
    expect(pricelistInput({ name: '', discountPercent: '101', isActive: true, validFrom: '2026-02-01', validTo: '2026-01-01' }, 3).errors).toEqual({
      name: 'pos.pricelists.form.errors.name',
      discountPercent: 'pos.pricelists.form.errors.discount',
      validTo: 'pos.pricelists.form.errors.validity',
    });
    expect(basisPointsToPercent(1250)).toBe('12,5');
    expect(basisPointsToPercent(1000)).toBe('10');
    expect(basisPointsToPercent(1)).toBe('0,01');
  });

  it('general list discount rounds half away from zero like the API', () => {
    expect(discountedPrice(1990, 1000)).toBe(1791);
    expect(discountedPrice(1995, 1000)).toBe(1795); // 199.5 → 200
    expect(discountedPrice(1990, 0)).toBe(1990);
  });
});
