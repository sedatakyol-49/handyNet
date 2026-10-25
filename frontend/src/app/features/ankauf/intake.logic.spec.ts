import { Intake } from '../../core/api/models';
import {
  SellerForm,
  ageOn,
  applyMrz,
  applySuggestion,
  daysUntilExpiry,
  defaultPayoutRegister,
  emptySellerForm,
  matchesSignature,
  signatureState,
  validatePayout,
  defaultPaidMinor,
  inspectFormFrom,
  intakeActions,
  intakeByQuote,
  isQuoteUsable,
  latestAdultBirthDate,
  offerDifferenceMinor,
  parseIsoDate,
  stepStates,
  today,
  validateInspectForm,
  validatePaidAmount,
  validateReason,
  validateReceiveForm,
} from './intake.logic';

const d = (s: string) => parseIsoDate(s)!;

describe('dates and age', () => {
  it('parses only real calendar dates', () => {
    expect(parseIsoDate('2000-02-29')).toEqual({ y: 2000, m: 2, d: 29 });
    expect(parseIsoDate('2001-02-29')).toBeNull();
    expect(parseIsoDate('2001-13-01')).toBeNull();
    expect(parseIsoDate('01.02.2001')).toBeNull();
    expect(parseIsoDate('')).toBeNull();
    expect(parseIsoDate(null)).toBeNull();
  });

  it('counts completed years (birthday not yet reached → one less)', () => {
    expect(ageOn(d('2008-10-15'), d('2026-10-15'))).toBe(18);
    expect(ageOn(d('2008-10-16'), d('2026-10-15'))).toBe(17);
    expect(ageOn(d('2008-11-01'), d('2026-10-15'))).toBe(17);
    expect(ageOn(d('1990-01-01'), d('2026-10-15'))).toBe(36);
  });

  it('treats 29 February like the API (18 only from 1 March in non-leap years)', () => {
    expect(ageOn(d('2008-02-29'), d('2026-02-28'))).toBe(17);
    expect(ageOn(d('2008-02-29'), d('2026-03-01'))).toBe(18);
  });

  it('computes the latest adult birth date for the date input max', () => {
    expect(latestAdultBirthDate(d('2026-10-15'))).toBe('2008-10-15');
    expect(latestAdultBirthDate(d('2028-02-29'))).toBe('2010-02-28');
  });

  it('uses the local calendar day', () => {
    expect(today(new Date(2026, 9, 15, 23, 59))).toEqual({ y: 2026, m: 10, d: 15 });
  });
});

const seller = (over: Partial<SellerForm> = {}): SellerForm => ({
  firstName: ' Max ',
  lastName: 'Muster',
  street: 'Weg 1',
  postalCode: '10115',
  city: 'Berlin',
  birthDate: '1990-01-01',
  idDocumentType: 'IdCard',
  idDocumentNumber: 'L01X00T47',
  idExpiryDate: '2030-01-31',
  nationality: '',
  idIssuingAuthority: '',
  idIssuingCountry: '',
  country: '',
  imei: '49-015420-323751-8',
  ...over,
});
const ON = d('2026-10-15');

