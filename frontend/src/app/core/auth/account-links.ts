import { EmailLanguage } from '../api/models';

/**
 * Reads the one-time token from an email link's fragment (`#token=…`). The token is in the fragment so it never
 * reaches server or proxy logs; the caller removes it from the address bar right away ({@link consumeFragmentToken}).
 */
export function readFragmentToken(hash: string | null | undefined): string | null {
  if (!hash) return null;
  const params = new URLSearchParams(hash.startsWith('#') ? hash.slice(1) : hash);
  const token = params.get('token')?.trim();
  return token ? token : null;
}

/**
 * Takes the token from `location.hash` and replaces the current history entry with the same URL without the
 * fragment, so it does not stay in the address bar, history or bookmarks. Returns null outside the browser.
 */
export function consumeFragmentToken(win: Window | null | undefined): string | null {
  if (!win?.location) return null;
  const token = readFragmentToken(win.location.hash);
  if (win.location.hash) {
    try {
      win.history.replaceState(win.history.state, '', win.location.pathname + win.location.search);
    } catch {
      /* ignore (sandboxed iframes) */
    }
  }
  return token;
}

/**
 * Language for account emails. The API accepts only `de` and `en` (anything else is a 422) and defaults to German,
 * so English is sent only for the English UI; every other UI language gets the German email.
 */
export function emailLanguage(uiLanguage: string): EmailLanguage {
  return uiLanguage === 'en' ? 'en' : 'de';
}
