import {
  DeviceCondition,
  DeviceDefect,
  IdDocumentType,
  InspectIntakeRequest,
  Intake,
  IntakePaymentMethod,
  IntakeStatus,
  PayoutRegister,
  Quote,
  ReceiveIntakeRequest,
  SellerDetails,
  SellerSuggestion,
} from '../../core/api/models';
import { parseMoneyToMinor } from '../../core/format/money';
import { isValidImei, normalizeImei } from './ankauf.logic';
import { MrzData, mrzNameCase } from './mrz';

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
  idIssuingAuthority: 100,
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
  /** yyyy-MM-dd */
  idExpiryDate: string;
  /** ICAO code (MRZ), e.g. "D"; optional. */
  nationality: string;
  idIssuingAuthority: string;
  idIssuingCountry: string;
  /** ISO alpha-2 of the address; empty = Germany. */
  country: string;
  imei: string;
}

export function emptySellerForm(): SellerForm {
  return {
    firstName: '',
    lastName: '',
    street: '',
    postalCode: '',
    city: '',
    birthDate: '',
    idDocumentType: null,
    idDocumentNumber: '',
    idExpiryDate: '',
    nationality: '',
    idIssuingAuthority: '',
    idIssuingCountry: '',
    country: '',
    imei: '',
  };
}

/** Fills name, birth date and the ID document from a verified MRZ; the address stays (it is not in the MRZ). */
export function applyMrz(form: SellerForm, mrz: MrzData): SellerForm {
  return {
    ...form,
    firstName: mrzNameCase(mrz.givenNames) || form.firstName,
    lastName: mrzNameCase(mrz.surname) || form.lastName,
    birthDate: mrz.birthDate,
    idDocumentType: mrz.documentType,
    idDocumentNumber: mrz.documentNumber,
    idExpiryDate: mrz.expiryDate,
    nationality: mrz.nationality,
    idIssuingCountry: mrz.issuingState,
  };
}

/** Takes the data of a self check-in (everything the seller entered). */
export function applySellerDetails(form: SellerForm, s: SellerDetails): SellerForm {
  return {
    ...form,
    firstName: s.firstName,
    lastName: s.lastName,
    street: s.street,
    postalCode: s.postalCode,
    city: s.city,
    birthDate: s.birthDate,
    idDocumentType: s.idDocumentType,
    idDocumentNumber: s.idDocumentNumber,
    idExpiryDate: s.idExpiryDate ?? '',
    nationality: s.nationality ?? '',
    idIssuingAuthority: s.idIssuingAuthority ?? '',
    idIssuingCountry: s.idIssuingCountry ?? '',
    country: s.country ?? '',
  };
}

/** A returning seller: person and address only — the ID document is presented and captured again every time. */
export function applySuggestion(form: SellerForm, s: SellerSuggestion): SellerForm {
  return {
    ...form,
    firstName: s.firstName,
    lastName: s.lastName,
    street: s.street,
    postalCode: s.postalCode,
    city: s.city,
    birthDate: s.birthDate,
    nationality: s.nationality ?? '',
    country: s.country ?? '',
    idDocumentType: null,
    idDocumentNumber: '',
    idExpiryDate: '',
    idIssuingAuthority: '',
    idIssuingCountry: '',
  };
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
export function validateReceiveForm(
  quoteId: string,
  quoteImei: string | null,
  form: SellerForm,
  on: YMD,
  source: { mrz?: string | null; checkInId?: string | null } = {},
): ReceiveValidation {
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
  if (idDocumentNumber && !errors.idDocumentNumber && !/^[A-Za-z0-9 <-]{2,30}$/.test(idDocumentNumber)) errors.idDocumentNumber = 'ankauf.intake.validation.idNumber';

  const expiry = parseIsoDate(form.idExpiryDate);
  if (!(form.idExpiryDate ?? '').trim()) errors.idExpiryDate = 'ankauf.intake.validation.required';
  else if (!expiry) errors.idExpiryDate = 'ankauf.intake.validation.date';
  else if (compare(expiry, on) < 0) errors.idExpiryDate = 'ankauf.intake.validation.idExpired';

  const code = (v: string, re: RegExp, field: 'nationality' | 'idIssuingCountry' | 'country'): string | null => {
    const c = (v ?? '').trim().toUpperCase().replace(/<+$/, '');
    if (!c) return null;
    if (!re.test(c)) errors[field] = 'ankauf.intake.validation.countryCode';
    return c;
  };
  const nationality = code(form.nationality, /^[A-Z]{1,3}$/, 'nationality');
  const idIssuingCountry = code(form.idIssuingCountry, /^[A-Z]{1,3}$/, 'idIssuingCountry');
  const country = code(form.country, /^[A-Z]{2}$/, 'country');
  const authority = (form.idIssuingAuthority ?? '').trim();
  if (authority.length > SELLER_MAX_LENGTH.idIssuingAuthority) errors.idIssuingAuthority = 'ankauf.intake.validation.tooLong';

  const birth = parseIsoDate(form.birthDate);
  if (!(form.birthDate ?? '').trim()) errors.birthDate = 'ankauf.intake.validation.required';
  else if (!birth || compare(birth, on) > 0 || birth.y < 1900) errors.birthDate = 'ankauf.intake.validation.birthDate';
  else if (ageOn(birth, on) < MINIMUM_SELLER_AGE) errors.birthDate = 'ankauf.intake.validation.minor';

  const imei = normalizeImei(form.imei);
  if (!imei) errors.imei = 'ankauf.intake.validation.required';
  else if (!isValidImei(imei)) errors.imei = 'ankauf.validation.imei';
  else if (quoteImei && quoteImei !== imei) errors.imei = 'ankauf.intake.validation.imeiMismatch';

  if (Object.keys(errors).length) return { request: null, errors };
  const request: ReceiveIntakeRequest = {
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
      idExpiryDate: form.idExpiryDate.trim(),
      nationality,
      idIssuingAuthority: authority || null,
      idIssuingCountry,
      country: country === 'DE' ? null : country,
    },
  };
  if (source.mrz) request.mrz = source.mrz;
  if (source.checkInId) request.checkInId = source.checkInId;
  return { request, errors };
}

