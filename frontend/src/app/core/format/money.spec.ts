import { MoneyPipe, SignedMoneyPipe, formatMoney, formatSignedMoney, minorToInput, parseMoneyToMinor } from './money';

/** Intl uses a non-breaking space between amount and currency symbol. */
const nbsp = (s: string) => s.replace(/ /g, ' ');

describe('money formatting', () => {
  it('formats cents as German euro amounts', () => {
    expect(formatMoney(123456)).toBe(nbsp('1.234,56 €'));
    expect(formatMoney(8990)).toBe(nbsp('89,90 €'));
    expect(formatMoney(5)).toBe(nbsp('0,05 €'));
    expect(formatMoney(0)).toBe(nbsp('0,00 €'));
    expect(formatMoney(-1999)).toBe(nbsp('-19,99 €'));
    expect(formatMoney(100000000)).toBe(nbsp('1.000.000,00 €'));
  });

  it('formats signed amounts with an explicit sign (zero without sign)', () => {
    expect(formatSignedMoney(66402)).toBe(nbsp('+664,02 €'));
    expect(formatSignedMoney(-123456)).toBe(nbsp('-1.234,56 €'));
    expect(formatSignedMoney(0)).toBe(nbsp('0,00 €'));
    expect(formatSignedMoney(null)).toBe('–');
    expect(new SignedMoneyPipe().transform(5)).toBe(nbsp('+0,05 €'));
  });

  it('renders a dash for missing values', () => {
    expect(formatMoney(null)).toBe('–');
    expect(formatMoney(undefined)).toBe('–');
  });

  it('is exposed as the money pipe', () => {
    expect(new MoneyPipe().transform(123456)).toBe(nbsp('1.234,56 €'));
  });

  it('parses German and English decimal input to cents without float drift', () => {
    expect(parseMoneyToMinor('89,90')).toBe(8990);
    expect(parseMoneyToMinor('1.234,56')).toBe(123456);
    expect(parseMoneyToMinor('1234.5')).toBe(123450);
    expect(parseMoneyToMinor('0,29')).toBe(29);
    expect(parseMoneyToMinor(' 12 € ')).toBe(1200);
    expect(parseMoneyToMinor('12,345')).toBeNull();
    expect(parseMoneyToMinor('abc')).toBeNull();
    expect(parseMoneyToMinor('')).toBeNull();
  });

  it('formats cents for editable inputs', () => {
    expect(minorToInput(8990)).toBe('89,90');
    expect(minorToInput(100)).toBe('1,00');
  });
});
