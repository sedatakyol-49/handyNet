import { Membership, POS_CONFIGURABLE_PERMISSIONS, PosConfigurablePermission, StaffMember } from '../../core/api/models';

/**
 * Per-cashier POS rights of the selected membership (`GET /api/me`: effective permissions + maxDiscountPercent). The UI
 * hides or disables what the cashier may not do; the API enforces the same rules (403 with a stable code).
 */
export interface PosRights {
  sell: boolean;
  manage: boolean;
  /** Sell at a price other than the till price (own POS price / offer price). */
  priceOverride: boolean;
  /** Give line discounts up to `maxDiscountPercent`. */
  discount: boolean;
  refund: boolean;
  cashMovement: boolean;
  closeSession: boolean;
  /** 0..100; 0 when discounts are not allowed. */
  maxDiscountPercent: number;
}

export const FULL_DISCOUNT_PERCENT = 100;

/** Staff POS rights while the membership has no own configuration (API `Permissions.DefaultStaffPos`). */
export const DEFAULT_STAFF_POS: readonly PosConfigurablePermission[] = ['pos.discount', 'pos.refund', 'pos.cash_movement', 'pos.close_session'];

export function posRights(membership: Pick<Membership, 'permissions' | 'maxDiscountPercent'> | null | undefined): PosRights {
  const p = new Set(membership?.permissions ?? []);
  const discount = p.has('pos.discount');
  const limit = membership?.maxDiscountPercent;
  // Older API versions did not send the limit: the discount right then meant "unlimited".
  const max = typeof limit === 'number' && Number.isFinite(limit) ? Math.min(Math.max(Math.trunc(limit), 0), FULL_DISCOUNT_PERCENT) : FULL_DISCOUNT_PERCENT;
  return {
    sell: p.has('pos.sell'),
    manage: p.has('pos.manage'),
    priceOverride: p.has('pos.price_override'),
    discount,
    refund: p.has('pos.refund'),
    cashMovement: p.has('pos.cash_movement'),
    closeSession: p.has('pos.close_session'),
    maxDiscountPercent: discount ? max : 0,
  };
}

/** Session report: `pos.close_session` or `pos.manage` (API ReportService). */
export function canSeeSessionReport(r: PosRights): boolean {
  return r.closeSession || r.manage;
}

/** May the cashier change the unit price of a line? Free entry when the till knows no price. */
export function canEditPrice(line: { listPriceMinor?: number | null }, r: PosRights): boolean {
  return r.priceOverride || line.listPriceMinor === null || line.listPriceMinor === undefined;
}

export function canGiveDiscount(r: PosRights): boolean {
  return r.discount && r.maxDiscountPercent > 0;
}

/** Largest discount (cents) on a line amount (unit price × quantity), like the API: max% × amount / 100, truncated. */
export function maxDiscountMinor(lineAmountMinor: number, maxPercent: number): number {
  if (lineAmountMinor <= 0 || maxPercent <= 0) return 0;
  return Math.floor((Math.min(maxPercent, FULL_DISCOUNT_PERCENT) * lineAmountMinor) / 100);
}

export type LinePolicyIssue =
  | { kind: 'priceOverride'; expectedMinor: number }
  | { kind: 'discountForbidden' }
  | { kind: 'discountLimit'; maxPercent: number; maxDiscountMinor: number };

/**
 * Mirrors the API's `LinePolicy.Check`: a unit price other than the known till price needs `pos.price_override`; any
 * discount needs `pos.discount` and may not exceed the limit, measured on unit price × quantity in integer arithmetic
 * (discount × 100 > max × amount). Without a known price the cashier enters it freely.
 */
export function linePolicyIssue(
  line: { unitPriceMinor: number | null; quantity: number; discountMinor: number; listPriceMinor?: number | null },
  r: PosRights,
): LinePolicyIssue | null {
  const expected = line.listPriceMinor ?? null;
  if (expected !== null && line.unitPriceMinor !== null && line.unitPriceMinor !== expected && !r.priceOverride) {
    return { kind: 'priceOverride', expectedMinor: expected };
  }
  if (line.discountMinor <= 0) return null;
  if (!r.discount) return { kind: 'discountForbidden' };
  const amount = (line.unitPriceMinor ?? 0) * line.quantity;
  // Integers ≤ 1e8 × 1e4 × 100 = 1e14 stay exact in a double.
  if (line.discountMinor * 100 > r.maxDiscountPercent * amount) {
    return { kind: 'discountLimit', maxPercent: r.maxDiscountPercent, maxDiscountMinor: maxDiscountMinor(amount, r.maxDiscountPercent) };
  }
  return null;
}

