import { IdDocumentType } from '../../core/api/models';

/**
 * ICAO 9303 machine-readable zone (TD1 ID cards 3×30, TD2 2×36, TD3 passports 2×44) with all check digits — the same
 * rules as the API (`Mrz` in the Ankauf domain), which verifies the MRZ again on receive. Works with keyboard-wedge
 * document scanners (they type the lines into the focused field, with or without line breaks) and with pasted text.
 */
export type MrzFormat = 'TD1' | 'TD2' | 'TD3';

export interface MrzData {
  format: MrzFormat;
  documentCode: string;
  documentType: IdDocumentType;
  issuingState: string;
  documentNumber: string;
  surname: string;
  givenNames: string;
  nationality: string;
  /** yyyy-MM-dd */
  birthDate: string;
  sex: 'M' | 'F' | 'X' | null;
  /** yyyy-MM-dd */
  expiryDate: string;
}

export type MrzField = 'documentNumber' | 'birthDate' | 'expiryDate' | 'personalNumber' | 'composite';

export type MrzResult =
  | { ok: true; data: MrzData; lines: string[] }
  /** `incomplete`: still typing (fewer characters than a full MRZ). */
  | { ok: false; error: 'empty' | 'incomplete' | 'format' | 'checkDigit' | 'date'; field?: MrzField };

const WEIGHTS = [7, 3, 1];

function charValue(c: string): number {
  if (c >= '0' && c <= '9') return c.charCodeAt(0) - 48;
  if (c >= 'A' && c <= 'Z') return c.charCodeAt(0) - 55;
  if (c === '<') return 0;
  throw new Error(`Invalid MRZ character ${c}`);
}

/** ICAO check digit: weights 7-3-1, 0–9 = value, A–Z = 10–35, `<` = 0. */
export function mrzCheckDigit(value: string): number {
  let sum = 0;
  for (let i = 0; i < value.length; i++) sum += charValue(value[i]) * WEIGHTS[i % 3];
  return sum % 10;
}

/** Upper case, spaces removed, split into lines; a single line of 90/88/72 characters is split into the MRZ lines. */
export function mrzLines(text: string): string[] {
  const lines = (text ?? '')
    .toUpperCase()
    .replace(/\r/g, '\n')
    .split('\n')
    .map((l) => l.replace(/\s+/g, ''))
    .filter((l) => l.length > 0);
  if (lines.length === 1) {
    const s = lines[0];
    if (s.length === 90) return [s.slice(0, 30), s.slice(30, 60), s.slice(60)];
    if (s.length === 88) return [s.slice(0, 44), s.slice(44)];
    if (s.length === 72) return [s.slice(0, 36), s.slice(36)];
  }
  return lines;
}

function check(fieldWithDigit: string, field: MrzField): MrzResult | null {
  const digit = fieldWithDigit[fieldWithDigit.length - 1];
  if (!/[0-9]/.test(digit) || mrzCheckDigit(fieldWithDigit.slice(0, -1)) !== Number(digit)) return { ok: false, error: 'checkDigit', field };
  return null;
}

function date(yymmdd: string, year: (yy: number) => number): string | null {
  if (!/^\d{6}$/.test(yymmdd)) return null;
  const yy = Number(yymmdd.slice(0, 2));
  const mm = Number(yymmdd.slice(2, 4));
  const dd = Number(yymmdd.slice(4, 6));
  const y = year(yy);
  const d = new Date(Date.UTC(y, mm - 1, dd));
  if (mm < 1 || mm > 12 || d.getUTCMonth() !== mm - 1 || d.getUTCDate() !== dd) return null;
  return `${String(y).padStart(4, '0')}-${yymmdd.slice(2, 4)}-${yymmdd.slice(4, 6)}`;
}

function dates(birthWithCheck: string, expiryWithCheck: string, todayYear: number): MrzResult | { birth: string; expiry: string } {
  const b = check(birthWithCheck, 'birthDate');
  if (b) return b;
  const e = check(expiryWithCheck, 'expiryDate');
  if (e) return e;
  // Birth: the latest century that is not in the future. Expiry: 20YY.
  const birth = date(birthWithCheck.slice(0, 6), (yy) => (2000 + yy > todayYear ? 1900 + yy : 2000 + yy));
  const expiry = date(expiryWithCheck.slice(0, 6), (yy) => 2000 + yy);
  if (!birth || !expiry) return { ok: false, error: 'date' };
  return { birth, expiry };
}

function names(field: string): { surname: string; givenNames: string } {
  const trimmed = field.replace(/<+$/, '');
  const i = trimmed.indexOf('<<');
  const clean = (s: string) => s.split('<').filter(Boolean).join(' ');
  return i < 0 ? { surname: clean(trimmed), givenNames: '' } : { surname: clean(trimmed.slice(0, i)), givenNames: clean(trimmed.slice(i + 2)) };
}

const code = (s: string) => s.replace(/</g, '');
const sex = (c: string): MrzData['sex'] => (c === 'M' || c === 'F' || c === 'X' ? c : null);

