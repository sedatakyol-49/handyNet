import { CompanyProfile } from '../../core/api/models';
import {
  CompanyForm,
  cropRegion,
  fieldOfApiError,
  fitWithin,
  formFromProfile,
  formatIban,
  isValidBic,
  isValidIban,
  isValidVatId,
  legalChanges,
  logoFileProblem,
  requestFromForm,
  validateCompanyForm,
} from './company.logic';

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

const form = (patch: Partial<CompanyForm> = {}): CompanyForm => ({ ...formFromProfile(PROFILE), ...patch });

describe('company logic', () => {
  it('checks IBANs with the mod-97 checksum and the country length', () => {
    expect(isValidIban('DE89 3704 0044 0532 0130 00')).toBe(true);
    expect(isValidIban('de89370400440532013000')).toBe(true);
    expect(isValidIban('GB29NWBK60161331926819')).toBe(true);
    expect(isValidIban('DE89370400440532013001')).toBe(false); // check digits
    expect(isValidIban('DE8937040044053201300')).toBe(false); // 21 instead of 22
    expect(isValidIban('')).toBe(false);
  });

  it('checks BIC and VAT id formats', () => {
    expect(isValidBic('COBADEFFXXX')).toBe(true);
    expect(isValidBic('cobadeff')).toBe(true);
    expect(isValidBic('COBADE')).toBe(false);
    expect(isValidVatId('DE 123 456 789', 'DE')).toBe(true);
    expect(isValidVatId('DE12345678', 'DE')).toBe(false);
    expect(isValidVatId('ATU12345678', 'DE')).toBe(false);
    expect(isValidVatId('ATU12345678', 'AT')).toBe(true);
  });

  it('formats the IBAN in groups of four and sends it without spaces', () => {
    expect(formatIban('DE89370400440532013000')).toBe('DE89 3704 0044 0532 0130 00');
    expect(formFromProfile(PROFILE).iban).toBe('DE89 3704 0044 0532 0130 00');
    const request = requestFromForm(form({ iban: 'de89 3704 0044 0532 0130 00', bic: ' cobadeff ', paymentTermsDays: '30', website: '  ' }), 42);
    expect(request.iban).toBe('DE89370400440532013000');
    expect(request.bic).toBe('COBADEFF');
    expect(request.paymentTermsDays).toBe(30);
    expect(request.website).toBeNull();
    expect(request.version).toBe(42);
  });

  it('keeps line breaks of receipt texts but trims trailing spaces and blank edges', () => {
    const request = requestFromForm(form({ receiptFooter: '\r\nZeile 1  \r\n\r\nZeile 3\n\n' }), 1);
    expect(request.receiptFooter).toBe('Zeile 1\n\nZeile 3');
    expect(requestFromForm(form({ receiptHeader: '   ' }), 1).receiptHeader).toBeNull();
  });

  it('reports field errors like the API', () => {
    expect(validateCompanyForm(form())).toEqual({});
    const errors = validateCompanyForm(
      form({
        name: ' ',
        postalCode: '1011',
        vatId: 'DE1',
        iban: 'DE89 3704 0044 0532 0130 01',
        bic: 'COBA',
        paymentTermsDays: '400',
        website: 'www.shop.de',
        receiptHeader: Array.from({ length: 13 }, () => 'x').join('\n'),
      }),
    );
    expect(errors).toEqual({
      name: 'company.errors.required',
      postalCode: 'company.errors.postalCode',
      vatId: 'company.errors.vatId',
      iban: 'company.errors.iban',
      bic: 'company.errors.bic',
      paymentTermsDays: 'company.errors.paymentTerms',
      website: 'company.errors.website',
      receiptHeader: 'company.errors.receiptText',
    });
    expect(validateCompanyForm(form({ iban: '', bankName: 'Sparkasse' })).iban).toBe('company.errors.ibanRequired');
  });

  it('maps API field names and detects legal-identity changes', () => {
    expect(fieldOfApiError('Iban')).toBe('iban');
    expect(fieldOfApiError('ReceiptFooter')).toBe('receiptFooter');
    expect(fieldOfApiError('Unknown')).toBeNull();
    const original = form();
    expect(legalChanges(form({ name: 'Neu' }), original)).toEqual([]);
    expect(legalChanges(form({ legalName: 'Neu GmbH', vatId: 'de 123456789' }), original)).toEqual(['legalName']);
    expect(legalChanges(form({ vatId: 'DE987654321' }), original)).toEqual(['vatId']);
  });

  it('computes crop regions within the image for each aspect and zoom', () => {
    expect(cropRegion(800, 200, 'free', 1, 0, 0)).toEqual({ x: 0, y: 0, width: 800, height: 200 });
    expect(cropRegion(800, 200, 'square', 1, 0, 0)).toEqual({ x: 300, y: 0, width: 200, height: 200 });
    expect(cropRegion(800, 200, 'square', 1, -1, 0)).toEqual({ x: 0, y: 0, width: 200, height: 200 });
    expect(cropRegion(800, 200, 'square', 1, 1, 0)).toEqual({ x: 600, y: 0, width: 200, height: 200 });
    expect(cropRegion(300, 300, 'wide', 1, 0, 0)).toEqual({ x: 0, y: 100, width: 300, height: 100 });
    expect(cropRegion(800, 200, 'free', 2, 1, 1)).toEqual({ x: 400, y: 100, width: 400, height: 100 });
    expect(cropRegion(800, 200, 'free', 99, 5, -5).width).toBe(200); // zoom ≤ 4, pan clamped
    expect(fitWithin(4000, 1000, 1600)).toEqual({ width: 1600, height: 400 });
    expect(fitWithin(100, 50, 1600)).toEqual({ width: 100, height: 50 });
  });

  it('refuses other file types and files over 2 MB before cropping', () => {
    expect(logoFileProblem({ type: 'image/png', size: 1000 })).toBeNull();
    expect(logoFileProblem({ type: 'image/svg+xml', size: 1000 })).toBe('company.logo.errors.type');
    expect(logoFileProblem({ type: 'image/gif', size: 1000 })).toBe('company.logo.errors.type');
    expect(logoFileProblem({ type: 'image/jpeg', size: 2 * 1024 * 1024 + 1 })).toBe('company.logo.errors.size');
  });
});
