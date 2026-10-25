import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { CompanyProfile, ReceiptDocument } from '../../core/api/models';
import { ReceiptViewComponent } from '../pos/receipt-view';
import { CompanyPage } from './company.page';

const PROFILE: CompanyProfile = {
  id: 'org-1',
  name: 'Handy Shop',
  legalName: 'Handy Shop GmbH',
  street: 'Hauptstraße 1',
  postalCode: '10115',
  city: 'Berlin',
  countryCode: 'DE',
  vatId: 'DE123456789',
  taxNumber: null,
  contactEmail: 'info@shop.de',
  contactPhone: null,
  website: null,
  receiptHeader: 'Willkommen',
  receiptFooter: 'Mo–Sa 10–19 Uhr\nUmtausch 14 Tage',
  bankName: null,
  iban: 'DE89370400440532013000',
  bic: null,
  managingDirector: null,
  registerCourt: null,
  registerNumber: null,
  paymentTermsDays: 14,
  logo: null,
  status: 'Verified',
  version: 42,
  canEdit: true,
};

const settle = async (fixture: { whenStable(): Promise<unknown>; detectChanges(): void }) => {
  await fixture.whenStable();
  fixture.detectChanges();
};

function type(el: HTMLElement, testId: string, value: string): void {
  const input = el.querySelector(`[data-testid="${testId}"]`) as HTMLInputElement | HTMLTextAreaElement;
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

describe('company page', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  async function render(profile: CompanyProfile) {
    const fixture = TestBed.createComponent(CompanyPage);
    fixture.detectChanges();
    await fixture.whenStable();
    http.expectOne('/api/organization/profile').flush(profile);
    await settle(fixture);
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  it('lets the owner edit, previews receipt texts live and saves with the loaded version', async () => {
    const { fixture, el } = await render(PROFILE);
    expect((el.querySelector('[data-testid="company-name"]') as HTMLInputElement).value).toBe('Handy Shop');
    expect((el.querySelector('[data-testid="company-iban"]') as HTMLInputElement).value).toBe('DE89 3704 0044 0532 0130 00');
    expect(el.querySelector('[data-testid="company-read-only"]')).toBeNull();
    const save = el.querySelector('[data-testid="company-save"]') as HTMLButtonElement;
    expect(save.disabled).toBe(true); // nothing changed yet

    type(el, 'company-receiptFooter', 'Danke für Ihren Einkauf!\nRückgabe 30 Tage');
    await settle(fixture);
    const footer = [...el.querySelectorAll('[data-testid="company-preview-footer"]')].map((p) => p.textContent?.trim());
    expect(footer).toEqual(['Danke für Ihren Einkauf!', 'Rückgabe 30 Tage']);
    expect(save.disabled).toBe(false);

    save.click();
    await settle(fixture);
    const req = http.expectOne('/api/organization/profile');
    expect(req.request.method).toBe('PUT');
    expect(req.request.body.version).toBe(42);
    expect(req.request.body.receiptFooter).toBe('Danke für Ihren Einkauf!\nRückgabe 30 Tage');
    expect(req.request.body.iban).toBe('DE89370400440532013000');
    req.flush({ ...PROFILE, receiptFooter: 'Danke für Ihren Einkauf!\nRückgabe 30 Tage', version: 43 });
    await settle(fixture);
    expect(save.disabled).toBe(true);
  });

  it('blocks an invalid IBAN on the client and warns about a legal name change', async () => {
    const { fixture, el } = await render(PROFILE);
    type(el, 'company-legalName', 'Handy Shop Berlin UG');
    await settle(fixture);
    expect(el.querySelector('[data-testid="company-legal-notice"]')).not.toBeNull();

    type(el, 'company-iban', 'DE89 3704 0044 0532 0130 01');
    (el.querySelector('[data-testid="company-save"]') as HTMLButtonElement).click();
    await settle(fixture);
    http.expectNone('/api/organization/profile');
    expect(el.querySelector('#company-iban-error')?.textContent).toContain('IBAN');
    expect(el.querySelector('[data-testid="company-iban"]')?.getAttribute('aria-invalid')).toBe('true');
  });

  it('shows a concurrency conflict with a reload action', async () => {
    const { fixture, el } = await render(PROFILE);
    type(el, 'company-name', 'Anderer Name');
    await settle(fixture);
    (el.querySelector('[data-testid="company-save"]') as HTMLButtonElement).click();
    await settle(fixture);
    http.expectOne('/api/organization/profile').flush({ code: 'concurrency', title: 'x' }, { status: 409, statusText: 'Conflict' });
    await settle(fixture);
    expect(el.querySelector('[data-testid="company-conflict"]')).not.toBeNull();
  });

  it('is read-only for members without organization.manage', async () => {
    const { el } = await render({ ...PROFILE, canEdit: false });
    expect(el.querySelector('[data-testid="company-read-only"]')).not.toBeNull();
    expect((el.querySelector('[data-testid="company-name"]') as HTMLInputElement).closest('fieldset')?.disabled).toBe(true);
    expect(el.querySelector('[data-testid="company-save"]')).toBeNull();
    expect(el.querySelector('[data-testid="company-logo-file"]')).toBeNull();
  });

  it('shows the logo with an option to remove it', async () => {
    const { el } = await render({
      ...PROFILE,
      logo: { id: 'l1', screenUrl: '/api/logos/l1/screen', printUrl: '/api/logos/l1/print', width: 480, height: 120, uploadedAt: '2026-10-23T10:00:00Z' },
    });
    expect(el.querySelector('[data-testid="company-logo-image"]')?.getAttribute('src')).toBe('/api/logos/l1/screen');
    expect(el.querySelector('[data-testid="company-logo-remove"]')).not.toBeNull();
  });
});

describe('receipt view branding', () => {
  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
  });

  it('prints the logo, header and footer texts given for the receipt', async () => {
    const fixture = TestBed.createComponent(ReceiptViewComponent);
    const doc = {
      title: 'Kassenbon', receiptNumber: 7, referencedReceiptNumber: null, registerName: 'Kasse 1', issuedAt: '2026-10-23T10:00:00Z', currency: 'EUR',
      seller: { name: 'Handy Shop', legalName: 'Handy Shop GmbH', street: 'Hauptstraße 1', postalCode: '10115', city: 'Berlin', countryCode: 'DE', vatId: null },
      customer: null, lines: [], payments: [], vat: [], notes: [], totalGrossMinor: 0, changeMinor: 0, roundingMinor: 0,
      fiscal: { status: 'Signed', notice: null, isTest: false },
    } as unknown as ReceiptDocument;
    fixture.componentRef.setInput('doc', doc);
    fixture.componentRef.setInput('branding', { logoUrl: '/api/logos/l1/screen', receiptHeader: 'Willkommen', receiptFooter: 'Zeile A\nZeile B' });
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="receipt-logo"]')?.getAttribute('src')).toBe('/api/logos/l1/screen');
    expect(el.querySelector('[data-testid="receipt-header-text"]')?.textContent?.trim()).toBe('Willkommen');
    expect(el.querySelector('[data-testid="receipt-footer-text"]')?.textContent).toContain('Zeile B');

    fixture.componentRef.setInput('branding', null);
    await settle(fixture);
    expect(el.querySelector('[data-testid="receipt-logo"]')).toBeNull();
    expect(el.querySelector('[data-testid="receipt-footer-text"]')).toBeNull();
  });
});