/** P = passport; A…, IR, RP = residence permit (German eAT "AR"); other I/C = ID card. */
export function mrzDocumentType(documentCode: string): IdDocumentType {
  if (documentCode.startsWith('P')) return 'Passport';
  if (documentCode.startsWith('A') || documentCode === 'IR' || documentCode === 'RP') return 'ResidencePermit';
  return 'IdCard';
}

export function parseMrz(text: string, today: Date = new Date()): MrzResult {
  const lines = mrzLines(text);
  if (!lines.length) return { ok: false, error: 'empty' };
  if (lines.some((l) => /[^A-Z0-9<]/.test(l))) return { ok: false, error: 'format' };
  const lengths = lines.map((l) => l.length).join(',');
  const y = today.getFullYear();
  if (lengths === '30,30,30') return td1(lines, y);
  if (lengths === '44,44') return td23(lines, 'TD3', y);
  if (lengths === '36,36') return td23(lines, 'TD2', y);
  const total = lines.reduce((n, l) => n + l.length, 0);
  return total < 88 && lines.every((l) => l.length <= 44) ? { ok: false, error: 'incomplete' } : { ok: false, error: 'format' };
}

function td1([l1, l2, l3]: string[], todayYear: number): MrzResult {
  const docCode = l1.slice(0, 2);
  if (!'IAC'.includes(docCode[0])) return { ok: false, error: 'format' };
  let number: string;
  let numberWithCheck: string;
  if (l1[14] === '<' && l1[13] !== '<') {
    // Long document number continues in the optional data, followed by its check digit.
    const optional = l1.slice(15, 30);
    const end = optional.indexOf('<');
    if (end < 1) return { ok: false, error: 'format' };
    number = l1.slice(5, 14) + optional.slice(0, end - 1);
    numberWithCheck = number + optional[end - 1];
  } else {
    number = l1.slice(5, 14);
    numberWithCheck = l1.slice(5, 15);
  }
  const n = check(numberWithCheck, 'documentNumber');
  if (n) return n;
  const d = dates(l2.slice(0, 7), l2.slice(8, 15), todayYear);
  if ('ok' in d) return d;
  const c = check(l1.slice(5, 30) + l2.slice(0, 7) + l2.slice(8, 15) + l2.slice(18, 29) + l2[29], 'composite');
  if (c) return c;
  return {
    ok: true,
    lines: [l1, l2, l3],
    data: {
      format: 'TD1',
      documentCode: docCode.replace(/<+$/, ''),
      documentType: mrzDocumentType(docCode),
      issuingState: code(l1.slice(2, 5)),
      documentNumber: code(number),
      ...names(l3),
      nationality: code(l2.slice(15, 18)),
      birthDate: d.birth,
      sex: sex(l2[7]),
      expiryDate: d.expiry,
    },
  };
}

function td23([l1, l2]: string[], format: 'TD2' | 'TD3', todayYear: number): MrzResult {
  const docCode = l1.slice(0, 2);
  if (format === 'TD3' ? docCode[0] !== 'P' : !'IACP'.includes(docCode[0])) return { ok: false, error: 'format' };
  const n = check(l2.slice(0, 10), 'documentNumber');
  if (n) return n;
  const d = dates(l2.slice(13, 20), l2.slice(21, 28), todayYear);
  if ('ok' in d) return d;
  let composite: string;
  if (format === 'TD3') {
    const personal = l2.slice(28, 42);
    const personalCheck = l2[42];
    if (!(personalCheck === '<' && /^<+$/.test(personal))) {
      const p = check(personal + personalCheck, 'personalNumber');
      if (p) return p;
    }
    composite = l2.slice(0, 10) + l2.slice(13, 20) + l2.slice(21, 43) + l2[43];
  } else {
    composite = l2.slice(0, 10) + l2.slice(13, 20) + l2.slice(21, 35) + l2[35];
  }
  const c = check(composite, 'composite');
  if (c) return c;
  return {
    ok: true,
    lines: [l1, l2],
    data: {
      format,
      documentCode: docCode.replace(/<+$/, ''),
      documentType: mrzDocumentType(docCode),
      issuingState: code(l1.slice(2, 5)),
      documentNumber: code(l2.slice(0, 9)),
      ...names(l1.slice(5)),
      nationality: code(l2.slice(10, 13)),
      birthDate: d.birth,
      sex: sex(l2[20]),
      expiryDate: d.expiry,
    },
  };
}

/** "MUELLER" → "Mueller", "ANNA MARIA" → "Anna Maria" (MRZ names are transliterated upper case; check against the card). */
export function mrzNameCase(name: string): string {
  return name
    .toLowerCase()
    .split(' ')
    .map((p) => p.replace(/(^|-)([a-z])/g, (_m, sep: string, ch: string) => sep + ch.toUpperCase()))
    .join(' ');
}

/** Names in the MRZ lose umlauts (Ä → AE, ß → SS): a hint to compare with the printed name. */
export function mrzNameMayBeTransliterated(name: string): boolean {
  return /AE|OE|UE|SS/.test(name.toUpperCase());
}
