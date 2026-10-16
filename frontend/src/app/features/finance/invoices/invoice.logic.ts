import { InvoiceParty, InvoiceSummary, IssueRunResult } from '../../../core/api/models';

/**
 * Pure helpers of the commission invoice screens (ADR-0009). Months and printed dates follow German local time
 * (Europe/Berlin) like the API; the API stays authoritative for every check mirrored here.
 */

export const INVOICE_TIME_ZONE = 'Europe/Berlin';

const MONTH_PATTERN = /^(\d{4})-(0[1-9]|1[0-2])$/;
const DATE_PATTERN = /^(\d{4})-(\d{2})-(\d{2})$/;

/** Marker the API uses for unconfigured issuer data (outside Production the PDF is a "MUSTER"). */
export const PLACEHOLDER_MARKER = 'PLATZHALTER';

// ---------------------------------------------------------------- months

/** Calendar month (`yyyy-MM`) that contains `now` in German local time. */
export function berlinMonth(now: Date): string {
  const parts = new Intl.DateTimeFormat('en-CA', { timeZone: INVOICE_TIME_ZONE, year: 'numeric', month: '2-digit' }).formatToParts(now);
  const year = parts.find((p) => p.type === 'year')?.value ?? '';
  const month = parts.find((p) => p.type === 'month')?.value ?? '';
  return `${year.padStart(4, '0')}-${month}`;
}

/** `shiftMonth('2026-01', -1)` → `'2025-12'`. Integer arithmetic only. */
export function shiftMonth(month: string, delta: number): string {
  const m = MONTH_PATTERN.exec(month);
  if (!m) return month;
  const index = Number(m[1]) * 12 + (Number(m[2]) - 1) + delta;
  const year = Math.floor(index / 12);
  return `${String(year).padStart(4, '0')}-${String(index - year * 12 + 1).padStart(2, '0')}`;
}

/** The month the admin form proposes: the previous month in German time (the one the monthly job issues). */
export function defaultIssueMonth(now: Date): string {
  return shiftMonth(berlinMonth(now), -1);
}

/**
 * Client check of the month to issue (mirrors the API: `YYYY-MM`, year ≥ 2000, month must be over in German time).
 * Returns a translation key or null.
 */
export function validateIssueMonth(value: string | null | undefined, now: Date): string | null {
  const v = (value ?? '').trim();
  if (!v) return 'invoices.admin.validation.required';
  const m = MONTH_PATTERN.exec(v);
  if (!m || Number(m[1]) < 2000) return 'invoices.admin.validation.format';
  // `yyyy-MM` strings compare chronologically.
  if (v >= berlinMonth(now)) return 'invoices.admin.validation.notOver';
  return null;
}

/** `'2026-09'` → "September 2026" (in the UI locale); unknown formats are returned unchanged. */
export function formatBillingMonth(month: string | null | undefined, locale: string): string {
  if (!month) return '–';
  const m = MONTH_PATTERN.exec(month);
  if (!m) return month;
  return new Intl.DateTimeFormat(locale, { month: 'long', year: 'numeric', timeZone: 'UTC' }).format(
    new Date(Date.UTC(Number(m[1]), Number(m[2]) - 1, 1)),
  );
}

/**
 * Calendar dates (`yyyy-MM-dd`, DateOnly in the API) formatted without any time zone shift — `new Date('2026-10-01')`
 * would be UTC midnight and show the previous day west of UTC.
 */
export function formatDateOnly(value: string | null | undefined, locale: string): string {
  if (!value) return '–';
  const m = DATE_PATTERN.exec(value);
  if (!m) return value;
  return new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeZone: 'UTC' }).format(
    new Date(Date.UTC(Number(m[1]), Number(m[2]) - 1, Number(m[3]))),
  );
}

// ---------------------------------------------------------------- documents

/** VAT rate for display: 1900 → "19 %" (locale-aware decimal separator, display only). */
export function formatVatRate(basisPoints: number, locale: string): string {
  return `${new Intl.NumberFormat(locale, { maximumFractionDigits: 2 }).format(basisPoints / 100)} %`;
}

/** Address block as printed: legal name (or name), trading name if different, street, postal code + city, country. */
export function partyLines(party: InvoiceParty): string[] {
  const display = party.legalName?.trim() ? party.legalName.trim() : party.name;
  const lines = [display];
  if (party.name && party.name !== display) lines.push(party.name);
  lines.push(party.street, `${party.postalCode} ${party.city}`.trim(), party.countryCode);
  return lines.filter((l) => !!l && l.trim().length > 0);
}

/** True while the issuer data still contains the configuration placeholder (document is only a sample). */
export function isPlaceholderParty(party: InvoiceParty | null | undefined): boolean {
  if (!party) return false;
  return [party.name, party.legalName, party.street, party.postalCode, party.city, party.vatId, party.taxNumber].some(
    (v) => !!v && v.toUpperCase().includes(PLACEHOLDER_MARKER),
  );
}

/** File name of the downloaded PDF (the number is already safe; anything unexpected is replaced). */
export function invoicePdfFileName(number: string): string {
  const safe = number.replace(/[^A-Za-z0-9._-]/g, '_');
  return `${safe || 'rechnung'}.pdf`;
}

export interface MinorTotals {
  netMinor: number;
  vatMinor: number;
  grossMinor: number;
}

/** Sum of documents in integer cents (corrections are negative, so the result is the month's net effect). */
export function sumDocuments(documents: readonly Pick<InvoiceSummary, 'totalNetMinor' | 'totalVatMinor' | 'totalGrossMinor'>[]): MinorTotals {
  return documents.reduce(
    (acc, d) => ({ netMinor: acc.netMinor + d.totalNetMinor, vatMinor: acc.vatMinor + d.totalVatMinor, grossMinor: acc.grossMinor + d.totalGrossMinor }),
    { netMinor: 0, vatMinor: 0, grossMinor: 0 },
  );
}

// ---------------------------------------------------------------- issue run

export type RunOutcome = 'failed' | 'issued' | 'unchanged' | 'empty';

/**
 * Summary of an issue run: `failed` if any seller failed (the others are issued; re-running is safe), `issued` if new
 * documents were created, `unchanged` if everything already existed (idempotent re-run), `empty` if no seller had
 * commission or refunds in that month.
 */
export function runOutcome(result: Pick<IssueRunResult, 'failures' | 'invoicesIssued' | 'creditNotesIssued' | 'alreadyIssued'>): RunOutcome {
  if (result.failures.length > 0) return 'failed';
  if (result.invoicesIssued + result.creditNotesIssued > 0) return 'issued';
  if (result.alreadyIssued > 0) return 'unchanged';
  return 'empty';
}

export function runOutcomeTone(outcome: RunOutcome): 'warning' | 'success' | 'info' {
  return outcome === 'failed' ? 'warning' : outcome === 'issued' ? 'success' : 'info';
}

/** Translation key of the run outcome message. */
export function runOutcomeKey(outcome: RunOutcome): string {
  switch (outcome) {
    case 'failed':
      return 'invoices.admin.outcome.failed';
    case 'issued':
      return 'invoices.admin.outcome.issued';
    case 'unchanged':
      return 'invoices.admin.outcome.unchanged';
    default:
      return 'invoices.admin.outcome.empty';
  }
}
