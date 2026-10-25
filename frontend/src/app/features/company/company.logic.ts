import { CompanyProfile, CompanyProfileFields, UpdateCompanyProfileRequest } from '../../core/api/models';

/** Limits mirrored from the API (CompanyProfileRules / OrganizationLogo). */
export const COMPANY_LIMITS = {
  receiptHeader: 500,
  receiptFooter: 1000,
  receiptLines: 12,
  logoBytes: 2 * 1024 * 1024,
  logoMinSide: 16,
  paymentTermsMax: 365,
} as const;

/** Form state: every field as text (inputs), payment terms included. */
export type CompanyForm = { [K in keyof CompanyProfileFields]: string };

export type CompanyField = keyof CompanyForm;

const FIELDS: CompanyField[] = [
  'name', 'legalName', 'street', 'postalCode', 'city', 'countryCode', 'vatId', 'taxNumber', 'contactEmail', 'contactPhone', 'website',
  'receiptHeader', 'receiptFooter', 'bankName', 'iban', 'bic', 'managingDirector', 'registerCourt', 'registerNumber', 'paymentTermsDays',
];

export function formFromProfile(p: CompanyProfile): CompanyForm {
  const form = {} as CompanyForm;
  for (const f of FIELDS) {
    const v = p[f];
    form[f] = v === null || v === undefined ? '' : f === 'iban' ? formatIban(String(v)) : String(v);
  }
  return form;
}

const blank = (v: string): string | null => (v.trim() === '' ? null : v.trim());

/** Multi-line receipt texts: normalized line breaks, trailing spaces removed, surrounding blank lines dropped. */
export function normalizeMultiline(v: string): string | null {
  const text = v.replace(/\r\n?/g, '\n').split('\n').map((l) => l.trimEnd()).join('\n').replace(/^\n+|\n+$/g, '');
  return text.trim() === '' ? null : text;
}

export function requestFromForm(form: CompanyForm, version: number): UpdateCompanyProfileRequest {
  const terms = form.paymentTermsDays.trim();
  return {
    name: form.name.trim(),
    legalName: form.legalName.trim(),
    street: form.street.trim(),
    postalCode: form.postalCode.trim(),
    city: form.city.trim(),
    countryCode: (blank(form.countryCode) ?? 'DE').toUpperCase(),
    vatId: blank(form.vatId)?.replace(/\s+/g, '').toUpperCase() ?? null,
    taxNumber: blank(form.taxNumber),
    contactEmail: blank(form.contactEmail),
    contactPhone: blank(form.contactPhone),
    website: blank(form.website),
    receiptHeader: normalizeMultiline(form.receiptHeader),
    receiptFooter: normalizeMultiline(form.receiptFooter),
    bankName: blank(form.bankName),
    iban: normalizeIban(form.iban),
    bic: blank(form.bic)?.replace(/\s+/g, '').toUpperCase() ?? null,
    managingDirector: blank(form.managingDirector),
    registerCourt: blank(form.registerCourt),
    registerNumber: blank(form.registerNumber),
    paymentTermsDays: terms === '' ? null : Number(terms),
    version,
  };
}

export function normalizeIban(v: string | null | undefined): string | null {
  const s = (v ?? '').replace(/\s+/g, '').toUpperCase();
  return s === '' ? null : s;
}

/** "DE89370400440532013000" → "DE89 3704 0044 0532 0130 00". */
export function formatIban(v: string): string {
  return (normalizeIban(v) ?? '').replace(/(.{4})(?=.)/g, '$1 ');
}

const IBAN_LENGTHS: Record<string, number> = {
  AD: 24, AT: 20, BE: 16, BG: 22, CH: 21, CY: 28, CZ: 24, DE: 22, DK: 18, EE: 20, ES: 24, FI: 18, FR: 27, GB: 22, GI: 23, GR: 27, HR: 21,
  HU: 28, IE: 22, IS: 26, IT: 27, LI: 21, LT: 20, LU: 20, LV: 21, MC: 27, MT: 31, NL: 18, NO: 15, PL: 28, PT: 25, RO: 24, SE: 24, SI: 19,
  SK: 24, SM: 27, VA: 22, TR: 26,
};