/** Days until the ID document expires (negative = expired); null if no date. */
export function daysUntilExpiry(expiry: string | null | undefined, on: YMD): number | null {
  const e = parseIsoDate(expiry);
  if (!e) return null;
  return Math.round((Date.UTC(e.y, e.m - 1, e.d) - Date.UTC(on.y, on.m - 1, on.d)) / 86_400_000);
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

export type IntakeStep = 'receive' | 'inspect' | 'sign' | 'complete';
export type StepState = 'done' | 'current' | 'todo' | 'skipped';
export const INTAKE_STEPS: IntakeStep[] = ['receive', 'inspect', 'sign', 'complete'];

/** State of each step; `null` = new intake (nothing recorded yet). Signing is done once a valid purchase signature exists. */
export function stepStates(
  intake: (Pick<Intake, 'status' | 'inspectedOfferMinor'> & { signature?: { valid: boolean } | null; signatureRequired?: boolean }) | null,
): Record<IntakeStep, StepState> {
  const status: IntakeStatus | null = intake?.status ?? null;
  const signed = !!intake?.signature?.valid;
  switch (status) {
    case null:
      return { receive: 'current', inspect: 'todo', sign: 'todo', complete: 'todo' };
    case 'Received':
      return { receive: 'done', inspect: 'current', sign: 'todo', complete: 'todo' };
    case 'Inspected':
      // Re-inspection stays possible (it invalidates a signature); then the seller signs, then payment.
      return signed || intake?.signatureRequired === false
        ? { receive: 'done', inspect: 'done', sign: signed ? 'done' : 'skipped', complete: 'current' }
        : { receive: 'done', inspect: 'done', sign: 'current', complete: 'todo' };
    case 'Completed':
      return { receive: 'done', inspect: 'done', sign: intake?.signature ? 'done' : 'skipped', complete: 'done' };
    case 'Rejected':
      return { receive: 'done', inspect: intake?.inspectedOfferMinor !== null ? 'done' : 'skipped', sign: 'skipped', complete: 'skipped' };
  }
}

export interface IntakeActions {
  inspect: boolean;
  sign: boolean;
  complete: boolean;
  reject: boolean;
}

/** Mirrors the API's state machine (`ankauf.intake_closed`, `ankauf.not_inspected`). */
export function intakeActions(status: IntakeStatus): IntakeActions {
  const open = status === 'Received' || status === 'Inspected';
  return { inspect: open, sign: status === 'Inspected', complete: status === 'Inspected', reject: open };
}

/** none = not signed yet; valid = confirms the current data, amount and method; stale = something changed after signing. */
export function signatureState(intake: Pick<Intake, 'signature'>): 'none' | 'valid' | 'stale' {
  const s = intake.signature;
  return !s ? 'none' : s.valid ? 'valid' : 'stale';
}

/** Whether the payment to submit is exactly the signed one (otherwise the API refuses with `ankauf.signature_invalid`). */
export function matchesSignature(intake: Pick<Intake, 'signature'>, minor: number, method: IntakePaymentMethod): boolean {
  const s = intake.signature;
  return !!s && s.valid && s.paidMinor === minor && s.paymentMethod === method;
}

/**
 * Cash from the till: needed for cash purchases when the organization uses the POS module. Returns an i18n key when the
 * chosen register cannot pay (no register, no open session, not enough cash), else null.
 */
export function validatePayout(
  method: IntakePaymentMethod,
  tillRequired: boolean,
  registerId: string | null,
  registers: readonly PayoutRegister[],
  amountMinor: number,
): string | null {
  if (method !== 'Cash' || !tillRequired) return null;
  if (!registerId) return 'ankauf.pay.registerRequired';
  const r = registers.find((x) => x.id === registerId);
  if (!r || !r.openSessionId) return 'ankauf.pay.noOpenSession';
  if ((r.expectedCashMinor ?? 0) < amountMinor) return 'ankauf.pay.notEnoughCash';
  return null;
}

/** Preselects the till: the given register (POS context) if usable, else the only register with an open session. */
export function defaultPayoutRegister(registers: readonly PayoutRegister[], preferred: string | null): string | null {
  if (preferred && registers.some((r) => r.id === preferred)) return preferred;
  const open = registers.filter((r) => r.openSessionId);
  return open.length === 1 ? open[0].id : null;
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

/** The seller part of the receive validation (self check-in form on the seller's phone: no device, no IMEI). */
export function validateSellerForm(form: SellerForm, on: YMD): { seller: SellerDetails | null; errors: Partial<Record<SellerField, string>> } {
  // A Luhn-valid placeholder IMEI: only the seller fields matter here.
  const v = validateReceiveForm('-', null, { ...form, imei: '490154203237518' }, on);
  const { imei: _imei, ...errors } = v.errors;
  return { seller: v.request?.seller ?? null, errors };
}
