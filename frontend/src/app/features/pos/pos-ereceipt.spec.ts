import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Receipt } from '../../core/api/models';
import { CompanyBrandingStore } from '../../core/company/company-branding.store';
import { PosEReceiptComponent, looksLikeEmail, publicReceiptUrl } from './pos-ereceipt';

describe('e-receipt helpers', () => {
  it('builds the public receipt URL from the web origin and the API path', () => {
    expect(publicReceiptUrl('https://shop.example/', '/beleg/abc')).toBe('https://shop.example/beleg/abc');
    expect(publicReceiptUrl('https://shop.example', 'beleg/abc')).toBe('https://shop.example/beleg/abc');
  });

  it('pre-checks email addresses', () => {
    expect(looksLikeEmail('kunde@example.de')).toBe(true);
    expect(looksLikeEmail(' kunde@example.de ')).toBe(true);
    expect(looksLikeEmail('kein-at')).toBe(false);
    expect(looksLikeEmail('Name <a@b.de>')).toBe(false);
  });
});

describe('PosEReceiptComponent', () => {
  let http: HttpTestingController;
  const receipt = { id: 'rc1', customerId: 'c1' } as unknown as Receipt;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('sends to the customer (email null) and shows a QR for a new public link', async () => {
    const fixture = TestBed.createComponent(PosEReceiptComponent);
    fixture.componentRef.setInput('receipt', receipt);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;

    (el.querySelector('[data-testid="pos-ereceipt-send-customer"]') as HTMLButtonElement).click();
    const mail = http.expectOne('/api/pos/sales/rc1/email');
    expect(mail.request.body).toEqual({ email: null, language: 'de' });
    mail.flush({ id: 'm1', receiptId: 'rc1', recipientMasked: 'k***@example.de', requestedAt: '2026-10-18T10:00:00Z' });
    await fixture.whenStable();
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="pos-ereceipt-sent"]')?.textContent).toContain('k***@example.de');

    (el.querySelector('[data-testid="pos-ereceipt-show-qr"]') as HTMLButtonElement).click();
    http.expectOne('/api/pos/sales/rc1/public-link').flush({ token: 't', path: '/beleg/t', expiresAt: '2027-01-16T10:00:00Z' });
    await fixture.whenStable();
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="pos-ereceipt-url"]')?.textContent).toContain('/beleg/t');
    expect(el.querySelector('[data-testid="qr-code"]')).not.toBeNull();
  });

  it('preselects the email language of the company and sends the chosen one', () => {
    (TestBed.inject(CompanyBrandingStore) as unknown as { profile: () => unknown }).profile = () => ({ emailLanguage: 'tr' });
    const fixture = TestBed.createComponent(PosEReceiptComponent);
    fixture.componentRef.setInput('receipt', receipt);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const select = el.querySelector('[data-testid="pos-ereceipt-language"]') as HTMLSelectElement;
    expect(select.options.length).toBe(7);
    expect(select.options[select.selectedIndex].value).toBe('tr');

    select.value = 'ru';
    select.dispatchEvent(new Event('change'));
    (el.querySelector('[data-testid="pos-ereceipt-send-customer"]') as HTMLButtonElement).click();
    const mail = http.expectOne('/api/pos/sales/rc1/email');
    expect(mail.request.body).toEqual({ email: null, language: 'ru' });
    mail.flush({ id: 'm1', receiptId: 'rc1', recipientMasked: 'k***@example.de', requestedAt: '2026-10-18T10:00:00Z' });
  });

  it('refuses an invalid address without calling the API', () => {
    const fixture = TestBed.createComponent(PosEReceiptComponent);
    fixture.componentRef.setInput('receipt', receipt);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const input = el.querySelector('[data-testid="pos-ereceipt-email"]') as HTMLInputElement;
    input.value = 'kein-at';
    input.dispatchEvent(new Event('input'));
    (el.querySelector('[data-testid="pos-ereceipt-send"]') as HTMLButtonElement).click();
    http.expectNone('/api/pos/sales/rc1/email');
  });
});
