import { mrzCheckDigit, mrzDocumentType, mrzLines, mrzNameCase, mrzNameMayBeTransliterated, parseMrz } from './mrz';

const TODAY = new Date(2026, 9, 22);
// Official specimens (ICAO 9303, Bundesdruckerei "Erika Mustermann").
const ICAO_PASSPORT = 'P<UTOERIKSSON<<ANNA<MARIA<<<<<<<<<<<<<<<<<<<\nL898902C36UTO7408122F1204159ZE184226B<<<<<10';
const GERMAN_ID = 'IDD<<T220001293<<<<<<<<<<<<<<<\n6408125<2010315D<<<<<<<<<<<<<4\nMUSTERMANN<<ERIKA<<<<<<<<<<<<<';
const GERMAN_PASSPORT = 'P<D<<MUSTERMANN<<ERIKA<<<<<<<<<<<<<<<<<<<<<<\nC01X00T478D<<6408125F2702283<<<<<<<<<<<<<<<4';

function tamper(text: string, line: number, pos: number): string {
  const lines = text.split('\n');
  const c = lines[line][pos];
  const next = c === '9' ? '8' : c === 'Z' ? 'Y' : String.fromCharCode(c.charCodeAt(0) + 1);
  lines[line] = lines[line].slice(0, pos) + next + lines[line].slice(pos + 1);
  return lines.join('\n');
}

describe('MRZ', () => {
  it('computes ICAO check digits', () => {
    expect(mrzCheckDigit('L898902C3')).toBe(6);
    expect(mrzCheckDigit('740812')).toBe(2);
    expect(mrzCheckDigit('T22000129')).toBe(3);
    expect(mrzCheckDigit('<<<')).toBe(0);
  });

  it('parses the German ID card specimen (TD1)', () => {
    const r = parseMrz(GERMAN_ID, TODAY);
    expect(r.ok).toBe(true);
    if (!r.ok) return;
    expect(r.data).toMatchObject({
      format: 'TD1', documentType: 'IdCard', documentNumber: 'T22000129', surname: 'MUSTERMANN', givenNames: 'ERIKA',
      nationality: 'D', issuingState: 'D', birthDate: '1964-08-12', expiryDate: '2020-10-31', sex: null,
    });
  });

  it('parses passports (TD3)', () => {
    const p = parseMrz(GERMAN_PASSPORT, TODAY);
    expect(p.ok && p.data).toMatchObject({ format: 'TD3', documentType: 'Passport', documentNumber: 'C01X00T47', expiryDate: '2027-02-28', sex: 'F' });
    const icao = parseMrz(ICAO_PASSPORT, TODAY);
    expect(icao.ok && icao.data).toMatchObject({ surname: 'ERIKSSON', givenNames: 'ANNA MARIA', birthDate: '1974-08-12', nationality: 'UTO' });
  });

  it('accepts keyboard-wedge input without line breaks, in lower case and with spaces', () => {
    expect(parseMrz(GERMAN_ID.replace(/\n/g, '').toLowerCase(), TODAY).ok).toBe(true);
    expect(parseMrz(' ' + GERMAN_PASSPORT.replace('\n', '\r\n') + '\n', TODAY).ok).toBe(true);
    expect(mrzLines(GERMAN_PASSPORT.replace('\n', ''))).toHaveLength(2);
  });

  it.each([
    [GERMAN_ID, 0, 6, 'documentNumber'],
    [GERMAN_ID, 0, 14, 'documentNumber'],
    [GERMAN_ID, 1, 1, 'birthDate'],
    [GERMAN_ID, 1, 9, 'expiryDate'],
    [GERMAN_ID, 1, 29, 'composite'],
    [GERMAN_PASSPORT, 1, 24, 'expiryDate'],
    [GERMAN_PASSPORT, 1, 43, 'composite'],
    [ICAO_PASSPORT, 1, 30, 'personalNumber'],
  ] as const)('refuses a wrong check digit (%#)', (text, line, pos, field) => {
    expect(parseMrz(tamper(text, line, pos), TODAY)).toEqual({ ok: false, error: 'checkDigit', field });
  });

  it('reports incomplete input while typing and format errors otherwise', () => {
    expect(parseMrz('', TODAY)).toEqual({ ok: false, error: 'empty' });
    expect(parseMrz('IDD<<T220001293<<<<<<<<<<<<<<<', TODAY)).toEqual({ ok: false, error: 'incomplete' });
    expect(parseMrz('IDD<<T22000129ä', TODAY)).toEqual({ ok: false, error: 'format' });
  });

  it('maps document codes and formats names', () => {
    expect(mrzDocumentType('AR')).toBe('ResidencePermit');
    expect(mrzDocumentType('P')).toBe('Passport');
    expect(mrzDocumentType('ID')).toBe('IdCard');
    expect(mrzNameCase('ANNA MARIA')).toBe('Anna Maria');
    expect(mrzNameCase('MUELLER-LUEDENSCHEIDT')).toBe('Mueller-Luedenscheidt');
    expect(mrzNameMayBeTransliterated('MUELLER')).toBe(true);
    expect(mrzNameMayBeTransliterated('ERIKA')).toBe(false);
  });
});
