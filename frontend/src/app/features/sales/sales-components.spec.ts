import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { SalesInvoice } from '../../core/api/models';
import { PosContext } from '../pos/pos-context';
import { SalesDetailComponent } from './sales-detail';
import { SalesFormComponent } from './sales-form';
import { OFFICE_SALES_NAV, posSalesNav } from './sales-nav';

function issued(overrides: Partial<SalesInvoice> = {}): SalesInvoice {
  return {
    id: 'inv1', kind: 'Invoice', status: 'Issued', number: 'RE-2026-000002', customerId: 'c1',
    customer: { customerId: 'c1', name: 'Max Muster', company: 'Muster GmbH', street: 'Weg 1', postalCode: '20095', city: 'Hamburg', countryCode: 'DE', vatId: 'DE1' },
    invoiceDate: '2026-10-22', serviceDate: '2026-10-22', paymentTermDays: 14, dueDate: '2026-11-05', priceMode: 'Gross', reverseCharge: false,
    warehouseId: 'w1', quotationId: null, quotationNumber: null, originalInvoiceId: null, originalNumber: null, creditNoteId: null, creditNoteNumber: null,
    reason: null, notes: null,
    lines: [{ position: 1, productId: 'p1', stockUnitId: null, imei: null, description: 'Kabel', note: null, quantity: 4, unitPriceMinor: 25_00, discountMinor: 0, amountMinor: 100_00, vatRateBasisPoints: 1900, margin25a: false }],
    vat: [{ rateBasisPoints: 1900, netMinor: 84_03, vatMinor: 15_97, grossMinor: 100_00 }], marginGrossMinor: 0, totalNetMinor: 84_03, totalVatMinor: 15_97,
    totalGrossMinor: 100_00, paidMinor: 0, openMinor: 100_00, paymentState: 'Open', overdue: false, cancelled: false, payments: [], issuedAt: '2026-10-22T10:00:00Z',
    createdAt: '2026-10-22T09:00:00Z', createdBy: 'u1', createdByName: 'Owner', pdfUrl: '/api/pos/sales-invoices/inv1/pdf', warnings: [],
    ...overrides,
  };
}

describe('SalesDetailComponent', () => {
  let http: HttpTestingController;
  afterEach(() => http.verify());

  async function render(nav: 'pos' | 'office', invoice: SalesInvoice) {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), ...(nav === 'pos' ? [PosContext] : [])],
    });
    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(SalesDetailComponent);
    fixture.componentRef.setInput('kind', 'invoice');
    fixture.componentRef.setInput('id', invoice.id);
    fixture.componentRef.setInput('nav', nav === 'pos' ? posSalesNav('r1') : OFFICE_SALES_NAV);
    fixture.detectChanges();
    http.expectOne(`/api/pos/sales-invoices/${invoice.id}`).flush(invoice);
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  it('offers collecting at the till only in the POS app, and payments everywhere', async () => {
    const office = await render('office', issued());
    expect(office.el.querySelector('[data-testid="sales-collect"]')).toBeNull();
    expect(office.el.querySelector('[data-testid="sales-record-payment"]')).not.toBeNull();
    expect(office.el.querySelector('[data-testid="sales-detail-open"]')?.textContent).toContain('100,00');
    TestBed.resetTestingModule();
    const pos = await render('pos', issued());
    expect(pos.el.querySelector('[data-testid="sales-collect"]')).not.toBeNull();
  });

  it('records a bank transfer with the open amount prefilled', async () => {
    const { fixture, el } = await render('office', issued());
    (el.querySelector('[data-testid="sales-record-payment"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect((el.querySelector('[data-testid="sales-pay-amount"]') as HTMLInputElement).value).toBe('100,00');
    (el.querySelector('[data-testid="sales-pay-save"]') as HTMLButtonElement).click();
    const req = http.expectOne((r) => r.method === 'POST' && r.url === '/api/pos/sales-invoices/inv1/payments');
    expect(req.request.body).toMatchObject({ method: 'BankTransfer', amountMinor: 100_00 });
    req.flush(issued({ paidMinor: 100_00, openMinor: 0, paymentState: 'Paid' }));
    await fixture.whenStable();
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="sales-record-payment"]')).toBeNull();
  });

  it('asks for a manager approval in the POS when the cashier lacks pos.refund', async () => {
    const { fixture, el } = await render('pos', issued());
    const ctx = fixture.debugElement.injector.get(PosContext);
    (el.querySelector('[data-testid="sales-credit-note"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(ctx.approvalRequest()).toEqual(['pos.refund']);
    expect((el.querySelector('[data-testid="sales-credit-dialog"]') as HTMLDialogElement).open).toBeFalsy();
  });

  it('shows draft actions (issue, discard) but no payments for a draft', async () => {
    const { el } = await render('office', issued({ status: 'Draft', number: null, paymentState: null, openMinor: 0, issuedAt: null }));
    expect(el.querySelector('[data-testid="sales-issue"]')).not.toBeNull();
    expect(el.querySelector('[data-testid="sales-discard"]')).not.toBeNull();
    expect(el.querySelector('[data-testid="sales-record-payment"]')).toBeNull();
    expect(el.querySelector('[data-testid="sales-credit-note"]')).toBeNull();
  });
});

describe('SalesFormComponent', () => {
  it('previews totals in net mode and refuses sub-cent prices on save', async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(SalesFormComponent);
    fixture.componentRef.setInput('kind', 'invoice');
    fixture.componentRef.setInput('nav', OFFICE_SALES_NAV);
    fixture.detectChanges();
    http.expectOne('/api/pos/sales-warehouses').flush([{ id: 'w1', code: 'MAIN', name: 'Hauptlager', isDefault: true }]);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    (el.querySelector('[data-testid="sales-add-free"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    const type = (testId: string, value: string) => {
      const input = el.querySelector(`[data-testid="${testId}"]`) as HTMLInputElement;
      input.value = value;
      input.dispatchEvent(new Event('input'));
      fixture.detectChanges();
    };
    (el.querySelector('[data-testid="sales-mode-Net"]') as HTMLButtonElement).click();
    type('line-description', 'Montage');
    type('line-quantity', '2');
    type('line-price', '10,00');
    expect(el.querySelector('[data-testid="sales-total"]')?.textContent).toContain('23,80');

    type('line-price', '10,005');
    (el.querySelector('[data-testid="sales-form-save"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="sales-form-error"]')).not.toBeNull();
    http.verify(); // nothing was sent
  });
});