describe('validateReceiveForm', () => {
  it('builds the request with trimmed values and a normalized IMEI', () => {
    const v = validateReceiveForm('q1', null, seller(), ON);
    expect(v.errors).toEqual({});
    expect(v.request).toEqual({
      quoteId: 'q1',
      imei: '490154203237518',
      seller: {
        firstName: 'Max',
        lastName: 'Muster',
        street: 'Weg 1',
        postalCode: '10115',
        city: 'Berlin',
        birthDate: '1990-01-01',
        idDocumentType: 'IdCard',
        idDocumentNumber: 'L01X00T47',
        idExpiryDate: '2030-01-31',
        nationality: null,
        idIssuingAuthority: null,
        idIssuingCountry: null,
        country: null,
      },
    });
  });

  it('requires every field', () => {
    const v = validateReceiveForm(
      'q1',
      null,
      { ...emptySellerForm(), lastName: ' ' },
      ON,
    );
    expect(v.request).toBeNull();
    expect(Object.keys(v.errors).sort()).toEqual(
      ['birthDate', 'city', 'firstName', 'idDocumentNumber', 'idDocumentType', 'idExpiryDate', 'imei', 'lastName', 'postalCode', 'street'].sort(),
    );
    expect(v.errors.idDocumentType).toBe('ankauf.intake.validation.idDocumentType');
  });

  it('rejects minors (age computed from the birth date) and accepts the 18th birthday', () => {
    expect(validateReceiveForm('q1', null, seller({ birthDate: '2010-05-01' }), ON).errors.birthDate).toBe('ankauf.intake.validation.minor');
    expect(validateReceiveForm('q1', null, seller({ birthDate: '2008-10-16' }), ON).errors.birthDate).toBe('ankauf.intake.validation.minor');
    expect(validateReceiveForm('q1', null, seller({ birthDate: '2008-10-15' }), ON).errors.birthDate).toBeUndefined();
  });

  it('rejects invalid and future birth dates', () => {
    expect(validateReceiveForm('q1', null, seller({ birthDate: '2027-01-01' }), ON).errors.birthDate).toBe('ankauf.intake.validation.birthDate');
    expect(validateReceiveForm('q1', null, seller({ birthDate: '1990-02-30' }), ON).errors.birthDate).toBe('ankauf.intake.validation.birthDate');
  });

  it('checks the IMEI (Luhn) and that it equals the quote IMEI', () => {
    expect(validateReceiveForm('q1', null, seller({ imei: '490154203237519' }), ON).errors.imei).toBe('ankauf.validation.imei');
    expect(validateReceiveForm('q1', '352099001761481', seller(), ON).errors.imei).toBe('ankauf.intake.validation.imeiMismatch');
    expect(validateReceiveForm('q1', '490154203237518', seller(), ON).errors.imei).toBeUndefined();
  });

  it('enforces the API lengths', () => {
    expect(validateReceiveForm('q1', null, seller({ postalCode: '12345678901' }), ON).errors.postalCode).toBe('ankauf.intake.validation.tooLong');
    expect(validateReceiveForm('q1', null, seller({ idDocumentNumber: 'X'.repeat(31) }), ON).errors.idDocumentNumber).toBe(
      'ankauf.intake.validation.tooLong',
    );
  });
});

describe('isQuoteUsable', () => {
  it('is false once the quote has expired', () => {
    const now = new Date('2026-10-15T12:00:00Z');
    expect(isQuoteUsable({ validUntil: '2026-10-15T12:00:01Z' }, now)).toBe(true);
    expect(isQuoteUsable({ validUntil: '2026-10-15T12:00:00Z' }, now)).toBe(false);
  });
});

describe('inspect form', () => {
  it('validates condition, battery and builds the request', () => {
    expect(validateInspectForm({ condition: null, defects: {}, batteryHealth: '101', notes: '' }).errors).toEqual({
      condition: 'ankauf.validation.condition',
      battery: 'ankauf.validation.battery',
    });
    expect(validateInspectForm({ condition: 'Gut', defects: { ScreenDamage: true, CameraDefect: false }, batteryHealth: ' 79 ', notes: ' Kratzer ' }).request).toEqual({
      condition: 'Gut',
      defects: ['ScreenDamage'],
      batteryHealthPercent: 79,
      notes: 'Kratzer',
    });
    expect(validateInspectForm({ condition: 'Gut', defects: {}, batteryHealth: '', notes: '  ' }).request?.notes).toBeNull();
  });

  it('prefills from the last inspection, else from the quote', () => {
    const quote = { condition: 'Neu' as const, defects: ['HousingDamage' as const], batteryHealthPercent: 90 };
    const fresh = { inspectedCondition: null, inspectedDefects: [], inspectedBatteryHealthPercent: null, inspectionNotes: null };
    expect(inspectFormFrom(fresh, quote)).toEqual({ condition: 'Neu', defects: { HousingDamage: true }, batteryHealth: '90', notes: '' });
    expect(inspectFormFrom(fresh, null)).toEqual({ condition: null, defects: {}, batteryHealth: '', notes: '' });
    expect(
      inspectFormFrom({ inspectedCondition: 'Akzeptabel', inspectedDefects: ['ScreenDamage'], inspectedBatteryHealthPercent: null, inspectionNotes: 'x' }, quote),
    ).toEqual({ condition: 'Akzeptabel', defects: { ScreenDamage: true }, batteryHealth: '', notes: 'x' });
  });

  it('computes the offer difference in cents', () => {
    expect(offerDifferenceMinor(30000, 14400)).toBe(-15600);
    expect(offerDifferenceMinor(30000, 30000)).toBe(0);
    expect(offerDifferenceMinor(30000, null)).toBeNull();
  });
});

