import {
  ConditionMap,
  DEVICE_CONDITIONS,
  DEVICE_DEFECTS,
  DeviceCondition,
  DeviceDefect,
  EntryDecision,
  PriceChange,
  PriceListEntry,
  PriceListStatus,
  PolicySettings,
} from '../../../core/api/models';

// ---------------------------------------------------------------- basis points ↔ percent

/**
 * Basis points → editable percent string without float math: 1250 → "12,5", -2000 → "-20", 5 → "0,05".
 * The decimal separator is a comma (the input accepts both).
 */
export function bpToPercentInput(bp: number): string {
  const sign = bp < 0 ? '-' : '';
  const abs = Math.abs(Math.trunc(bp));
  const whole = Math.floor(abs / 100);
  const frac = String(abs % 100).padStart(2, '0').replace(/0+$/, '');
  return sign + whole + (frac ? ',' + frac : '');
}

/**
 * Percent input ("12,5", "12.5", "+20", "-20 %") → integer basis points, at most two decimals; null when invalid.
 * Parsed as text so 0,07 % is exactly 7 bp (no floating-point drift).
 */
export function parsePercentToBp(input: string | number | null | undefined): number | null {
  if (input === null || input === undefined) return null;
  const s = String(input).trim().replace(/\s|%/g, '').replace('−', '-');
  const m = /^([+-]?)(\d{1,5})(?:[.,](\d{1,2}))?$/.exec(s);
  if (!m) return null;
  const value = Number(m[2]) * 100 + Number(((m[3] ?? '') + '00').slice(0, 2));
  return m[1] === '-' ? -value : value;
}

/** Display: 1250 → "+12,5 %" (signed) or "12,5 %"; formatting only. */
export function formatBp(bp: number | null | undefined, locale = 'de-DE', signed = false): string {
  if (bp === null || bp === undefined) return '–';
  const f = new Intl.NumberFormat(locale, { maximumFractionDigits: 2, signDisplay: signed ? 'exceptZero' : 'auto' });
  return `${f.format(bp / 100)} %`;
}

// ---------------------------------------------------------------- price changes

/** Relative change of the new price vs. the previous one in basis points (display only); null for new models. */
export function priceChangeBp(previousMinor: number | null | undefined, newMinor: number): number | null {
  if (previousMinor === null || previousMinor === undefined || previousMinor <= 0) return null;
  return Math.round(((newMinor - previousMinor) * 10_000) / previousMinor);
}

/** Text colour of the change column: increases need attention (green/up), large decreases red. */
export function changeTone(change: PriceChange): string {
  switch (change) {
    case 'Increase':
      return 'text-emerald-700';
    case 'Decrease':
      return 'text-amber-700';
    case 'LargeDecrease':
      return 'text-red-700 font-semibold';
    default:
      return 'text-slate-600';
  }
}

export const CHANGE_TONES: Record<PriceChange, 'neutral' | 'info' | 'success' | 'warning' | 'danger'> = {
  New: 'info',
  Unchanged: 'neutral',
  Increase: 'success',
  Decrease: 'warning',
  LargeDecrease: 'danger',
};

export const DECISION_TONES: Record<EntryDecision, 'neutral' | 'info' | 'success' | 'warning' | 'danger'> = {
  AutoAccepted: 'neutral',
  Pending: 'warning',
  Approved: 'success',
  Rejected: 'danger',
};

// ---------------------------------------------------------------- review selection

/** Only entries that needed approval (Pending/Approved/Rejected) can be (re-)decided, and only while in Review. */
export function isSelectable(entry: Pick<PriceListEntry, 'decision'>, status: PriceListStatus): boolean {
  return status === 'Review' && entry.decision !== 'AutoAccepted';
}

export function selectableIds(entries: readonly PriceListEntry[], status: PriceListStatus): number[] {
  return entries.filter((e) => isSelectable(e, status)).map((e) => e.id);
}

/** Toggles all selectable ids of the page: selects them all unless they are all selected already. */
export function toggleAll(selected: ReadonlySet<number>, pageIds: readonly number[]): Set<number> {
  const next = new Set(selected);
  const all = pageIds.length > 0 && pageIds.every((id) => next.has(id));
  for (const id of pageIds) {
    if (all) next.delete(id);
    else next.add(id);
  }
  return next;
}

export const MAX_DECISIONS = 1000;

/** Publishing needs a list in Review without pending entries; returns the i18n key of the reason when blocked. */
export function publishBlockedReason(status: PriceListStatus, pending: number): string | null {
  if (status !== 'Review') return 'ankaufAdmin.review.notInReview';
  if (pending > 0) return 'ankaufAdmin.review.pendingBlock';
  return null;
}

// ---------------------------------------------------------------- validity dates

const pad = (n: number) => String(n).padStart(2, '0');

