import {
  DeviceCondition,
  DeviceDefect,
  IdDocumentType,
  InspectIntakeRequest,
  Intake,
  IntakeStatus,
  Quote,
  ReceiveIntakeRequest,
} from '../../core/api/models';
import { parseMoneyToMinor } from '../../core/format/money';
import { isValidImei, normalizeImei } from './ankauf.logic';

/** Same minimum age as the API (Intake.MinimumSellerAge). */
export const MINIMUM_SELLER_AGE = 18;

/** Max. lengths of the API validator / database columns. */
export const SELLER_MAX_LENGTH = {
  firstName: 100,
  lastName: 100,
  street: 200,
  postalCode: 10,
  city: 100,
  idDocumentNumber: 30,
} as const;
export const NOTES_MAX_LENGTH = 2000;
export const REASON_MAX_LENGTH = 1000;

// ---------------------------------------------------------------- dates / age

export interface YMD {
  y: number;
  m: number;
  d: number;
}

/** Parses a real calendar date "yyyy-MM-dd" (as delivered by `<input type="date">`); null otherwise. */
export function parseIsoDate(value: string | null | undefined): YMD | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec((value ?? '').trim());
  if (!match) return null;
  const [y, m, d] = [Number(match[1]), Number(match[2]), Number(match[3])];
  const check = new Date(Date.UTC(y, m - 1, d));
  if (check.getUTCFullYear() !== y || check.getUTCMonth() !== m - 1 || check.getUTCDate() !== d) return null;
  return { y, m, d };
}

/** Today's calendar date in the browser's time zone. */
export function today(now: Date = new Date()): YMD {
  return { y: now.getFullYear(), m: now.getMonth() + 1, d: now.getDate() };
}

/** Completed years between birth date and `on` (a birthday on 29 Feb counts from 1 Mar in non-leap years). */
export function ageOn(birth: YMD, on: YMD): number {
  let age = on.y - birth.y;
  if (on.m < birth.m || (on.m === birth.m && on.d < birth.d)) age--;
  return age;
}

function compare(a: YMD, b: YMD): number {
  return a.y - b.y || a.m - b.m || a.d - b.d;
}

/** yyyy-MM-dd of the latest birth date that is still old enough on `on` (for the date input's `max`). */
export function latestAdultBirthDate(on: YMD): string {
  const p = (n: number, len = 2) => String(n).padStart(len, '0');
  // 29 Feb minus 18 years does not exist → 28 Feb is the latest day that is already 18.
  let d = on.d;
  while (!parseIsoDate(`${p(on.y - MINIMUM_SELLER_AGE, 4)}-${p(on.m)}-${p(d)}`)) d--;
  return `${p(on.y - MINIMUM_SELLER_AGE, 4)}-${p(on.m)}-${p(d)}`;
}

// ---------------------------------------------------------------- receive (seller + IMEI)

export interface SellerForm {
  firstName: string;
  lastName: string;
  street: string;
  postalCode: string;
  city: string;
  /** yyyy-MM-dd */
  birthDate: string;
  idDocumentType: IdDocumentType | null;
  idDocumentNumber: string;
  imei: string;
}

export type SellerField = keyof SellerForm;

export interface ReceiveValidation {
  request: ReceiveIntakeRequest | null;
  /** Field → i18n key. */
  errors: Partial<Record<SellerField, string>>;
}

/**
 * Client checks before `POST /api/ankauf/intakes` (the API stays authoritative): required fields, lengths, a real birth
 * date in the past with age ≥ 18 on `on`, Luhn-valid IMEI that equals the quote's IMEI if the quote had one.
 */
