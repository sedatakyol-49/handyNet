import {
  DEFAULT_STAFF_POS,
  canEditPrice,
  canGiveDiscount,
  canSeeSessionReport,
  effectivePosRights,
  linePolicyIssue,
  maxDiscountMinor,
  posRights,
  posRightsConfigurable,
  posRightsKey,
  rightsFormFromStaff,
  rightsRequestFromForm,
} from './pos-rights';

const owner = posRights({
  permissions: ['pos.sell', 'pos.manage', 'pos.price_override', 'pos.discount', 'pos.refund', 'pos.cash_movement', 'pos.close_session'],
  maxDiscountPercent: 100,
});
const cashier = (permissions: string[], maxDiscountPercent?: number) => posRights({ permissions: ['pos.sell', ...permissions], maxDiscountPercent });

describe('permission gating', () => {
  it('reads the effective permissions and the discount limit of the membership', () => {
    expect(owner).toEqual({
      sell: true, manage: true, priceOverride: true, discount: true, refund: true, cashMovement: true, closeSession: true, maxDiscountPercent: 100,
    });
    const staff = cashier(['pos.discount', 'pos.refund'], 15);
    expect(staff).toMatchObject({ manage: false, priceOverride: false, discount: true, refund: true, cashMovement: false, closeSession: false, maxDiscountPercent: 15 });
    expect(posRights(null)).toMatchObject({ sell: false, discount: false, maxDiscountPercent: 0 });
  });

  it('treats a missing limit as unlimited, clamps it, and has no limit without the discount right', () => {
    expect(cashier(['pos.discount']).maxDiscountPercent).toBe(100);
    expect(cashier(['pos.discount'], 150).maxDiscountPercent).toBe(100);
    expect(cashier(['pos.discount'], -5).maxDiscountPercent).toBe(0);
    expect(cashier([], 50).maxDiscountPercent).toBe(0);
    expect(canGiveDiscount(cashier(['pos.discount'], 0))).toBe(false);
    expect(canGiveDiscount(cashier(['pos.discount'], 10))).toBe(true);
  });

  it('locks the price of lines with a known till price unless the cashier may override it', () => {
    const plain = cashier([]);
    expect(canEditPrice({ listPriceMinor: 1999 }, plain)).toBe(false);
    expect(canEditPrice({ listPriceMinor: null }, plain)).toBe(true);
    expect(canEditPrice({}, plain)).toBe(true);
    expect(canEditPrice({ listPriceMinor: 1999 }, cashier(['pos.price_override']))).toBe(true);
  });

  it('shows the session report with close_session or manage only', () => {
    expect(canSeeSessionReport(cashier(['pos.close_session']))).toBe(true);
    expect(canSeeSessionReport(posRights({ permissions: ['pos.manage'] }))).toBe(true);
    expect(canSeeSessionReport(cashier(['pos.refund']))).toBe(false);
  });
});

