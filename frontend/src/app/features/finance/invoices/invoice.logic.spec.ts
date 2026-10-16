import { InvoiceParty } from '../../../core/api/models';
import {
  berlinMonth,
  defaultIssueMonth,
  formatBillingMonth,
  formatDateOnly,
  formatVatRate,
  invoicePdfFileName,
  isPlaceholderParty,
  partyLines,
  runOutcome,
  runOutcomeKey,
  runOutcomeTone,
  shiftMonth,
  sumDocuments,
  validateIssueMonth,
} from './invoice.logic';

const party = (over: Partial<InvoiceParty> = {}): InvoiceParty => ({
  name: 'Handy Shop',
  legalName: 'Handy Shop GmbH',
  street: 'Teststraße 1',
  postalCode: '10115',
  city: 'Berlin',
  countryCode: 'DE',
  vatId: 'DE123456789',
  taxNumber: null,
  ...over,
});

describe('invoice months (German time)', () => {
  it('uses Europe/Berlin, not UTC, around midnight at the month boundary', () => {
    // 2026-09-30 22:30 UTC is already 1 October 00:30 in Berlin (CEST, UTC+2).
    expect(berlinMonth(new Date('2026-09-30T22:30:00Z'))).toBe('2026-10');
    expect(berlinMonth(new Date('2026-09-30T21:59:00Z'))).toBe('2026-09');
    // Winter time (CET, UTC+1): 31 Dec 23:30 UTC = 1 Jan 00:30 Berlin.
    expect(berlinMonth(new Date('2026-12-31T23:30:00Z'))).toBe('2027-01');
  });

  it('shifts months across years with integer arithmetic', () => {
    expect(shiftMonth('2026-01', -1)).toBe('2025-12');
    expect(shiftMonth('2026-12', 1)).toBe('2027-01');
    expect(shiftMonth('2026-05', -17)).toBe('2024-12');
    expect(shiftMonth('nonsense', -1)).toBe('nonsense');
  });

  it('proposes the previous month (the one the monthly job issues)', () => {
    expect(defaultIssueMonth(new Date('2026-10-16T10:00:00Z'))).toBe('2026-09');
    expect(defaultIssueMonth(new Date('2026-01-01T00:30:00+01:00'))).toBe('2025-12');
  });

  it('validates the month like the API (format, year ≥ 2000, month over)', () => {
    const now = new Date('2026-10-16T10:00:00Z');
    expect(validateIssueMonth('', now)).toBe('invoices.admin.validation.required');
    expect(validateIssueMonth('  ', now)).toBe('invoices.admin.validation.required');
    expect(validateIssueMonth(null, now)).toBe('invoices.admin.validation.required');
    expect(validateIssueMonth('2026-13', now)).toBe('invoices.admin.validation.format');
    expect(validateIssueMonth('2026-9', now)).toBe('invoices.admin.validation.format');
    expect(validateIssueMonth('1999-12', now)).toBe('invoices.admin.validation.format');
    expect(validateIssueMonth('2026-10', now)).toBe('invoices.admin.validation.notOver');
    expect(validateIssueMonth('2027-01', now)).toBe('invoices.admin.validation.notOver');
    expect(validateIssueMonth('2026-09', now)).toBeNull();
    expect(validateIssueMonth(' 2025-02 ', now)).toBeNull();
  });

  it('treats the month as over only once it has ended in Berlin', () => {
    // 30 Sep 23:30 Berlin = 21:30 UTC → September is not over yet.
    expect(validateIssueMonth('2026-09', new Date('2026-09-30T21:30:00Z'))).toBe('invoices.admin.validation.notOver');
    expect(validateIssueMonth('2026-09', new Date('2026-09-30T22:00:00Z'))).toBeNull();
  });
});

