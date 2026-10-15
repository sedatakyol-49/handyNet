import { PolicySettings, PriceListEntry } from '../../../core/api/models';
import {
  bpToPercentInput,
  defaultValidity,
  formatBp,
  isSelectable,
  normalizeOverrideKey,
  parsePercentToBp,
  policyState,
  policyToForm,
  priceChangeBp,
  publishBlockedReason,
  selectableIds,
  toggleAll,
  validatePolicyForm,
  validityToIso,
} from './ankauf-admin.logic';

describe('basis points ↔ percent', () => {
  it('formats basis points as editable percent strings', () => {
    expect(bpToPercentInput(2000)).toBe('20');
    expect(bpToPercentInput(-2000)).toBe('-20');
    expect(bpToPercentInput(1250)).toBe('12,5');
    expect(bpToPercentInput(5)).toBe('0,05');
    expect(bpToPercentInput(0)).toBe('0');
    expect(bpToPercentInput(10000)).toBe('100');
    expect(bpToPercentInput(-9000)).toBe('-90');
    expect(bpToPercentInput(-1)).toBe('-0,01');
  });

  it('parses German and English percent input to integer basis points', () => {
    expect(parsePercentToBp('20')).toBe(2000);
    expect(parsePercentToBp('+20')).toBe(2000);
    expect(parsePercentToBp('-20 %')).toBe(-2000);
    expect(parsePercentToBp('−20')).toBe(-2000);
    expect(parsePercentToBp('12,5')).toBe(1250);
    expect(parsePercentToBp('12.5')).toBe(1250);
    expect(parsePercentToBp('0,07')).toBe(7);
    expect(parsePercentToBp('0,29')).toBe(29); // 0.29 * 100 = 28.999999999999996 in floating point
    expect(parsePercentToBp(' 7 ')).toBe(700);
  });

  it('rejects invalid percent input', () => {
    for (const bad of ['', ' ', 'abc', '1,234', '1.2.3', '--5', '5-', '12,', ',5', null, undefined]) {
      expect(parsePercentToBp(bad as string)).toBeNull();
    }
  });

  it('round-trips every value of the allowed condition range', () => {
    for (let bp = -9000; bp <= 10000; bp += 37) expect(parsePercentToBp(bpToPercentInput(bp))).toBe(bp);
  });

  it('formats basis points for display', () => {
    expect(formatBp(2000, 'de-DE', true)).toBe('+20 %');
    expect(formatBp(-1250, 'de-DE', true)).toBe('-12,5 %');
    expect(formatBp(1500, 'de-DE')).toBe('15 %');
    expect(formatBp(0, 'de-DE', true)).toBe('0 %');
    expect(formatBp(null)).toBe('–');
  });
});

describe('priceChangeBp', () => {
  it('computes the relative change in basis points', () => {
    expect(priceChangeBp(30000, 33000)).toBe(1000);
    expect(priceChangeBp(30000, 21000)).toBe(-3000);
    expect(priceChangeBp(30000, 20999)).toBe(-3000); // −30.0033 % displayed rounded
    expect(priceChangeBp(30000, 30000)).toBe(0);
    expect(priceChangeBp(29999, 30000)).toBe(0);
  });

  it('returns null without a previous price', () => {
    expect(priceChangeBp(null, 30000)).toBeNull();
    expect(priceChangeBp(undefined, 30000)).toBeNull();
    expect(priceChangeBp(0, 30000)).toBeNull();
  });
});

const entry = (id: number, decision: PriceListEntry['decision']): PriceListEntry => ({
  id,
  rowNumber: id + 1,
  brand: 'Apple',
  model: `iPhone ${id}`,
  storage: '128 GB',
  priceMinor: 30000,
  previousPriceMinor: null,
  change: 'New',
  decision,
  decidedAt: null,
});

