import { ApiProblem } from '../../core/api/api-problem';
import { QuoteForm, ankaufProblemText, isValidImei, modelLabel, normalizeImei, validateQuoteForm } from './ankauf.logic';

describe('IMEI (Luhn)', () => {
  it('accepts valid IMEIs, ignoring separators', () => {
    expect(isValidImei('490154203237518')).toBe(true);
    expect(isValidImei('35-209900-176148-1')).toBe(true);
    expect(isValidImei('35 209900 176148 1')).toBe(true);
    expect(isValidImei('353918/05/840234/2')).toBe(false); // wrong check digit
    expect(isValidImei('353918058402343')).toBe(false);
  });

  it('rejects wrong check digits, lengths and characters', () => {
    expect(isValidImei('490154203237519')).toBe(false);
    expect(isValidImei('49015420323751')).toBe(false);
    expect(isValidImei('4901542032375180')).toBe(false);
    expect(isValidImei('49015420323751a')).toBe(false);
    expect(isValidImei('')).toBe(false);
    expect(isValidImei(null)).toBe(false);
  });

  it('normalizes separators', () => {
    expect(normalizeImei(' 35-209900/176148 1 ')).toBe('352099001761481');
  });
});

const form = (over: Partial<QuoteForm> = {}): QuoteForm => ({
  modelKey: 'APPLE|IPHONE13|128GB',
  condition: 'Neu',
  defects: {},
  batteryHealth: '',
  consumerPrice: '',
  imei: '',
  ...over,
});

describe('validateQuoteForm', () => {
  it('builds the request with only checked defects and optional fields omitted', () => {
    const v = validateQuoteForm(form({ defects: { HousingDamage: true, ScreenDamage: false } }));
    expect(v.errors).toEqual({});
    expect(v.request).toEqual({
      modelKey: 'APPLE|IPHONE13|128GB',
      condition: 'Neu',
      defects: ['HousingDamage'],
      batteryHealthPercent: null,
      consumerPrice: null,
      imei: null,
    });
  });

  it('converts optional values (decimal euros without drift, normalized IMEI)', () => {
    const v = validateQuoteForm(form({ batteryHealth: '79', consumerPrice: '1.249,90', imei: '35-209900-176148-1' }));
    expect(v.request?.batteryHealthPercent).toBe(79);
    expect(v.request?.consumerPrice).toBe(1249.9);
    expect(v.request?.imei).toBe('352099001761481');
    expect(validateQuoteForm(form({ consumerPrice: '0,29' })).request?.consumerPrice).toBe(0.29);
  });

  it('reports field errors', () => {
    const v = validateQuoteForm(form({ modelKey: null, condition: null, batteryHealth: '101', consumerPrice: '12,345', imei: '123' }));
    expect(v.request).toBeNull();
    expect(v.errors).toEqual({
      model: 'ankauf.validation.model',
      condition: 'ankauf.validation.condition',
      battery: 'ankauf.validation.battery',
      consumerPrice: 'ankauf.validation.consumerPrice',
      imei: 'ankauf.validation.imei',
    });
    expect(validateQuoteForm(form({ batteryHealth: '-1' })).errors.battery).toBeDefined();
    expect(validateQuoteForm(form({ batteryHealth: '80,5' })).errors.battery).toBeDefined();
    expect(validateQuoteForm(form({ consumerPrice: '-5' })).errors.consumerPrice).toBeDefined();
    expect(validateQuoteForm(form({ batteryHealth: '0' })).request?.batteryHealthPercent).toBe(0);
    expect(validateQuoteForm(form({ batteryHealth: '100' })).request?.batteryHealthPercent).toBe(100);
  });
});

describe('helpers', () => {
  it('labels models without empty storage', () => {
    expect(modelLabel({ brand: 'Apple', model: 'iPhone 13', storage: '128 GB' })).toBe('Apple iPhone 13 128 GB');
    expect(modelLabel({ brand: 'Apple', model: 'iPhone 13', storage: null })).toBe('Apple iPhone 13');
  });

  const dict: Record<string, string> = {
    'errors.codes.ankauf.sheet_missing': 'Blatt fehlt.',
    'errors.codes.ankauf.no_valid_price': 'Kein Preis.',
  };
  const t = (k: string) => dict[k] ?? k;
  const has = (k: string) => k in dict;
  const fallback = (p: ApiProblem) => `fallback:${p.title}`;
  const problem = (code: string | null, title: string): ApiProblem => ({ status: 422, code, title, errors: {} });

  it('describes Ankauf problems with translated text and API detail where useful', () => {
    expect(ankaufProblemText(problem('ankauf.sheet_missing', "Sheet 'X' not found. Available: A, B."), t, has, fallback)).toBe(
      "Blatt fehlt. (Sheet 'X' not found. Available: A, B.)",
    );
    expect(ankaufProblemText(problem('ankauf.no_valid_price', 'There is no current price'), t, has, fallback)).toBe('Kein Preis.');
    expect(ankaufProblemText(problem('other', 'Other'), t, has, fallback)).toBe('fallback:Other');
    expect(ankaufProblemText(problem(null, 'Nothing'), t, has, fallback)).toBe('fallback:Nothing');
  });
});