// ---------------------------------------------------------------- team: per-cashier rights editor

export interface PosRightsForm {
  /** Use the role defaults (sends null permissions). */
  useDefaults: boolean;
  granted: Record<PosConfigurablePermission, boolean>;
  /** Percent as typed; empty = no limit (100 %). */
  maxDiscount: string;
}

/** Only staff rights are configurable; owners and managers always hold every POS right. */
export function posRightsConfigurable(role: string): boolean {
  return role === 'Staff';
}

export function rightsFormFromStaff(s: Pick<StaffMember, 'posPermissions' | 'posMaxDiscountPercent'>): PosRightsForm {
  const own = s.posPermissions ?? null;
  const source = new Set<string>(own ?? DEFAULT_STAFF_POS);
  const granted = Object.fromEntries(POS_CONFIGURABLE_PERMISSIONS.map((p) => [p, source.has(p)])) as Record<PosConfigurablePermission, boolean>;
  const max = s.posMaxDiscountPercent;
  return { useDefaults: own === null, granted, maxDiscount: max === null || max === undefined ? '' : String(max) };
}

export type RightsRequestResult =
  | { ok: true; body: { permissions: string[] | null; maxDiscountPercent: number | null } }
  | { ok: false; error: 'maxDiscount' };

/** Form → `PUT …/pos-rights` body; the limit must be a whole number 0..100 (empty = none). */
export function rightsRequestFromForm(form: PosRightsForm): RightsRequestResult {
  const text = form.maxDiscount.trim().replace(/\s|%/g, '');
  let max: number | null = null;
  if (text !== '') {
    if (!/^\d{1,3}$/.test(text) || Number(text) > FULL_DISCOUNT_PERCENT) return { ok: false, error: 'maxDiscount' };
    max = Number(text);
  }
  const permissions = form.useDefaults ? null : POS_CONFIGURABLE_PERMISSIONS.filter((p) => form.granted[p]);
  return { ok: true, body: { permissions, maxDiscountPercent: max } };
}

/** 'pos.price_override' → 'priceOverride' (translation key part). */
export function posRightsKey(permission: string): string {
  return permission.replace(/^pos\./, '').replace(/_(\w)/g, (_, c: string) => c.toUpperCase());
}

/** Configured POS rights of a member as shown in the team list. */
export function effectivePosRights(s: Pick<StaffMember, 'role' | 'effectivePermissions' | 'posPermissions'>): PosConfigurablePermission[] {
  if (!posRightsConfigurable(s.role)) return [...POS_CONFIGURABLE_PERMISSIONS];
  const effective = s.effectivePermissions ?? s.posPermissions ?? DEFAULT_STAFF_POS;
  const set = new Set<string>(effective);
  return POS_CONFIGURABLE_PERMISSIONS.filter((p) => set.has(p));
}

/** Rights a manager approval must grant so that these line issues go away (price override, discount). */
export function approvalPermissionsFor(issues: LinePolicyIssue[]): PosConfigurablePermission[] {
  const wanted = new Set<PosConfigurablePermission>();
  for (const i of issues) wanted.add(i.kind === 'priceOverride' ? 'pos.price_override' : 'pos.discount');
  return POS_CONFIGURABLE_PERMISSIONS.filter((p) => wanted.has(p));
}

/** An API refusal for a missing POS right that a manager approval can allow, mapped to that right (else null). */
export function approvalPermissionForCode(code: string | null | undefined, fallback: PosConfigurablePermission): PosConfigurablePermission | null {
  if (code === 'pos.price_override_forbidden') return 'pos.price_override';
  if (code === 'pos.discount_forbidden' || code === 'pos.discount_limit_exceeded') return 'pos.discount';
  return code === 'permission.denied' ? fallback : null;
}
