import { CarrierProfile, Order, Shipment, TrackingEvent } from '../../../core/api/models';

/** Upper bounds accepted by the API validator (`CreateShipmentValidator`). */
export const MAX_WEIGHT_GRAMS = 1_000_000;
export const MAX_DIMENSION_CM = 500;

/**
 * Goods worth more than the carrier's standard liability (gross, cents) must be shipped insured. Without a selected
 * carrier profile nothing is required yet. The API remains authoritative (`shipment.insurance_required`).
 */
export function insuranceRequired(grossMinor: number, profile: Pick<CarrierProfile, 'standardLiabilityMinor'> | null | undefined): boolean {
  return !!profile && grossMinor > profile.standardLiabilityMinor;
}

/** False when the goods value exceeds what the carrier can insure at all (API: `shipment.value_too_high`). */
export function insurable(grossMinor: number, profile: Pick<CarrierProfile, 'maxInsuredValueMinor'> | null | undefined): boolean {
  return !profile || grossMinor <= profile.maxInsuredValueMinor;
}

/** Profile of the selected carrier, or null when none is selected / the carrier has no profile. */
export function profileFor(profiles: readonly CarrierProfile[] | null | undefined, carrier: string): CarrierProfile | null {
  return (carrier && profiles?.find((p) => p.carrier === carrier)) || null;
}

/**
 * Parses a kilogram input ("1,25", "1.25", "0,5", "2") to integer grams without floating-point math.
 * Accepts at most three decimals and one decimal separator; returns null for anything else.
 */
export function parseKgToGrams(input: string | number | null | undefined): number | null {
  if (input === null || input === undefined) return null;
  const s = String(input).trim().replace(/\s|kg$/gi, '');
  const m = /^(\d+)(?:[.,](\d{1,3}))?$/.exec(s);
  if (!m) return null;
  const grams = Number(m[1]) * 1000 + Number((m[2] ?? '').padEnd(3, '0'));
  return Number.isSafeInteger(grams) ? grams : null;
}

/** Whole centimetres 1–500, else null. */
export function parseCm(input: string | number | null | undefined): number | null {
  if (input === null || input === undefined) return null;
  const s = String(input).trim();
  if (!/^\d+$/.test(s)) return null;
  const n = Number(s);
  return n >= 1 && n <= MAX_DIMENSION_CM ? n : null;
}

export interface ShipmentFormValue {
  carrier: string;
  weightKg: string;
  lengthCm: string;
  widthCm: string;
  heightCm: string;
  insured: boolean;
  trackingNumber: string;
}

/**
 * Field → translation key of the first client-side problem. Empty object = valid. Limits of the selected carrier
 * (`profile`) are checked as well; the keys `tooHeavy` / `tooLong` / `insuranceRequired` expect the carrier's
 * limits as parameters (see `profileParams`).
 */
export function validateShipmentForm(
  v: ShipmentFormValue,
  grossMinor: number,
  profile: CarrierProfile | null = null,
): Partial<Record<keyof ShipmentFormValue, string>> {
  const errors: Partial<Record<keyof ShipmentFormValue, string>> = {};
  if (!v.carrier) errors.carrier = 'validation.required';
  const grams = parseKgToGrams(v.weightKg);
  if (grams === null || grams < 1 || grams > MAX_WEIGHT_GRAMS) errors.weightKg = 'shipping.form.weightInvalid';
  else if (profile && grams > profile.maxWeightGrams) errors.weightKg = 'shipping.form.tooHeavy';
  for (const f of ['lengthCm', 'widthCm', 'heightCm'] as const) {
    const cm = parseCm(v[f]);
    if (cm === null) errors[f] = 'shipping.form.dimensionInvalid';
    else if (profile && cm > profile.maxLengthCm) errors[f] = 'shipping.form.tooLong';
  }
  if (v.carrier === 'Manual' && !v.trackingNumber.trim()) errors.trackingNumber = 'shipping.form.trackingRequired';
  if (v.trackingNumber.trim().length > 100) errors.trackingNumber = 'shipping.form.trackingTooLong';
  if (insuranceRequired(grossMinor, profile) && !v.insured) errors.insured = 'shipping.form.insuranceRequired';
  return errors;
}

/** A non-cancelled shipment blocks booking another one (API: `shipment.exists`). */
export function hasOpenShipment(shipments: readonly Shipment[]): boolean {
  return shipments.some((s) => s.status !== 'Cancelled');
}

/** Tracking timeline, newest event first (does not mutate the input). */
export function eventsNewestFirst(events: readonly TrackingEvent[]): TrackingEvent[] {
  return [...events].sort((a, b) => Date.parse(b.occurredAt) - Date.parse(a.occurredAt));
}

export interface DurationParts {
  days: number;
  hours: number;
  minutes: number;
}

/** Splits a positive duration into whole days/hours/minutes (rounded down to the minute). */
export function splitDuration(ms: number): DurationParts {
  const totalMinutes = Math.max(0, Math.floor(ms / 60_000));
  return { days: Math.floor(totalMinutes / 1440), hours: Math.floor((totalMinutes % 1440) / 60), minutes: totalMinutes % 60 };
}

export type Translate = (key: string, params?: Record<string, string | number>) => string;

/**
 * Remaining time until `endIso` as text, e.g. "1 T. 23 Std. 5 Min." (zero parts are omitted, days only when > 0).
 * Returns the `shipping.remaining.expired` text once the deadline passed, or "–" for a missing/invalid date.
 */
export function formatRemaining(endIso: string | null | undefined, nowMs: number, t: Translate): string {
  if (!endIso) return '–';
  const end = Date.parse(endIso);
  if (Number.isNaN(end)) return '–';
  const ms = end - nowMs;
  if (ms <= 0) return t('shipping.remaining.expired');
  const p = splitDuration(ms);
  if (p.days === 0 && p.hours === 0 && p.minutes === 0) return t('shipping.remaining.lessThanMinute');
  const parts: string[] = [];
  if (p.days > 0) parts.push(t('shipping.remaining.days', { n: p.days }));
  if (p.days > 0 || p.hours > 0) parts.push(t('shipping.remaining.hours', { n: p.hours }));
  parts.push(t('shipping.remaining.minutes', { n: p.minutes }));
  return parts.join(' ');
}

/** True while the buyer may still accept (or, later, dispute) the goods. */
export function inspectionRunning(order: Pick<Order, 'status' | 'inspectionEndsAt'>, nowMs: number): boolean {
  return order.status === 'Delivered' && !!order.inspectionEndsAt && Date.parse(order.inspectionEndsAt) > nowMs;
}