describe('review selection', () => {
  it('only Pending/Approved/Rejected rows of a list in Review are selectable', () => {
    expect(isSelectable(entry(1, 'Pending'), 'Review')).toBe(true);
    expect(isSelectable(entry(1, 'Approved'), 'Review')).toBe(true);
    expect(isSelectable(entry(1, 'Rejected'), 'Review')).toBe(true);
    expect(isSelectable(entry(1, 'AutoAccepted'), 'Review')).toBe(false);
    expect(isSelectable(entry(1, 'Pending'), 'Published')).toBe(false);
    expect(isSelectable(entry(1, 'Pending'), 'Discarded')).toBe(false);
  });

  it('collects selectable ids of a page', () => {
    const rows = [entry(1, 'Pending'), entry(2, 'AutoAccepted'), entry(3, 'Rejected')];
    expect(selectableIds(rows, 'Review')).toEqual([1, 3]);
    expect(selectableIds(rows, 'Published')).toEqual([]);
  });

  it('toggles a whole page while keeping selections of other pages', () => {
    const other = new Set([99]);
    const all = toggleAll(other, [1, 3]);
    expect([...all].sort((a, b) => a - b)).toEqual([1, 3, 99]);
    expect([...toggleAll(all, [1, 3])]).toEqual([99]);
    expect([...toggleAll(new Set([1]), [1, 3])].sort()).toEqual([1, 3]);
    expect(toggleAll(new Set(), []).size).toBe(0);
  });

  it('blocks publishing while entries are pending or the list is not in review', () => {
    expect(publishBlockedReason('Review', 0)).toBeNull();
    expect(publishBlockedReason('Review', 2)).toBe('ankaufAdmin.review.pendingBlock');
    expect(publishBlockedReason('Published', 0)).toBe('ankaufAdmin.review.notInReview');
  });
});

describe('validity dates', () => {
  it('defaults to the first and last day of next month', () => {
    expect(defaultValidity(new Date(2026, 9, 15))).toEqual({ from: '2026-11-01', until: '2026-11-30' });
    expect(defaultValidity(new Date(2026, 11, 31, 23, 30))).toEqual({ from: '2027-01-01', until: '2027-01-31' });
    expect(defaultValidity(new Date(2027, 0, 31))).toEqual({ from: '2027-02-01', until: '2027-02-28' });
    expect(defaultValidity(new Date(2028, 0, 10))).toEqual({ from: '2028-02-01', until: '2028-02-29' });
  });

  it('converts local dates to ISO instants (start time, inclusive end of day)', () => {
    const r = validityToIso('2026-11-01', '00:00', '2026-11-30');
    expect(r.error).toBeUndefined();
    expect(new Date(r.validFrom!).getTime()).toBe(new Date(2026, 10, 1, 0, 0).getTime());
    expect(new Date(r.validUntil!).getTime()).toBe(new Date(2026, 10, 30, 23, 59, 59, 999).getTime());
    const t = validityToIso('2026-10-15', '13:45', '2026-10-31');
    expect(new Date(t.validFrom!).getTime()).toBe(new Date(2026, 9, 15, 13, 45).getTime());
  });

  it('rejects invalid, reversed and too long periods', () => {
    expect(validityToIso('2026-02-30', '00:00', '2026-03-31').error).toBe('ankaufAdmin.upload.dateInvalid');
    expect(validityToIso('', '00:00', '2026-03-31').error).toBe('ankaufAdmin.upload.dateInvalid');
    expect(validityToIso('2026-11-01', '25:00', '2026-11-30').error).toBe('ankaufAdmin.upload.dateInvalid');
    expect(validityToIso('2026-11-30', '00:00', '2026-11-01').error).toBe('ankaufAdmin.upload.dateOrder');
    expect(validityToIso('2026-11-01', '00:00', '2027-02-28').error).toBe('ankaufAdmin.upload.dateTooLong');
    expect(validityToIso('2026-11-01', '00:00', '2027-01-31').error).toBeUndefined();
  });
});

const settings: PolicySettings = {
  conditionAdjustments: { Neu: 2000, WieNeu: 700, SehrGut: 500, Gut: 0, Akzeptabel: -2000 },
  brandOverrides: { APPLE: { Neu: 2500 } },
  modelOverrides: { 'APPLE|IPHONE13|128GB': { Akzeptabel: -3000 } },
  defectDeductions: { ScreenDamage: 3000, HousingDamage: 1000, CameraDefect: 1500, BiometricDefect: 2000, MissingFunction: 2500 },
  batteryHealthThresholdPercent: 80,
  batteryDeduction: 1000,
  platformMargin: 0,
  minDealerMargin: 1500,
};
const now = new Date(2026, 9, 15, 12, 0);