export function validateReceiveForm(quoteId: string, quoteImei: string | null, form: SellerForm, on: YMD): ReceiveValidation {
  const errors: ReceiveValidation['errors'] = {};
  const text = (field: keyof typeof SELLER_MAX_LENGTH): string => {
    const v = (form[field] ?? '').trim();
    if (!v) errors[field] = 'ankauf.intake.validation.required';
    else if (v.length > SELLER_MAX_LENGTH[field]) errors[field] = 'ankauf.intake.validation.tooLong';
    return v;
  };
  const firstName = text('firstName');
  const lastName = text('lastName');
  const street = text('street');
  const postalCode = text('postalCode');
  const city = text('city');
  const idDocumentNumber = text('idDocumentNumber');
  if (!form.idDocumentType) errors.idDocumentType = 'ankauf.intake.validation.idDocumentType';

  const birth = parseIsoDate(form.birthDate);
  if (!(form.birthDate ?? '').trim()) errors.birthDate = 'ankauf.intake.validation.required';
  else if (!birth || compare(birth, on) > 0 || birth.y < 1900) errors.birthDate = 'ankauf.intake.validation.birthDate';
  else if (ageOn(birth, on) < MINIMUM_SELLER_AGE) errors.birthDate = 'ankauf.intake.validation.minor';

  const imei = normalizeImei(form.imei);
  if (!imei) errors.imei = 'ankauf.intake.validation.required';
  else if (!isValidImei(imei)) errors.imei = 'ankauf.validation.imei';
  else if (quoteImei && quoteImei !== imei) errors.imei = 'ankauf.intake.validation.imeiMismatch';

  if (Object.keys(errors).length) return { request: null, errors };
  return {
    request: {
      quoteId,
      imei,
      seller: {
        firstName,
        lastName,
        street,
        postalCode,
        city,
        birthDate: form.birthDate.trim(),
        idDocumentType: form.idDocumentType!,
        idDocumentNumber,
      },
    },
    errors,
  };
}

/** Whether a quote can still be used for an intake (unexpired; the API also refuses quotes already used). */
export function isQuoteUsable(quote: Pick<Quote, 'validUntil'>, now: Date = new Date()): boolean {
  return new Date(quote.validUntil).getTime() > now.getTime();
}

// ---------------------------------------------------------------- inspect

export interface InspectForm {
  condition: DeviceCondition | null;
  defects: Partial<Record<DeviceDefect, boolean>>;
  /** Text input; empty = not given. */
  batteryHealth: string;
  notes: string;
}

export interface InspectValidation {
  request: InspectIntakeRequest | null;
  errors: Partial<Record<'condition' | 'battery' | 'notes', string>>;
}

export function validateInspectForm(form: InspectForm): InspectValidation {
  const errors: InspectValidation['errors'] = {};
  if (!form.condition) errors.condition = 'ankauf.validation.condition';
  let battery: number | null = null;
  const b = (form.batteryHealth ?? '').trim();
  if (b) {
    if (/^\d{1,3}$/.test(b) && Number(b) <= 100) battery = Number(b);
    else errors.battery = 'ankauf.validation.battery';
  }
  const notes = (form.notes ?? '').trim();
  if (notes.length > NOTES_MAX_LENGTH) errors.notes = 'ankauf.intake.validation.tooLong';
  if (Object.keys(errors).length) return { request: null, errors };
  const defects = (Object.entries(form.defects) as [DeviceDefect, boolean][]).filter(([, on]) => on).map(([d]) => d);
  return { request: { condition: form.condition!, defects, batteryHealthPercent: battery, notes: notes || null }, errors };
}

/** Inspect form prefilled from the last inspection, else from the quote's findings (if known). */
export function inspectFormFrom(
  intake: Pick<Intake, 'inspectedCondition' | 'inspectedDefects' | 'inspectedBatteryHealthPercent' | 'inspectionNotes'>,
  quote: Pick<Quote, 'condition' | 'defects' | 'batteryHealthPercent'> | null,
): InspectForm {
  const source = intake.inspectedCondition
    ? { condition: intake.inspectedCondition, defects: intake.inspectedDefects, battery: intake.inspectedBatteryHealthPercent }
    : quote
      ? { condition: quote.condition, defects: quote.defects, battery: quote.batteryHealthPercent }
      : { condition: null, defects: [] as DeviceDefect[], battery: null };
  return {
    condition: source.condition,
    defects: Object.fromEntries(source.defects.map((d) => [d, true])),
    batteryHealth: source.battery === null || source.battery === undefined ? '' : String(source.battery),
    notes: intake.inspectionNotes ?? '',
  };
}