/** ISO 13616: shape, country length (when known) and the mod-97 checksum (same rule as the API). */
export function isValidIban(v: string): boolean {
  const iban = normalizeIban(v);
  if (!iban || iban.length < 15 || iban.length > 34 || !/^[A-Z]{2}[0-9]{2}[A-Z0-9]+$/.test(iban)) return false;
  const expected = IBAN_LENGTHS[iban.slice(0, 2)];
  if (expected && iban.length !== expected) return false;
  const rearranged = iban.slice(4) + iban.slice(0, 4);
  let remainder = 0;
  for (const c of rearranged) {
    const digits = c >= 'A' && c <= 'Z' ? String(c.charCodeAt(0) - 55) : c;
    for (const d of digits) remainder = (remainder * 10 + Number(d)) % 97;
  }
  return remainder === 1;
}

export function isValidBic(v: string): boolean {
  return /^[A-Z]{4}[A-Z]{2}[A-Z0-9]{2}([A-Z0-9]{3})?$/.test(v.replace(/\s+/g, '').toUpperCase());
}

export function isValidVatId(v: string, country: string): boolean {
  const vat = v.replace(/[\s.\-_]+/g, '').toUpperCase();
  return (country.trim().toUpperCase() || 'DE') === 'DE' ? /^DE\d{9}$/.test(vat) : /^[A-Z]{2}[A-Z0-9]{2,12}$/.test(vat);
}

const lineCount = (v: string): number => (v.trim() === '' ? 0 : v.trim().replace(/\r\n?/g, '\n').split('\n').length);

/** Client-side checks (the API validates again); values are i18n keys under `company.errors`. */
export function validateCompanyForm(form: CompanyForm): Partial<Record<CompanyField, string>> {
  const e: Partial<Record<CompanyField, string>> = {};
  const country = form.countryCode.trim().toUpperCase() || 'DE';
  for (const f of ['name', 'legalName', 'street', 'postalCode', 'city'] as const) {
    if (form[f].trim() === '') e[f] = 'company.errors.required';
  }
  if (!/^[A-Za-z]{2}$/.test(country)) e.countryCode = 'company.errors.country';
  if (!e.postalCode && country === 'DE' && !/^\d{5}$/.test(form.postalCode.trim())) e.postalCode = 'company.errors.postalCode';
  if (form.vatId.trim() && !isValidVatId(form.vatId, country)) e.vatId = 'company.errors.vatId';
  if (form.taxNumber.trim()) {
    const t = form.taxNumber.trim();
    const digits = t.replace(/\D/g, '').length;
    if (!/^\d[\d/ ]*\d$/.test(t) || digits < 10 || digits > 13) e.taxNumber = 'company.errors.taxNumber';
  }
  if (form.contactEmail.trim() && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(form.contactEmail.trim())) e.contactEmail = 'company.errors.email';
  if (form.contactPhone.trim() && !/^\+?[0-9 ()/-]{6,40}$/.test(form.contactPhone.trim())) e.contactPhone = 'company.errors.phone';
  if (form.website.trim() && !/^https?:\/\/[^\s/]+\.[^\s]+$/i.test(form.website.trim())) e.website = 'company.errors.website';
  if (form.receiptHeader.length > COMPANY_LIMITS.receiptHeader || lineCount(form.receiptHeader) > COMPANY_LIMITS.receiptLines)
    e.receiptHeader = 'company.errors.receiptText';
  if (form.receiptFooter.length > COMPANY_LIMITS.receiptFooter || lineCount(form.receiptFooter) > COMPANY_LIMITS.receiptLines)
    e.receiptFooter = 'company.errors.receiptText';
  if (form.iban.trim() && !isValidIban(form.iban)) e.iban = 'company.errors.iban';
  if (!form.iban.trim() && (form.bic.trim() || form.bankName.trim())) e.iban = 'company.errors.ibanRequired';
  if (form.bic.trim() && !isValidBic(form.bic)) e.bic = 'company.errors.bic';
  const terms = form.paymentTermsDays.trim();
  if (terms && (!/^\d+$/.test(terms) || Number(terms) > COMPANY_LIMITS.paymentTermsMax)) e.paymentTermsDays = 'company.errors.paymentTerms';
  return e;
}