/** yyyy-MM-dd of a local date. */
export function toDateInput(d: Date): string {
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

/** Default validity of a new monthly list: first to last day of next month (local dates). */
export function defaultValidity(now: Date): { from: string; until: string } {
  const from = new Date(now.getFullYear(), now.getMonth() + 1, 1);
  const until = new Date(now.getFullYear(), now.getMonth() + 2, 0);
  return { from: toDateInput(from), until: toDateInput(until) };
}

function parseDateInput(value: string): { y: number; m: number; d: number } | null {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value ?? '');
  if (!m) return null;
  const y = Number(m[1]);
  const mo = Number(m[2]);
  const d = Number(m[3]);
  const check = new Date(y, mo - 1, d);
  return check.getFullYear() === y && check.getMonth() === mo - 1 && check.getDate() === d ? { y, m: mo, d } : null;
}

function parseTimeInput(value: string): { h: number; mi: number } | null {
  const m = /^(\d{2}):(\d{2})(?::\d{2})?$/.exec(value || '00:00');
  if (!m) return null;
  const h = Number(m[1]);
  const mi = Number(m[2]);
  return h < 24 && mi < 60 ? { h, mi } : null;
}

export const MAX_VALIDITY_DAYS = 92;

export interface ValidityResult {
  validFrom?: string;
  validUntil?: string;
  error?: string;
}

/**
 * Converts the form's local dates (browser time zone) into ISO instants for the API:
 * valid from `fromDate fromTime`, valid until the end of `untilDate` (23:59:59.999, inclusive).
 */
export function validityToIso(fromDate: string, fromTime: string, untilDate: string): ValidityResult {
  const f = parseDateInput(fromDate);
  const t = parseTimeInput(fromTime);
  const u = parseDateInput(untilDate);
  if (!f || !t || !u) return { error: 'ankaufAdmin.upload.dateInvalid' };
  const from = new Date(f.y, f.m - 1, f.d, t.h, t.mi, 0, 0);
  const until = new Date(u.y, u.m - 1, u.d, 23, 59, 59, 999);
  if (until.getTime() <= from.getTime()) return { error: 'ankaufAdmin.upload.dateOrder' };
  if (until.getTime() - from.getTime() > MAX_VALIDITY_DAYS * 24 * 3600 * 1000) return { error: 'ankaufAdmin.upload.dateTooLong' };
  return { validFrom: from.toISOString(), validUntil: until.toISOString() };
}

export function browserTimeZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
  } catch {
    return 'UTC';
  }
}

// ---------------------------------------------------------------- policy form

export interface OverrideRow {
  scope: 'brand' | 'model';
  key: string;
  condition: DeviceCondition;
  percent: string;
}

export interface PolicyForm {
  conditions: Record<DeviceCondition, string>;
  defects: Record<DeviceDefect, string>;
  batteryThreshold: string;
  batteryDeduction: string;
  platformMargin: string;
  minDealerMargin: string;
  overrides: OverrideRow[];
  /** datetime-local value, empty = immediately. */
  effectiveFrom: string;
}

/** Same normalization as the API (`Text.NormalizeKey`): trimmed, upper-case, without whitespace, '-', '_' and '.'. */
export function normalizeKey(value: string): string {
  return value.trim().toUpperCase().replace(/[\s\-_.]/g, '');
}

/** Override key as the API matches it: brand → "APPLE"; model → "APPLE|IPHONE13|128GB" (storage may be empty). */
export function normalizeOverrideKey(scope: OverrideRow['scope'], raw: string): string | null {
  if (scope === 'brand') {
    const k = normalizeKey(raw);
    return k && !k.includes('|') ? k : null;
  }
  const parts = raw.split('|');
  if (parts.length < 2 || parts.length > 3) return null;
  const [brand, model, storage = ''] = parts.map(normalizeKey);
  return brand && model ? `${brand}|${model}|${storage}` : null;
}

export function policyToForm(s: PolicySettings): PolicyForm {
  const overrides: OverrideRow[] = [];
  for (const [scope, map] of [
    ['brand', s.brandOverrides],
    ['model', s.modelOverrides],
  ] as const) {
    for (const [key, conditions] of Object.entries(map ?? {})) {
      for (const c of DEVICE_CONDITIONS) {
        const v = (conditions as ConditionMap)[c];
        if (v !== undefined) overrides.push({ scope, key, condition: c, percent: bpToPercentInput(v) });
      }
    }
  }
  return {
    conditions: Object.fromEntries(DEVICE_CONDITIONS.map((c) => [c, bpToPercentInput(s.conditionAdjustments[c] ?? 0)])) as Record<DeviceCondition, string>,
    defects: Object.fromEntries(DEVICE_DEFECTS.map((d) => [d, bpToPercentInput(s.defectDeductions[d] ?? 0)])) as Record<DeviceDefect, string>,
    batteryThreshold: String(s.batteryHealthThresholdPercent),
    batteryDeduction: bpToPercentInput(s.batteryDeduction),
    platformMargin: bpToPercentInput(s.platformMargin),
    minDealerMargin: bpToPercentInput(s.minDealerMargin),
    overrides,
    effectiveFrom: '',
  };
}