/** Inspected − quoted offer in cents (negative = the device is worth less than quoted). Integer arithmetic only. */
export function offerDifferenceMinor(quotedMinor: number, inspectedMinor: number | null): number | null {
  return inspectedMinor === null ? null : inspectedMinor - quotedMinor;
}

// ---------------------------------------------------------------- complete

export interface PaidAmountCheck {
  minor: number | null;
  /** i18n key */
  error: string | null;
}

/** Amount paid to the seller: German/English decimal euros, > 0 and ≤ the inspected offer (both in cents). */
export function validatePaidAmount(input: string, inspectedOfferMinor: number | null): PaidAmountCheck {
  if (!(input ?? '').trim()) return { minor: null, error: 'ankauf.intake.validation.amountRequired' };
  const minor = parseMoneyToMinor(input);
  if (minor === null) return { minor: null, error: 'ankauf.intake.validation.amountInvalid' };
  if (minor <= 0) return { minor, error: 'ankauf.intake.validation.amountPositive' };
  if (inspectedOfferMinor === null) return { minor, error: 'ankauf.intake.validation.notInspected' };
  if (minor > inspectedOfferMinor) return { minor, error: 'ankauf.intake.validation.amountAboveOffer' };
  return { minor, error: null };
}

/** Prefill for the paid amount: the inspected max. consumer price (never above the offer), empty when 0/unknown. */
export function defaultPaidMinor(intake: Pick<Intake, 'inspectedMaxConsumerPriceMinor' | 'inspectedOfferMinor'>): number | null {
  const max = intake.inspectedMaxConsumerPriceMinor;
  const offer = intake.inspectedOfferMinor;
  if (max === null || offer === null) return null;
  const v = Math.min(max, offer);
  return v > 0 ? v : null;
}

// ---------------------------------------------------------------- steps / actions per status

export type IntakeStep = 'receive' | 'inspect' | 'complete';
export type StepState = 'done' | 'current' | 'todo' | 'skipped';
export const INTAKE_STEPS: IntakeStep[] = ['receive', 'inspect', 'complete'];

/** State of each step; `null` status = new intake (nothing recorded yet). */
export function stepStates(intake: Pick<Intake, 'status' | 'inspectedOfferMinor'> | null): Record<IntakeStep, StepState> {
  const status: IntakeStatus | null = intake?.status ?? null;
  switch (status) {
    case null:
      return { receive: 'current', inspect: 'todo', complete: 'todo' };
    case 'Received':
      return { receive: 'done', inspect: 'current', complete: 'todo' };
    case 'Inspected':
      // Re-inspection stays possible, but the next step is paying.
      return { receive: 'done', inspect: 'done', complete: 'current' };
    case 'Completed':
      return { receive: 'done', inspect: 'done', complete: 'done' };
    case 'Rejected':
      return { receive: 'done', inspect: intake?.inspectedOfferMinor !== null ? 'done' : 'skipped', complete: 'skipped' };
  }
}

export interface IntakeActions {
  inspect: boolean;
  complete: boolean;
  reject: boolean;
}

/** Mirrors the API's state machine (`ankauf.intake_closed`, `ankauf.not_inspected`). */
export function intakeActions(status: IntakeStatus): IntakeActions {
  const open = status === 'Received' || status === 'Inspected';
  return { inspect: open, complete: status === 'Inspected', reject: open };
}

export function validateReason(reason: string): string | null {
  const r = (reason ?? '').trim();
  if (!r) return 'ankauf.intake.validation.reasonRequired';
  if (r.length > REASON_MAX_LENGTH) return 'ankauf.intake.validation.tooLong';
  return null;
}

/** Quote id → intake that uses it (not rejected), to link quotes to their running/finished intake. */
export function intakeByQuote(intakes: readonly Intake[]): Map<string, Intake> {
  const map = new Map<string, Intake>();
  for (const i of intakes) if (i.status !== 'Rejected' && !map.has(i.quoteId)) map.set(i.quoteId, i);
  return map;
}