describe('paid amount', () => {
  it('must be a positive amount not above the inspected offer', () => {
    expect(validatePaidAmount('', 14400)).toEqual({ minor: null, error: 'ankauf.intake.validation.amountRequired' });
    expect(validatePaidAmount('abc', 14400).error).toBe('ankauf.intake.validation.amountInvalid');
    expect(validatePaidAmount('1,234', 14400).error).toBe('ankauf.intake.validation.amountInvalid');
    expect(validatePaidAmount('0', 14400).error).toBe('ankauf.intake.validation.amountPositive');
    expect(validatePaidAmount('-5', 14400).error).toBe('ankauf.intake.validation.amountPositive');
    expect(validatePaidAmount('144,01', 14400).error).toBe('ankauf.intake.validation.amountAboveOffer');
    expect(validatePaidAmount('144,00', 14400)).toEqual({ minor: 14400, error: null });
    expect(validatePaidAmount('0,01', 14400)).toEqual({ minor: 1, error: null });
    expect(validatePaidAmount('50.5', 14400)).toEqual({ minor: 5050, error: null });
    expect(validatePaidAmount('10', null).error).toBe('ankauf.intake.validation.notInspected');
  });

  it('prefills with the inspected max. consumer price (capped at the offer, empty when 0)', () => {
    expect(defaultPaidMinor({ inspectedMaxConsumerPriceMinor: 12240, inspectedOfferMinor: 14400 })).toBe(12240);
    expect(defaultPaidMinor({ inspectedMaxConsumerPriceMinor: 20000, inspectedOfferMinor: 14400 })).toBe(14400);
    expect(defaultPaidMinor({ inspectedMaxConsumerPriceMinor: 0, inspectedOfferMinor: 0 })).toBeNull();
    expect(defaultPaidMinor({ inspectedMaxConsumerPriceMinor: null, inspectedOfferMinor: null })).toBeNull();
  });
});

describe('steps and actions by status', () => {
  it('derives the step states', () => {
    expect(stepStates(null)).toEqual({ receive: 'current', inspect: 'todo', sign: 'todo', complete: 'todo' });
    expect(stepStates({ status: 'Received', inspectedOfferMinor: null })).toEqual({ receive: 'done', inspect: 'current', sign: 'todo', complete: 'todo' });
    expect(stepStates({ status: 'Inspected', inspectedOfferMinor: 100 })).toEqual({ receive: 'done', inspect: 'done', sign: 'current', complete: 'todo' });
    expect(stepStates({ status: 'Inspected', inspectedOfferMinor: 100, signature: { valid: false } })).toMatchObject({ sign: 'current' });
    expect(stepStates({ status: 'Inspected', inspectedOfferMinor: 100, signature: { valid: true } })).toMatchObject({ sign: 'done', complete: 'current' });
    expect(stepStates({ status: 'Inspected', inspectedOfferMinor: 100, signatureRequired: false })).toMatchObject({ sign: 'skipped', complete: 'current' });
    expect(stepStates({ status: 'Completed', inspectedOfferMinor: 100, signature: { valid: true } })).toEqual({ receive: 'done', inspect: 'done', sign: 'done', complete: 'done' });
    expect(stepStates({ status: 'Rejected', inspectedOfferMinor: null })).toEqual({ receive: 'done', inspect: 'skipped', sign: 'skipped', complete: 'skipped' });
    expect(stepStates({ status: 'Rejected', inspectedOfferMinor: 100 })).toEqual({ receive: 'done', inspect: 'done', sign: 'skipped', complete: 'skipped' });
  });

  it('allows actions like the API state machine', () => {
    expect(intakeActions('Received')).toEqual({ inspect: true, sign: false, complete: false, reject: true });
    expect(intakeActions('Inspected')).toEqual({ inspect: true, sign: true, complete: true, reject: true });
    expect(intakeActions('Completed')).toEqual({ inspect: false, sign: false, complete: false, reject: false });
    expect(intakeActions('Rejected')).toEqual({ inspect: false, sign: false, complete: false, reject: false });
  });

  it('requires a rejection reason', () => {
    expect(validateReason('  ')).toBe('ankauf.intake.validation.reasonRequired');
    expect(validateReason('x'.repeat(1001))).toBe('ankauf.intake.validation.tooLong');
    expect(validateReason('Gerät gesperrt')).toBeNull();
  });

  it('maps quotes to their non-rejected intake', () => {
    const i = (id: string, quoteId: string, status: Intake['status']) => ({ id, quoteId, status }) as Intake;
    const map = intakeByQuote([i('a', 'q1', 'Rejected'), i('b', 'q1', 'Received'), i('c', 'q2', 'Completed')]);
    expect(map.get('q1')?.id).toBe('b');
    expect(map.get('q2')?.id).toBe('c');
    expect(map.has('q3')).toBe(false);
  });
});

