import { consumeFragmentToken, emailLanguage, readFragmentToken } from './account-links';
import { meetsPasswordPolicy, newPasswordErrors } from './password-policy';

describe('readFragmentToken', () => {
  it('reads the token from "#token=…"', () => {
    expect(readFragmentToken('#token=abc_DEF-123')).toBe('abc_DEF-123');
    expect(readFragmentToken('token=abc')).toBe('abc');
  });

  it('decodes URL encoding and ignores other parameters', () => {
    expect(readFragmentToken('#foo=1&token=a%2Bb&bar=2')).toBe('a+b');
  });

  it('returns null for a missing or empty token', () => {
    expect(readFragmentToken('')).toBeNull();
    expect(readFragmentToken(null)).toBeNull();
    expect(readFragmentToken('#')).toBeNull();
    expect(readFragmentToken('#token=')).toBeNull();
    expect(readFragmentToken('#token=%20%20')).toBeNull();
    expect(readFragmentToken('#other=1')).toBeNull();
  });
});

describe('consumeFragmentToken', () => {
  afterEach(() => window.history.replaceState(null, '', '/'));

  it('returns the token and removes the fragment from the address bar, keeping path and query', () => {
    window.history.replaceState({ navigationId: 3 }, '', '/email-bestaetigen?x=1#token=secret-token');
    expect(consumeFragmentToken(window)).toBe('secret-token');
    expect(window.location.hash).toBe('');
    expect(window.location.pathname).toBe('/email-bestaetigen');
    expect(window.location.search).toBe('?x=1');
    expect(window.history.state).toEqual({ navigationId: 3 });
  });

  it('returns null without a fragment and outside the browser', () => {
    window.history.replaceState(null, '', '/passwort-zuruecksetzen');
    expect(consumeFragmentToken(window)).toBeNull();
    expect(consumeFragmentToken(null)).toBeNull();
  });
});

describe('emailLanguage', () => {
  it('sends English only for the English UI; everything else gets German like the API default', () => {
    expect(emailLanguage('en')).toBe('en');
    for (const lang of ['de', 'tr', 'es', 'ru', 'ar', 'fa']) expect(emailLanguage(lang)).toBe('de');
  });
});

describe('password rules', () => {
  it('mirrors the server policy', () => {
    expect(meetsPasswordPolicy('kurz1')).toBe(false);
    expect(meetsPasswordPolicy('nurbuchstaben')).toBe(false);
    expect(meetsPasswordPolicy('1234567890')).toBe(false);
    expect(meetsPasswordPolicy('Passwort-2026')).toBe(true);
    expect(meetsPasswordPolicy('Straße 12345')).toBe(true);
  });

  it('validates the reset form', () => {
    expect(newPasswordErrors({ newPassword: '', confirmPassword: '' })).toEqual({
      newPassword: 'validation.required',
      confirmPassword: 'validation.required',
    });
    expect(newPasswordErrors({ newPassword: 'schwach', confirmPassword: 'schwach' }).newPassword).toBe('validation.passwordPolicy');
    expect(newPasswordErrors({ newPassword: 'Passwort-2026', confirmPassword: 'Passwort-2027' })).toEqual({
      confirmPassword: 'validation.passwordMismatch',
    });
    expect(newPasswordErrors({ newPassword: 'Passwort-2026', confirmPassword: 'Passwort-2026' })).toEqual({});
  });

  it('requires the current password and a different new one when changing', () => {
    expect(newPasswordErrors({ currentPassword: '', newPassword: 'Passwort-2026', confirmPassword: 'Passwort-2026' })).toEqual({
      currentPassword: 'validation.required',
    });
    expect(
      newPasswordErrors({ currentPassword: 'Passwort-2026', newPassword: 'Passwort-2026', confirmPassword: 'Passwort-2026' }).newPassword,
    ).toBe('validation.passwordUnchanged');
  });
});
