import { ApiProblem } from '../../core/api/api-problem';
import { CreateQuoteRequest, DeviceCondition, DeviceDefect } from '../../core/api/models';
import { minorToDecimal, parseMoneyToMinor } from '../../core/format/money';

/** Removes the separators the API ignores (whitespace, '-', '/'). */
export function normalizeImei(input: string | null | undefined): string {
  return (input ?? '').replace(/[\s\-/]/g, '');
}

/** 15 digits with a valid Luhn check digit (same rule as the API). */
export function isValidImei(input: string | null | undefined): boolean {
  const imei = normalizeImei(input);
  if (!/^\d{15}$/.test(imei)) return false;
  let sum = 0;
  for (let i = 0; i < 14; i++) {
    let digit = imei.charCodeAt(i) - 48;
    if (i % 2 === 1) {
      digit *= 2;
      if (digit > 9) digit -= 9;
    }
    sum += digit;
  }
  return (10 - (sum % 10)) % 10 === imei.charCodeAt(14) - 48;
}

export interface QuoteForm {
  modelKey: string | null;
  condition: DeviceCondition | null;
  defects: Partial<Record<DeviceDefect, boolean>>;
  /** Text input; empty = not given. */
  batteryHealth: string;
  /** German or English decimal euros; empty = not given. */
  consumerPrice: string;
  imei: string;
}

export interface QuoteValidation {
  request: CreateQuoteRequest | null;
  /** Field → i18n key. */
  errors: Partial<Record<'model' | 'condition' | 'battery' | 'consumerPrice' | 'imei', string>>;
}

export function validateQuoteForm(form: QuoteForm): QuoteValidation {
  const errors: QuoteValidation['errors'] = {};
  if (!form.modelKey) errors.model = 'ankauf.validation.model';
  if (!form.condition) errors.condition = 'ankauf.validation.condition';

  let battery: number | null = null;
  const b = (form.batteryHealth ?? '').trim();
  if (b) {
    if (/^\d{1,3}$/.test(b) && Number(b) <= 100) battery = Number(b);
    else errors.battery = 'ankauf.validation.battery';
  }

  let consumerMinor: number | null = null;
  if ((form.consumerPrice ?? '').trim()) {
    consumerMinor = parseMoneyToMinor(form.consumerPrice);
    if (consumerMinor === null || consumerMinor < 0) errors.consumerPrice = 'ankauf.validation.consumerPrice';
  }

  let imei: string | null = null;
  if ((form.imei ?? '').trim()) {
    if (isValidImei(form.imei)) imei = normalizeImei(form.imei);
    else errors.imei = 'ankauf.validation.imei';
  }

  if (Object.keys(errors).length) return { request: null, errors };
  const defects = (Object.entries(form.defects) as [DeviceDefect, boolean][]).filter(([, on]) => on).map(([d]) => d);
  return {
    request: {
      modelKey: form.modelKey!,
      condition: form.condition!,
      defects,
      batteryHealthPercent: battery,
      consumerPrice: consumerMinor === null ? null : minorToDecimal(consumerMinor),
      imei,
    },
    errors,
  };
}

/** Model label "Apple iPhone 13 128 GB". */
export function modelLabel(m: { brand: string; model: string; storage: string | null }): string {
  return [m.brand, m.model, m.storage].filter((x) => !!x).join(' ');
}

/** Codes whose API title carries useful detail (sheet names, duplicate rows, counts) that is appended to the translation. */
const DETAIL_KEYS = new Set([
  'errors.codes.ankauf.sheet_missing',
  'errors.codes.ankauf.duplicate_models',
  'errors.codes.ankauf.unreadable',
  'errors.codes.ankauf.pending_decisions',
  'errors.codes.ankauf.policy_range',
]);

/**
 * Ankauf error text: the translated message for known codes, plus the API's own detail for codes where it names
 * sheets/rows. Falls back to the generic problem description.
 */
export function ankaufProblemText(
  problem: ApiProblem,
  t: (key: string) => string,
  has: (key: string) => boolean,
  fallback: (p: ApiProblem) => string,
): string {
  const key = problem.code ? `errors.codes.${problem.code}` : '';
  if (key && has(key)) {
    return DETAIL_KEYS.has(key) && problem.title ? `${t(key)} (${problem.title})` : t(key);
  }
  return fallback(problem);
}