/** Client-side ranges, mirroring `PolicySettings.Validate()` of the API. */
export const POLICY_RANGES = {
  condition: [-9000, 10000],
  deduction: [0, 10000],
  margin: [0, 5000],
} as const;

export interface PolicyValidation {
  settings: PolicySettings | null;
  effectiveFrom: string | null;
  /** Field id → i18n key. Override rows use `override-{index}-key` / `override-{index}-percent`. */
  errors: Record<string, string>;
}

function inRange(bp: number | null, [min, max]: readonly [number, number]): bp is number {
  return bp !== null && bp >= min && bp <= max;
}

export function validatePolicyForm(form: PolicyForm, now: Date): PolicyValidation {
  const errors: Record<string, string> = {};
  const pct = (id: string, value: string, range: readonly [number, number], key: string): number => {
    const bp = parsePercentToBp(value);
    if (bp === null) errors[id] = 'ankaufAdmin.policy.invalidNumber';
    else if (!inRange(bp, range)) errors[id] = key;
    return bp ?? 0;
  };

  const conditionAdjustments = {} as Record<DeviceCondition, number>;
  for (const c of DEVICE_CONDITIONS) conditionAdjustments[c] = pct(`cond-${c}`, form.conditions[c], POLICY_RANGES.condition, 'ankaufAdmin.policy.rangeCondition');
  const defectDeductions = {} as Record<DeviceDefect, number>;
  for (const d of DEVICE_DEFECTS) defectDeductions[d] = pct(`defect-${d}`, form.defects[d], POLICY_RANGES.deduction, 'ankaufAdmin.policy.rangeDeduction');
  const batteryDeduction = pct('battery-deduction', form.batteryDeduction, POLICY_RANGES.deduction, 'ankaufAdmin.policy.rangeDeduction');
  const platformMargin = pct('platform-margin', form.platformMargin, POLICY_RANGES.margin, 'ankaufAdmin.policy.rangeMargin');
  const minDealerMargin = pct('dealer-margin', form.minDealerMargin, POLICY_RANGES.margin, 'ankaufAdmin.policy.rangeMargin');

  const threshold = String(form.batteryThreshold ?? '').trim();
  const batteryHealthThresholdPercent = /^\d{1,3}$/.test(threshold) ? Number(threshold) : NaN;
  if (!(batteryHealthThresholdPercent >= 0 && batteryHealthThresholdPercent <= 100)) errors['battery-threshold'] = 'ankaufAdmin.policy.rangeThreshold';

  const brandOverrides: Record<string, ConditionMap> = {};
  const modelOverrides: Record<string, ConditionMap> = {};
  form.overrides.forEach((row, i) => {
    const key = normalizeOverrideKey(row.scope, row.key);
    if (!key) errors[`override-${i}-key`] = row.scope === 'brand' ? 'ankaufAdmin.policy.brandKeyInvalid' : 'ankaufAdmin.policy.modelKeyInvalid';
    const bp = pct(`override-${i}-percent`, row.percent, POLICY_RANGES.condition, 'ankaufAdmin.policy.rangeCondition');
    if (!key) return;
    const target = row.scope === 'brand' ? brandOverrides : modelOverrides;
    const entry = (target[key] ??= {});
    if (entry[row.condition] !== undefined) errors[`override-${i}-key`] = 'ankaufAdmin.policy.overrideDuplicate';
    entry[row.condition] = bp;
  });

  let effectiveFrom: string | null = null;
  if (form.effectiveFrom) {
    const d = new Date(form.effectiveFrom);
    if (Number.isNaN(d.getTime())) errors['effective-from'] = 'ankaufAdmin.upload.dateInvalid';
    else if (d.getTime() < now.getTime()) errors['effective-from'] = 'ankaufAdmin.policy.effectiveInPast';
    else effectiveFrom = d.toISOString();
  }

  if (Object.keys(errors).length) return { settings: null, effectiveFrom: null, errors };
  return {
    settings: {
      conditionAdjustments,
      brandOverrides,
      modelOverrides,
      defectDeductions,
      batteryHealthThresholdPercent,
      batteryDeduction,
      platformMargin,
      minDealerMargin,
    },
    effectiveFrom,
    errors,
  };
}

/** Status of a policy version for display: active, scheduled (future) or superseded. */
export function policyState(p: { isActive: boolean; effectiveFrom: string }, now: Date): 'active' | 'scheduled' | 'superseded' {
  if (p.isActive) return 'active';
  return new Date(p.effectiveFrom).getTime() > now.getTime() ? 'scheduled' : 'superseded';
}
