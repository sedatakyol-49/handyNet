/**
 * Loyalty points (ADR-0020), pure client rules mirroring the API. A redemption is a price reduction of
 * points × point value, spread by the API over the eligible lines (all lines except gift cards). The API decides.
 */

export interface LoyaltyRules {
  enabled: boolean;
  /** Points per full euro paid on eligible lines. */
  pointsPerEuro: number;
  /** Cents per point at redemption. */
  pointValueMinor: number;
  minRedeemPoints: number;
}

export const LOYALTY_LIMITS = { maxPointsPerEuro: 100, minPointValueMinor: 1, maxPointValueMinor: 100, maxMinRedeemPoints: 1_000_000, reason: 500 } as const;

export function redeemValueMinor(points: number, pointValueMinor: number): number {
  return Number.isSafeInteger(points) && points > 0 ? points * pointValueMinor : 0;
}

/** Most points that can be redeemed now: the balance, and no more than the eligible total is worth. */
export function maxRedeemPoints(balance: number, pointValueMinor: number, eligibleMinor: number): number {
  if (pointValueMinor <= 0 || eligibleMinor <= 0 || balance <= 0) return 0;
  return Math.min(balance, Math.floor(eligibleMinor / pointValueMinor));
}

export type RedeemError = 'disabled' | 'noCustomer' | 'invalid' | 'belowMin' | 'exceedsBalance' | 'exceedsTotal' | null;

export function redeemError(
  points: number | null,
  o: { rules: Pick<LoyaltyRules, 'enabled' | 'pointValueMinor' | 'minRedeemPoints'> | null; hasCustomer: boolean; balance: number; eligibleMinor: number },
): RedeemError {
  if (!o.rules?.enabled) return 'disabled';
  if (!o.hasCustomer) return 'noCustomer';
  if (points === null || !Number.isSafeInteger(points) || points <= 0) return 'invalid';
  if (points < o.rules.minRedeemPoints) return 'belowMin';
  if (points > o.balance) return 'exceedsBalance';
  if (redeemValueMinor(points, o.rules.pointValueMinor) > o.eligibleMinor) return 'exceedsTotal';
  return null;
}

/** Points a sale earns: floor(eligible gross after all discounts / 1 €) × points per euro. */
export function pointsEarned(eligibleMinor: number, pointsPerEuro: number): number {
  return eligibleMinor > 0 && pointsPerEuro > 0 ? Math.floor(eligibleMinor / 100) * pointsPerEuro : 0;
}

/** Whole points from the input field; null when not a positive integer. */
export function parsePoints(input: string): number | null {
  const t = input.trim();
  if (!/^\d{1,9}$/.test(t)) return null;
  const n = Number(t);
  return n > 0 ? n : null;
}

/** Signed points for a manual adjustment ("+50", "-20", "15"); null when invalid or zero. */
export function parseAdjustment(input: string): number | null {
  const t = input.replace(/\s/g, '').replace('−', '-');
  if (!/^[+-]?\d{1,9}$/.test(t)) return null;
  const n = Number(t);
  return n === 0 ? null : n;
}

export type SettingsError = 'pointsPerEuro' | 'pointValue' | 'minRedeem';

/** Validates the settings form (texts) like the API; returns the request or the fields in error. */
export function loyaltySettingsInput(f: { enabled: boolean; pointsPerEuro: string; pointValue: string; minRedeemPoints: string }, parseMoney: (s: string) => number | null):
  | { ok: true; value: LoyaltyRules }
  | { ok: false; errors: SettingsError[] } {
  const errors: SettingsError[] = [];
  const ppe = /^\d{1,3}$/.test(f.pointsPerEuro.trim()) ? Number(f.pointsPerEuro.trim()) : NaN;
  if (!(ppe >= 0 && ppe <= LOYALTY_LIMITS.maxPointsPerEuro)) errors.push('pointsPerEuro');
  const value = parseMoney(f.pointValue);
  if (value === null || value < LOYALTY_LIMITS.minPointValueMinor || value > LOYALTY_LIMITS.maxPointValueMinor) errors.push('pointValue');
  const min = f.minRedeemPoints.trim() === '' ? 0 : /^\d{1,7}$/.test(f.minRedeemPoints.trim()) ? Number(f.minRedeemPoints.trim()) : NaN;
  if (!(min >= 0 && min <= LOYALTY_LIMITS.maxMinRedeemPoints)) errors.push('minRedeem');
  if (errors.length) return { ok: false, errors };
  return { ok: true, value: { enabled: f.enabled, pointsPerEuro: ppe, pointValueMinor: value!, minRedeemPoints: min } };
}