describe('discount limit (mirrors the API LinePolicy)', () => {
  const ten = cashier(['pos.discount'], 10);

  it('allows exactly the limit and refuses one cent more, on unit price × quantity', () => {
    // 2 × 12,50 € = 25,00 € → 10 % = 2,50 €
    expect(linePolicyIssue({ unitPriceMinor: 1250, quantity: 2, discountMinor: 250 }, ten)).toBeNull();
    expect(linePolicyIssue({ unitPriceMinor: 1250, quantity: 2, discountMinor: 251 }, ten)).toEqual({
      kind: 'discountLimit',
      maxPercent: 10,
      maxDiscountMinor: 250,
    });
  });

  it('compares in integers without rounding (odd amounts)', () => {
    // 10 % of 0,05 € = 0,005 € → 1 cent is already more than allowed; the max shown is truncated to 0.
    expect(linePolicyIssue({ unitPriceMinor: 5, quantity: 1, discountMinor: 1 }, ten)).toMatchObject({ kind: 'discountLimit', maxDiscountMinor: 0 });
    expect(maxDiscountMinor(999, 10)).toBe(99);
    expect(maxDiscountMinor(1000, 0)).toBe(0);
    expect(maxDiscountMinor(100_000_000 * 10_000, 100)).toBe(1_000_000_000_000);
  });

  it('refuses any discount without the right and a changed price without override', () => {
    expect(linePolicyIssue({ unitPriceMinor: 1000, quantity: 1, discountMinor: 1 }, cashier([]))).toEqual({ kind: 'discountForbidden' });
    expect(linePolicyIssue({ unitPriceMinor: 1000, quantity: 1, discountMinor: 0 }, cashier([]))).toBeNull();
    expect(linePolicyIssue({ unitPriceMinor: 1500, quantity: 1, discountMinor: 0, listPriceMinor: 1999 }, cashier([]))).toEqual({
      kind: 'priceOverride',
      expectedMinor: 1999,
    });
    expect(linePolicyIssue({ unitPriceMinor: 1999, quantity: 3, discountMinor: 0, listPriceMinor: 1999 }, cashier([]))).toBeNull();
    expect(linePolicyIssue({ unitPriceMinor: 1500, quantity: 1, discountMinor: 0, listPriceMinor: 1999 }, owner)).toBeNull();
    // No known price: free entry.
    expect(linePolicyIssue({ unitPriceMinor: 1500, quantity: 1, discountMinor: 0, listPriceMinor: null }, cashier([]))).toBeNull();
  });

  it('checks the price before the discount (same order as the API)', () => {
    expect(linePolicyIssue({ unitPriceMinor: 1500, quantity: 1, discountMinor: 500, listPriceMinor: 1999 }, cashier([]))!.kind).toBe('priceOverride');
  });
});

describe('team: per-cashier rights editor', () => {
  it('starts from the role defaults when nothing is configured', () => {
    const f = rightsFormFromStaff({ posPermissions: null, posMaxDiscountPercent: null });
    expect(f.useDefaults).toBe(true);
    expect(f.maxDiscount).toBe('');
    expect(Object.entries(f.granted).filter(([, v]) => v).map(([k]) => k)).toEqual([...DEFAULT_STAFF_POS]);
    expect(rightsRequestFromForm(f)).toEqual({ ok: true, body: { permissions: null, maxDiscountPercent: null } });
  });

  it('sends the chosen rights in a stable order and validates the limit', () => {
    const f = rightsFormFromStaff({ posPermissions: ['pos.refund'], posMaxDiscountPercent: 20 });
    expect(f.useDefaults).toBe(false);
    expect(f.maxDiscount).toBe('20');
    const granted = { ...f.granted, 'pos.price_override': true };
    expect(rightsRequestFromForm({ ...f, granted, maxDiscount: ' 15 % ' })).toEqual({
      ok: true,
      body: { permissions: ['pos.price_override', 'pos.refund'], maxDiscountPercent: 15 },
    });
    expect(rightsRequestFromForm({ ...f, maxDiscount: '101' })).toEqual({ ok: false, error: 'maxDiscount' });
    expect(rightsRequestFromForm({ ...f, maxDiscount: '7,5' })).toEqual({ ok: false, error: 'maxDiscount' });
    expect(rightsRequestFromForm({ ...f, maxDiscount: '0' })).toMatchObject({ ok: true, body: { maxDiscountPercent: 0 } });
  });

  it('configures staff only and lists effective rights', () => {
    expect(posRightsConfigurable('Staff')).toBe(true);
    expect(posRightsConfigurable('Owner')).toBe(false);
    expect(posRightsConfigurable('Manager')).toBe(false);
    expect(effectivePosRights({ role: 'Owner' })).toHaveLength(5);
    expect(effectivePosRights({ role: 'Staff', effectivePermissions: ['pos.sell', 'pos.refund', 'orders.read'] })).toEqual(['pos.refund']);
    expect(effectivePosRights({ role: 'Staff' })).toEqual([...DEFAULT_STAFF_POS]);
    expect(posRightsKey('pos.price_override')).toBe('priceOverride');
    expect(posRightsKey('pos.close_session')).toBe('closeSession');
  });
});