describe('policy form', () => {
  it('normalizes override keys like the API', () => {
    expect(normalizeOverrideKey('brand', ' Apple ')).toBe('APPLE');
    expect(normalizeOverrideKey('brand', 'Hewlett-Packard')).toBe('HEWLETTPACKARD');
    expect(normalizeOverrideKey('brand', '')).toBeNull();
    expect(normalizeOverrideKey('brand', 'A|B')).toBeNull();
    expect(normalizeOverrideKey('model', 'Apple|iPhone 13|128 GB')).toBe('APPLE|IPHONE13|128GB');
    expect(normalizeOverrideKey('model', 'Apple | iPhone 13')).toBe('APPLE|IPHONE13|');
    expect(normalizeOverrideKey('model', 'APPLE|IPHONE13|128GB')).toBe('APPLE|IPHONE13|128GB');
    expect(normalizeOverrideKey('model', 'iPhone 13')).toBeNull();
    expect(normalizeOverrideKey('model', '|iPhone|1')).toBeNull();
    expect(normalizeOverrideKey('model', 'a|b|c|d')).toBeNull();
  });

  it('round-trips settings through the form', () => {
    const form = policyToForm(settings);
    expect(form.conditions.Neu).toBe('20');
    expect(form.conditions.Akzeptabel).toBe('-20');
    expect(form.defects.ScreenDamage).toBe('30');
    expect(form.minDealerMargin).toBe('15');
    expect(form.overrides).toEqual([
      { scope: 'brand', key: 'APPLE', condition: 'Neu', percent: '25' },
      { scope: 'model', key: 'APPLE|IPHONE13|128GB', condition: 'Akzeptabel', percent: '-30' },
    ]);
    const v = validatePolicyForm(form, now);
    expect(v.errors).toEqual({});
    expect(v.settings).toEqual(settings);
    expect(v.effectiveFrom).toBeNull();
  });

  it('validates ranges and numbers per field', () => {
    const form = policyToForm(settings);
    form.conditions.Neu = '101';
    form.conditions.Akzeptabel = '-90,01';
    form.defects.ScreenDamage = '-1';
    form.platformMargin = '50,01';
    form.minDealerMargin = 'x';
    form.batteryThreshold = '101';
    const v = validatePolicyForm(form, now);
    expect(v.settings).toBeNull();
    expect(v.errors).toEqual({
      'cond-Neu': 'ankaufAdmin.policy.rangeCondition',
      'cond-Akzeptabel': 'ankaufAdmin.policy.rangeCondition',
      'defect-ScreenDamage': 'ankaufAdmin.policy.rangeDeduction',
      'platform-margin': 'ankaufAdmin.policy.rangeMargin',
      'dealer-margin': 'ankaufAdmin.policy.invalidNumber',
      'battery-threshold': 'ankaufAdmin.policy.rangeThreshold',
    });
  });

  it('accepts the range limits', () => {
    const form = policyToForm(settings);
    form.conditions.Neu = '100';
    form.conditions.Akzeptabel = '-90';
    form.defects.ScreenDamage = '100';
    form.platformMargin = '50';
    form.batteryThreshold = '0';
    expect(validatePolicyForm(form, now).errors).toEqual({});
  });

  it('reports invalid and duplicate override rows', () => {
    const form = policyToForm(settings);
    form.overrides.push({ scope: 'brand', key: 'apple', condition: 'Neu', percent: '10' });
    form.overrides.push({ scope: 'model', key: 'nur modell', condition: 'Gut', percent: '5' });
    form.overrides.push({ scope: 'brand', key: 'Samsung', condition: 'Gut', percent: '200' });
    const v = validatePolicyForm(form, now);
    expect(v.errors['override-2-key']).toBe('ankaufAdmin.policy.overrideDuplicate');
    expect(v.errors['override-3-key']).toBe('ankaufAdmin.policy.modelKeyInvalid');
    expect(v.errors['override-4-percent']).toBe('ankaufAdmin.policy.rangeCondition');
  });

  it('checks the effective date', () => {
    const form = policyToForm(settings);
    form.effectiveFrom = '2026-10-15T11:00';
    expect(validatePolicyForm(form, now).errors['effective-from']).toBe('ankaufAdmin.policy.effectiveInPast');
    form.effectiveFrom = '2026-11-01T00:00';
    const v = validatePolicyForm(form, now);
    expect(v.errors).toEqual({});
    expect(new Date(v.effectiveFrom!).getTime()).toBe(new Date(2026, 10, 1).getTime());
  });

  it('derives the display state of a version', () => {
    expect(policyState({ isActive: true, effectiveFrom: '2026-01-01T00:00:00Z' }, now)).toBe('active');
    expect(policyState({ isActive: false, effectiveFrom: '2027-01-01T00:00:00Z' }, now)).toBe('scheduled');
    expect(policyState({ isActive: false, effectiveFrom: '2026-01-01T00:00:00Z' }, now)).toBe('superseded');
  });
});