/** API field error keys ("Iban", "ReceiptFooter") → form fields. */
export function fieldOfApiError(key: string): CompanyField | null {
  const f = (key.charAt(0).toLowerCase() + key.slice(1)) as CompanyField;
  return FIELDS.includes(f) ? f : null;
}

export function isDirty(form: CompanyForm, original: CompanyForm): boolean {
  return FIELDS.some((f) => form[f] !== original[f]);
}

/** Legal identity fields whose change the platform administration is told about. */
export function legalChanges(form: CompanyForm, original: CompanyForm): ('legalName' | 'vatId')[] {
  const out: ('legalName' | 'vatId')[] = [];
  if (form.legalName.trim() !== original.legalName.trim()) out.push('legalName');
  if ((normalizeIban(form.vatId) ?? '') !== (normalizeIban(original.vatId) ?? '')) out.push('vatId');
  return out;
}

// ---- logo crop ------------------------------------------------------------------------------------------------------

export type CropAspect = 'free' | 'square' | 'wide';

export interface CropRegion {
  x: number;
  y: number;
  width: number;
  height: number;
}

/**
 * Source rectangle of the crop: the largest rectangle with the chosen aspect (free = the image's own) that fits the image,
 * shrunk by `zoom` (1 = all of it, up to 4), moved by `panX` / `panY` (−1 … 1 = from one edge to the other). Integer pixels.
 */
export function cropRegion(imageWidth: number, imageHeight: number, aspect: CropAspect, zoom: number, panX: number, panY: number): CropRegion {
  const ratio = aspect === 'square' ? 1 : aspect === 'wide' ? 3 : imageWidth / imageHeight;
  let width = imageWidth;
  let height = width / ratio;
  if (height > imageHeight) {
    height = imageHeight;
    width = height * ratio;
  }
  const z = Math.min(4, Math.max(1, zoom));
  width /= z;
  height /= z;
  const clamp = (v: number) => Math.min(1, Math.max(-1, v));
  const x = ((imageWidth - width) / 2) * (1 + clamp(panX));
  const y = ((imageHeight - height) / 2) * (1 + clamp(panY));
  return { x: Math.round(x), y: Math.round(y), width: Math.max(1, Math.round(width)), height: Math.max(1, Math.round(height)) };
}

/** Output size of the cropped logo: longest side at most `maxSide` (never enlarged). */
export function fitWithin(width: number, height: number, maxSide: number): { width: number; height: number } {
  const longest = Math.max(width, height);
  if (longest <= maxSide) return { width, height };
  const s = maxSide / longest;
  return { width: Math.max(1, Math.round(width * s)), height: Math.max(1, Math.round(height * s)) };
}

/** Quick client-side type/size check before cropping (the API checks the bytes again). */
export function logoFileProblem(file: { type: string; size: number }): string | null {
  if (!['image/png', 'image/jpeg', 'image/webp'].includes(file.type)) return 'company.logo.errors.type';
  if (file.size > COMPANY_LIMITS.logoBytes) return 'company.logo.errors.size';
  return null;
}

/** Lines of a receipt text for the preview (blank lines kept as spacers). */
export function receiptLines(text: string | null | undefined): string[] {
  return text ? text.replace(/\r\n?/g, '\n').split('\n') : [];
}
