import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Receipt } from '../../core/api/models';
import { PosInvoiceComponent, invoiceFormFrom, invoiceRequestFrom } from './pos-invoice';

const customer = { id: 'c1', name: 'Erika Mustermann', company: null, street: 'Lindenweg 3', postalCode: '10405', city: 'Berlin', countryCode: 'DE', vatId: null };

describe('invoice form', () => {
  it('prefills from the receipt customer and requires name and full address', () => {
    const form = invoiceFormFrom({ customer } as unknown as Receipt);
    expect(form).toMatchObject({ name: 'Erika Mustermann', street: 'Lindenweg 3', city: 'Berlin', countryCode: 'DE' });
    expect(invoiceRequestFrom(form)).toEqual({
      ok: true,
      body: { name: 'Erika Mustermann', company: null, street: 'Lindenweg 3', postalCode: '10405', city: 'Berlin', countryCode: 'DE', vatId: null },
    });
    const empty = invoiceFormFrom({ customer: null } as unknown as Receipt);
    expect(invoiceRequestFrom({ ...empty, countryCode: 'Deutschland' })).toEqual({ ok: false, missing: ['name', 'street', 'postalCode', 'city', 'countryCode'] });
    expect(invoiceRequestFrom({ ...form, countryCode: 'at', vatId: ' ATU123 ' })).toMatchObject({ ok: true, body: { countryCode: 'AT', vatId: 'ATU123' } });
  });
});

describe('PosInvoiceComponent', () => {
  let http: HttpTestingController;
  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  it('issues the invoice with the prefilled customer and then shows its number', async () => {
    const fixture = TestBed.createComponent(PosInvoiceComponent);
    fixture.componentRef.setInput('receipt', { id: 'rc1', kind: 'Sale', customer } as unknown as Receipt);
    fixture.detectChanges();
    http.expectOne('/api/pos/sales/rc1/invoice').flush(null, { status: 204, statusText: 'No Content' });
    await fixture.whenStable();
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    (el.querySelector('[data-testid="pos-invoice-start"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect((el.querySelector('[data-testid="pos-invoice-name"]') as HTMLInputElement).value).toBe('Erika Mustermann');
    (el.querySelector('[data-testid="pos-invoice-submit"]') as HTMLButtonElement).click();
    const req = http.expectOne((r) => r.method === 'POST' && r.url === '/api/pos/sales/rc1/invoice');
    expect(req.request.body.customer.city).toBe('Berlin');
    req.flush({ id: 'i1', receiptId: 'rc1', number: 'RE-2026-000001', issueDate: '2026-10-18', document: { customer } });
    await fixture.whenStable();
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="pos-invoice-number"]')?.textContent).toContain('RE-2026-000001');
  });
});