describe('seller capture', () => {
  it('refuses expired ID documents and checks country codes', () => {
    expect(validateReceiveForm('q1', null, seller({ idExpiryDate: '2026-10-14' }), ON).errors.idExpiryDate).toBe('ankauf.intake.validation.idExpired');
    expect(validateReceiveForm('q1', null, seller({ idExpiryDate: '2026-10-15' }), ON).errors.idExpiryDate).toBeUndefined();
    expect(validateReceiveForm('q1', null, seller({ nationality: 'D1' }), ON).errors.nationality).toBe('ankauf.intake.validation.countryCode');
    expect(validateReceiveForm('q1', null, seller({ nationality: 'd<<', country: 'de' }), ON).request?.seller).toMatchObject({ nationality: 'D', country: null });
    expect(validateReceiveForm('q1', null, seller(), ON, { mrz: 'X', checkInId: 'c1' }).request).toMatchObject({ mrz: 'X', checkInId: 'c1' });
    expect(daysUntilExpiry('2026-10-20', ON)).toBe(5);
    expect(daysUntilExpiry('', ON)).toBeNull();
  });

  it('fills the form from the MRZ and keeps the address', () => {
    const f = applyMrz(seller({ street: 'Weg 9' }), {
      format: 'TD3', documentCode: 'P', documentType: 'Passport', issuingState: 'D', documentNumber: 'C01X00T47', surname: 'MUSTERMANN',
      givenNames: 'ERIKA', nationality: 'D', birthDate: '1964-08-12', sex: 'F', expiryDate: '2027-02-28',
    });
    expect(f).toMatchObject({ firstName: 'Erika', lastName: 'Mustermann', street: 'Weg 9', idDocumentType: 'Passport', idDocumentNumber: 'C01X00T47', idExpiryDate: '2027-02-28' });
  });

  it('reuses a returning seller without the old ID document', () => {
    const f = applySuggestion(seller(), {
      latestIntakeId: 'i', firstName: 'Ali', lastName: 'Kaya', birthDate: '1980-01-01', street: 'A 1', postalCode: '10115', city: 'Berlin', country: null,
      nationality: 'TUR', lastIntakeAt: '', intakeCount: 2,
    });
    expect(f).toMatchObject({ firstName: 'Ali', nationality: 'TUR', idDocumentNumber: '', idExpiryDate: '', idDocumentType: null });
  });
});

describe('signature and payout', () => {
  const sig = (valid: boolean) =>
    ({ signature: { id: 's', purpose: 'Purchase', source: 'Counter', signedAt: '', paidMinor: 200_00, paymentMethod: 'Cash', valid, hasImage: true, bindingHash: 'h', declarationVersion: 'v' } }) as Pick<Intake, 'signature'>;

  it('knows whether the signature still matches', () => {
    expect(signatureState({ signature: null })).toBe('none');
    expect(signatureState(sig(false))).toBe('stale');
    expect(matchesSignature(sig(true), 200_00, 'Cash')).toBe(true);
    expect(matchesSignature(sig(true), 199_99, 'Cash')).toBe(false);
    expect(matchesSignature(sig(true), 200_00, 'BankTransfer')).toBe(false);
  });

  it('needs an open till with enough cash for cash purchases', () => {
    const regs = [
      { id: 'a', name: 'A', openSessionId: 's', expectedCashMinor: 300_00 },
      { id: 'b', name: 'B', openSessionId: null, expectedCashMinor: null },
    ];
    expect(validatePayout('BankTransfer', true, null, regs, 100)).toBeNull();
    expect(validatePayout('Cash', false, null, regs, 100)).toBeNull();
    expect(validatePayout('Cash', true, null, regs, 100)).toBe('ankauf.pay.registerRequired');
    expect(validatePayout('Cash', true, 'b', regs, 100)).toBe('ankauf.pay.noOpenSession');
    expect(validatePayout('Cash', true, 'a', regs, 300_01)).toBe('ankauf.pay.notEnoughCash');
    expect(validatePayout('Cash', true, 'a', regs, 300_00)).toBeNull();
    expect(defaultPayoutRegister(regs, null)).toBe('a');
    expect(defaultPayoutRegister(regs, 'b')).toBe('b');
  });
});