describe('invoice formatting', () => {
  it('formats billing months in the UI locale without a time zone shift', () => {
    expect(formatBillingMonth('2026-09', 'de-DE')).toBe('September 2026');
    expect(formatBillingMonth('2026-01', 'en')).toBe('January 2026');
    expect(formatBillingMonth('weird', 'de-DE')).toBe('weird');
    expect(formatBillingMonth(null, 'de-DE')).toBe('–');
  });

  it('formats calendar dates as the given day', () => {
    expect(formatDateOnly('2026-10-01', 'de-DE')).toBe('01.10.2026');
    expect(formatDateOnly('2026-09-30', 'en')).toBe('Sep 30, 2026');
    expect(formatDateOnly(undefined, 'de-DE')).toBe('–');
    expect(formatDateOnly('not a date', 'de-DE')).toBe('not a date');
  });

  it('shows the VAT rate from basis points', () => {
    expect(formatVatRate(1900, 'de-DE')).toBe('19 %');
    expect(formatVatRate(750, 'de-DE')).toBe('7,5 %');
    expect(formatVatRate(750, 'en')).toBe('7.5 %');
  });

  it('builds the address block (legal name first, trading name if different)', () => {
    expect(partyLines(party())).toEqual(['Handy Shop GmbH', 'Handy Shop', 'Teststraße 1', '10115 Berlin', 'DE']);
    expect(partyLines(party({ legalName: null }))).toEqual(['Handy Shop', 'Teststraße 1', '10115 Berlin', 'DE']);
    expect(partyLines(party({ legalName: 'Handy Shop' }))).toEqual(['Handy Shop', 'Teststraße 1', '10115 Berlin', 'DE']);
  });

  it('detects placeholder issuer data (sample documents)', () => {
    expect(isPlaceholderParty(party())).toBe(false);
    expect(isPlaceholderParty(party({ vatId: 'DE-PLATZHALTER' }))).toBe(true);
    expect(isPlaceholderParty(party({ street: 'Platzhalterstraße 1' }))).toBe(true);
    expect(isPlaceholderParty(null)).toBe(false);
  });

  it('names the PDF after the number', () => {
    expect(invoicePdfFileName('HN-2026-000001')).toBe('HN-2026-000001.pdf');
    expect(invoicePdfFileName('HN-RK-2026-000002')).toBe('HN-RK-2026-000002.pdf');
    expect(invoicePdfFileName('a/b\\c')).toBe('a_b_c.pdf');
    expect(invoicePdfFileName('')).toBe('rechnung.pdf');
  });

  it('sums documents in integer cents including negative corrections', () => {
    const totals = sumDocuments([
      { totalNetMinor: 1001, totalVatMinor: 190, totalGrossMinor: 1191 },
      { totalNetMinor: 2, totalVatMinor: 1, totalGrossMinor: 3 },
      { totalNetMinor: -501, totalVatMinor: -95, totalGrossMinor: -596 },
    ]);
    expect(totals).toEqual({ netMinor: 502, vatMinor: 96, grossMinor: 598 });
    expect(sumDocuments([])).toEqual({ netMinor: 0, vatMinor: 0, grossMinor: 0 });
  });
});

describe('issue run outcome', () => {
  const run = (over: Partial<{ failures: { organizationId: string; code: string; message: string }[]; invoicesIssued: number; creditNotesIssued: number; alreadyIssued: number }> = {}) => ({
    failures: [],
    invoicesIssued: 0,
    creditNotesIssued: 0,
    alreadyIssued: 0,
    ...over,
  });

  it('reports failures first, even if other documents were issued', () => {
    const r = run({ invoicesIssued: 3, failures: [{ organizationId: 'o', code: 'invoicing.vat_mismatch', message: 'x' }] });
    expect(runOutcome(r)).toBe('failed');
    expect(runOutcomeTone('failed')).toBe('warning');
    expect(runOutcomeKey('failed')).toBe('invoices.admin.outcome.failed');
  });

  it('distinguishes new documents, an idempotent re-run and an empty month', () => {
    expect(runOutcome(run({ invoicesIssued: 1 }))).toBe('issued');
    expect(runOutcome(run({ creditNotesIssued: 1, alreadyIssued: 4 }))).toBe('issued');
    expect(runOutcome(run({ alreadyIssued: 4 }))).toBe('unchanged');
    expect(runOutcome(run())).toBe('empty');
    expect(runOutcomeTone('issued')).toBe('success');
    expect(runOutcomeTone('unchanged')).toBe('info');
    expect(runOutcomeKey('unchanged')).toBe('invoices.admin.outcome.unchanged');
    expect(runOutcomeKey('empty')).toBe('invoices.admin.outcome.empty');
    expect(runOutcomeKey('issued')).toBe('invoices.admin.outcome.issued');
  });
});
