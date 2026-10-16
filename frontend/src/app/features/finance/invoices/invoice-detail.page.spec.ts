import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { toApiProblem } from '../../../core/api/api-problem';
import { AdminInvoicesApi, InvoicesApi } from '../../../core/api/handynet-api';
import { Invoice } from '../../../core/api/models';
import { InvoiceDetailPage } from './invoice-detail.page';

/** Shape of `GET /api/invoices/{id}` (InvoiceDto) for a correction of a September invoice. */
const CORRECTION: Invoice = {
  id: 'c0000000-0000-0000-0000-000000000002',
  kind: 'CreditNote',
  number: 'HN-RK-2026-000001',
  issueDate: '2026-11-01',
  issuedAt: '2026-11-01T00:05:00+00:00',
  billingMonth: '2026-10',
  servicePeriodStart: '2026-10-01',
  servicePeriodEnd: '2026-10-31',
  vatRateBasisPoints: 1900,
  totalNetMinor: -500,
  totalVatMinor: -95,
  totalGrossMinor: -595,
  currency: 'EUR',
  referencedInvoiceId: 'i0000000-0000-0000-0000-000000000001',
  referencedInvoiceNumber: 'HN-2026-000007',
  issuer: {
    name: 'PLATZHALTER HandyNet',
    legalName: 'PLATZHALTER HandyNet GmbH',
    street: 'PLATZHALTER Straße 1',
    postalCode: '00000',
    city: 'PLATZHALTER Stadt',
    countryCode: 'DE',
    vatId: 'PLATZHALTER-USt-IdNr',
    taxNumber: 'PLATZHALTER-Steuernummer',
  },
  recipient: {
    name: 'Großhandel Nord',
    legalName: 'Großhandel Nord GmbH',
    street: 'Hafenstraße 5',
    postalCode: '20457',
    city: 'Hamburg',
    countryCode: 'DE',
    vatId: 'DE123456789',
    taxNumber: null,
  },
  lines: [
    {
      lineNumber: 1,
      orderId: 'o0000000-0000-0000-0000-000000000001',
      orderNumber: 'B2B-001042',
      paymentId: 'p0000000-0000-0000-0000-000000000001',
      paidAt: '2026-09-12T08:30:00+00:00',
      netMinor: -500,
      vatMinor: -95,
      grossMinor: -595,
      creditedInvoiceId: 'i0000000-0000-0000-0000-000000000001',
    },
  ],
};

describe('InvoiceDetailPage', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  async function render(invoice: Invoice) {
    const fixture = TestBed.createComponent(InvoiceDetailPage);
    fixture.componentRef.setInput('id', invoice.id);
    fixture.detectChanges();
    await fixture.whenStable();
    http.expectOne(`/api/invoices/${invoice.id}`).flush(invoice);
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows a correction exactly as issued: negative amounts, corrected invoice, sample warning', async () => {
    const el = await render(CORRECTION);
    const text = (id: string) => el.querySelector(`[data-testid="${id}"]`)?.textContent?.replace(/\s+/g, ' ').trim();

    expect(text('invoice-number')).toBe('HN-RK-2026-000001');
    expect(el.querySelector('h1')?.textContent).toContain('Rechnungskorrektur HN-RK-2026-000001');
    expect(text('invoice-referenced')).toBe('HN-2026-000007');
    expect(el.querySelector('[data-testid="invoice-referenced"]')?.getAttribute('href')).toBe('/app/finanzen/rechnungen/i0000000-0000-0000-0000-000000000001');
    expect(text('invoice-header')).toContain('01.11.2026');
    expect(text('invoice-header')).toContain('Oktober 2026');
    expect(text('invoice-header')).toContain('01.10.2026 bis 31.10.2026');
    expect(text('invoice-header')).toContain('19 %');
    expect(text('invoice-sample')).toContain('keine gültige Rechnung');
    expect(text('invoice-recipient')).toContain('Großhandel Nord GmbH');
    expect(text('invoice-recipient')).toContain('20457 Hamburg');
    expect(text('invoice-recipient')).toContain('DE123456789');

    const row = el.querySelector('[data-testid="invoice-lines"] tbody tr[data-order-number="B2B-001042"]');
    // Intl separates amount and currency with a no-break space; `\s` covers it.
    const rowText = row?.textContent?.replace(/\s+/g, ' ') ?? '';
    expect(rowText).toContain('-5,00 €');
    expect(rowText).toContain('-0,95 €');
    expect(rowText).toContain('-5,95 €');
    expect(row?.querySelector('a')?.getAttribute('href')).toBe('/app/bestellungen/o0000000-0000-0000-0000-000000000001');
    expect(text('invoice-total-net')).toBe('-5,00 €');
    expect(text('invoice-total-vat')).toBe('-0,95 €');
    expect(text('invoice-total-gross')).toBe('-5,95 €');
    expect(el.textContent).toContain('storniert die folgenden Positionen der Rechnung HN-2026-000007');
  });

  it('shows no sample warning for configured issuer data', async () => {
    const el = await render({
      ...CORRECTION,
      kind: 'Invoice',
      number: 'HN-2026-000008',
      referencedInvoiceId: null,
      referencedInvoiceNumber: null,
      issuer: { ...CORRECTION.recipient, name: 'HandyNet', legalName: 'HandyNet GmbH' },
    });
    expect(el.querySelector('[data-testid="invoice-sample"]')).toBeNull();
    expect(el.querySelector('[data-testid="invoice-referenced"]')).toBeNull();
    expect(el.querySelector('h1')?.textContent).toContain('Rechnung HN-2026-000008');
  });
});

describe('invoice API clients', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('pages the seller list and posts the admin month', async () => {
    const list = firstValueFrom(TestBed.inject(InvoicesApi).list(2, 25));
    const req = http.expectOne((r) => r.url === '/api/invoices');
    expect(req.request.params.get('page')).toBe('2');
    expect(req.request.params.get('pageSize')).toBe('25');
    req.flush({ items: [], page: 2, pageSize: 25, totalCount: 0 });
    expect((await list).page).toBe(2);

    const admin = TestBed.inject(AdminInvoicesApi);
    const issue = firstValueFrom(admin.issue('2026-09'));
    const post = http.expectOne('/api/admin/invoices/issue');
    expect(post.request.method).toBe('POST');
    expect(post.request.body).toEqual({ month: '2026-09' });
    post.flush({ month: '2026-09', sellersChecked: 0, invoicesIssued: 0, creditNotesIssued: 0, alreadyIssued: 0, documents: [], failures: [] });
    expect((await issue).month).toBe('2026-09');

    const month = firstValueFrom(admin.forMonth('2026-09'));
    http.expectOne((r) => r.url === '/api/admin/invoices' && r.params.get('month') === '2026-09').flush([]);
    expect(await month).toEqual([]);
  });

  it('turns a problem body of a failed PDF download into an ApiProblem with code', async () => {
    const pdf = firstValueFrom(TestBed.inject(InvoicesApi).pdf('x'));
    const body = new Blob([JSON.stringify({ title: 'Invoice not found.', status: 404, code: 'invoicing.not_found' })], { type: 'application/problem+json' });
    http.expectOne('/api/invoices/x/pdf').flush(body, { status: 404, statusText: 'Not Found' });
    const problem = await pdf.then(
      () => null,
      (e: unknown) => toApiProblem(e),
    );
    expect(problem?.status).toBe(404);
    expect(problem?.code).toBe('invoicing.not_found');
  });
});
