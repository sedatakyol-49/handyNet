import { FORMAT_LOCALES, I18nService } from '../i18n/i18n.service';
import { describeProblem } from './toast.service';

/** Minimal I18nService stand-in: a fixed dictionary, `has` and `t` like the real service. */
function fakeI18n(dict: Record<string, string>): I18nService {
  return { t: (k: string) => dict[k] ?? k, has: (k: string) => k in dict } as unknown as I18nService;
}

describe('describeProblem', () => {
  const i18n = fakeI18n({
    'errors.network': 'Netzwerkfehler',
    'errors.codes.order.empty': 'Eine Bestellung braucht mindestens eine Position.',
    'errors.codes.import.mapping_incomplete': 'Bitte ordnen Sie alle Pflichtfelder zu.',
    'errors.forbidden': 'Keine Berechtigung',
  });

  it('uses the translation for a known code', () => {
    expect(describeProblem(i18n, { status: 422, title: 'An order needs at least one line.', code: 'order.empty', errors: {} })).toBe(
      'Eine Bestellung braucht mindestens eine Position.',
    );
  });

  it('appends the API detail for codes whose title names columns/rows', () => {
    expect(
      describeProblem(i18n, { status: 422, title: 'Map these required fields: price.', code: 'import.mapping_incomplete', errors: {} }),
    ).toBe('Bitte ordnen Sie alle Pflichtfelder zu. (Map these required fields: price.)');
  });

  it('falls back to the API title, then to status texts', () => {
    expect(describeProblem(i18n, { status: 409, title: 'Something else', code: 'x.unknown', errors: {} })).toBe('Something else');
    expect(describeProblem(i18n, { status: 403, title: '', code: null, errors: {} })).toBe('Keine Berechtigung');
    expect(describeProblem(i18n, { status: 0, title: '', code: 'network', errors: {} })).toBe('Netzwerkfehler');
  });
});

describe('FORMAT_LOCALES', () => {
  const day = new Date(Date.UTC(2026, 9, 18, 10, 54));
  const fmt = (lang: keyof typeof FORMAT_LOCALES) =>
    new Intl.DateTimeFormat(FORMAT_LOCALES[lang], { dateStyle: 'medium', timeZone: 'Europe/Berlin' }).format(day);

  it('formats Persian and Arabic dates in the Gregorian calendar with Western digits', () => {
    expect(fmt('fa')).toContain('2026');
    expect(fmt('fa')).not.toMatch(/[۰-۹]/);
    expect(fmt('ar')).toContain('2026');
    expect(fmt('ar')).not.toMatch(/[٠-٩]/);
  });

  it('uses German and European English date order', () => {
    expect(fmt('de')).toBe('18.10.2026');
    expect(fmt('en')).toBe('18 Oct 2026');
  });
});
