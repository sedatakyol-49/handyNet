import { Pipe, PipeTransform } from '@angular/core';

const formatters = new Map<string, Intl.NumberFormat>();

function formatter(currency: string): Intl.NumberFormat {
  let f = formatters.get(currency);
  if (!f) {
    f = new Intl.NumberFormat('de-DE', { style: 'currency', currency, minimumFractionDigits: 2, maximumFractionDigits: 2 });
    formatters.set(currency, f);
  }
  return f;
}

/** Formats integer minor units (cents) as German currency, e.g. 123456 → "1.234,56 €". */
export function formatMoney(minor: number | null | undefined, currency = 'EUR'): string {
  if (minor === null || minor === undefined || Number.isNaN(minor)) return '–';
  // Integer arithmetic on cents avoids float drift; Intl only formats the final value.
  return formatter(currency || 'EUR').format(minor / 100);
}

const signedFormatters = new Map<string, Intl.NumberFormat>();

/** Like formatMoney, but always with a sign for non-zero amounts: 12345 → "+123,45 €", -500 → "-5,00 €". */
export function formatSignedMoney(minor: number | null | undefined, currency = 'EUR'): string {
  if (minor === null || minor === undefined || Number.isNaN(minor)) return '–';
  const c = currency || 'EUR';
  let f = signedFormatters.get(c);
  if (!f) {
    f = new Intl.NumberFormat('de-DE', { style: 'currency', currency: c, minimumFractionDigits: 2, maximumFractionDigits: 2, signDisplay: 'exceptZero' });
    signedFormatters.set(c, f);
  }
  return f.format(minor / 100);
}

/** Parses a German or English decimal string ("1.234,56", "89,90", "89.90") to cents; null when invalid. */
export function parseMoneyToMinor(input: string | number | null | undefined): number | null {
  if (input === null || input === undefined) return null;
  if (typeof input === 'number') return Number.isFinite(input) ? Math.round(input * 100) : null;
  let s = input.trim().replace(/\s|€/g, '');
  if (!s) return null;
  if (s.includes(',')) s = s.replace(/\./g, '').replace(',', '.');
  if (!/^-?\d+(\.\d{1,2})?$/.test(s)) return null;
  const [whole, frac = ''] = s.replace('-', '').split('.');
  const cents = Number(whole) * 100 + Number((frac + '00').slice(0, 2));
  return s.startsWith('-') ? -cents : cents;
}

/** Cents → decimal euros for API inputs that take decimals (offers). */
export function minorToDecimal(minor: number): number {
  return Math.round(minor) / 100;
}

/** Cents → editable German string without currency ("1234,50"). */
export function minorToInput(minor: number): string {
  return (minor / 100).toFixed(2).replace('.', ',');
}

/**
 * Gross per unit at a VAT rate in basis points (orders use the standard 1900 = 19 %): VAT rounded half away from zero
 * like the API's Money.ApplyRate, integer cents only.
 */
export function grossFromNetMinor(netMinor: number, vatBasisPoints = 1900): number {
  const scaled = netMinor * vatBasisPoints;
  const sign = scaled < 0 ? -1 : 1;
  const abs = Math.abs(scaled);
  const whole = Math.floor(abs / 10_000);
  const vat = (abs - whole * 10_000) * 2 >= 10_000 ? whole + 1 : whole;
  return netMinor + sign * vat;
}

/** Cents → API decimal string without float formatting, e.g. 1050 → "10.50" (query parameters). */
export function minorToDecimalString(minor: number): string {
  const sign = minor < 0 ? '-' : '';
  const abs = Math.abs(Math.trunc(minor));
  return `${sign}${Math.floor(abs / 100)}.${String(abs % 100).padStart(2, '0')}`;
}

@Pipe({ name: 'signedMoney' })
export class SignedMoneyPipe implements PipeTransform {
  transform(minor: number | null | undefined, currency = 'EUR'): string {
    return formatSignedMoney(minor, currency);
  }
}

@Pipe({ name: 'money' })
export class MoneyPipe implements PipeTransform {
  transform(minor: number | null | undefined, currency = 'EUR'): string {
    return formatMoney(minor, currency);
  }
}
