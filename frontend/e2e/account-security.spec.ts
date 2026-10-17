import { Browser, Page, expect, request, test } from '@playwright/test';
import { API_URL, Api, PASSWORD, RUN, company, login, logout } from './support/handynet';

/**
 * Sessions and account emails (ADR-0010) against the real API. Needs no admin account: the users are self-registered
 * (unverified organization and email). Emails are not read (Mailpit may not run), so links are exercised with
 * invalid tokens only.
 */

const NEW_PASSWORD = 'Neues-Passwort-2026!';

interface StoredSession {
  accessToken: string;
  expiresAt: string;
  refreshToken: string;
}

async function storedSession(page: Page): Promise<StoredSession> {
  const raw = await page.evaluate(() => localStorage.getItem('hn.auth'));
  expect(raw, 'session in localStorage').toBeTruthy();
  return JSON.parse(raw!) as StoredSession;
}

/** Presents a refresh token to the API directly; returns the HTTP status. */
async function refreshStatus(refreshToken: string): Promise<number> {
  const ctx = await request.newContext({ baseURL: API_URL });
  const res = await ctx.post('/api/auth/refresh', { data: { refreshToken } });
  await ctx.dispose();
  return res.status();
}

async function registerUser(kind: 'shop' | 'supplier', suffix: string) {
  const c = company(kind);
  const user = { ...c, name: `${c.name} ${suffix}`, email: c.email.replace('@', `-${suffix}@`) };
  await Api.register(user);
  return user;
}

test.describe('account security', () => {
  test('password reset request is enumeration-safe', async ({ page }) => {
    const user = await registerUser('shop', 'reset');
    await page.goto('/login');
    await page.getByTestId('forgot-password').click();
    await expect(page).toHaveURL(/\/passwort-vergessen$/);
    await expect(page.getByRole('heading', { name: 'Passwort vergessen' })).toBeVisible();

    // Client validation first.
    await page.getByRole('button', { name: 'Link anfordern' }).click();
    await expect(page.getByText('Pflichtfeld.')).toBeVisible();

    // Unknown and existing address get the same confirmation.
    for (const email of [`niemand-${RUN}@e2e.handynet.test`, user.email]) {
      await page.locator('#forgot-email').fill(email);
      const response = page.waitForResponse((r) => r.url().endsWith('/api/auth/password-reset/request'));
      await page.getByRole('button', { name: 'Link anfordern' }).click();
      const res = await response;
      expect(res.status()).toBe(202);
      expect(res.request().postDataJSON()).toEqual({ email, language: 'de' });
      const confirmation = page.getByTestId('reset-requested');
      await expect(confirmation).toContainText(`Falls für ${email} ein Konto besteht`);
      await confirmation.getByRole('button', { name: 'Erneut anfordern' }).click();
    }
  });

  test('email links with an invalid token: verify landing and reset page', async ({ page }) => {
    await page.goto('/email-bestaetigen#token=ungueltiger-token-' + RUN);
    await expect(page.getByTestId('verify-invalid')).toContainText('Der Link ist ungültig oder wurde bereits verwendet.');
    // The token is removed from the address bar.
    expect(new URL(page.url()).hash).toBe('');
    await expect(page.getByTestId('verify-invalid').getByRole('link', { name: 'Anmelden' })).toBeVisible();

    await page.goto('/email-bestaetigen');
    await expect(page.getByTestId('verify-missing')).toContainText('Dieser Link ist unvollständig');

    await page.goto('/passwort-zuruecksetzen#token=ungueltig-' + RUN);
    expect(new URL(page.url()).hash).toBe('');
    await page.locator('#reset-password').fill(NEW_PASSWORD);
    await page.locator('#reset-confirm').fill('anders-' + NEW_PASSWORD);
    await page.getByTestId('reset-submit').click();
    await expect(page.getByText('Die Passwörter stimmen nicht überein.')).toBeVisible();
    await page.locator('#reset-confirm').fill(NEW_PASSWORD);
    await page.getByTestId('reset-submit').click();
    await expect(page.getByTestId('reset-link-problem')).toContainText('Der Link ist ungültig oder wurde bereits verwendet.');
    await page.getByRole('link', { name: 'Neuen Link anfordern' }).click();
    await expect(page).toHaveURL(/\/passwort-vergessen$/);
  });

  test('change password, logout revokes the session, login with the new password', async ({ page }) => {
    const user = await registerUser('shop', 'pw');
    await login(page, user.email, PASSWORD);

    // Freshly registered: email not verified yet → banner with resend; the registration already sent a mail,
    // so "resend" right away hits the one-minute cooldown (409) and says so.
    const banner = page.getByTestId('verify-email-banner');
    await expect(banner).toContainText('Bitte bestätigen Sie Ihre E-Mail-Adresse');
    const resend = page.waitForResponse((r) => r.url().endsWith('/api/auth/verify-email/resend'));
    await banner.getByTestId('verify-resend').click();
    expect((await resend).status()).toBe(409);
    await expect(banner).toContainText('Bitte warten Sie eine Minute');
    await expect(banner.getByTestId('verify-resend')).toBeDisabled();

    await page.getByTestId('user-menu').click();
    await page.getByTestId('account-link').click();
    await expect(page).toHaveURL(/\/app\/konto$/);
    await expect(page.getByTestId('email-unverified')).toBeVisible();

    const form = page.getByTestId('change-password-form');
    await form.locator('#pw-current').fill('Falsches-Passwort-1');
    await form.locator('#pw-new').fill(NEW_PASSWORD);
    await form.locator('#pw-confirm').fill(NEW_PASSWORD);
    await page.getByTestId('change-password-submit').click();
    await expect(form.getByText('Das aktuelle Passwort ist nicht korrekt.')).toBeVisible();

    const before = await storedSession(page);
    await form.locator('#pw-current').fill(PASSWORD);
    await page.getByTestId('change-password-submit').click();
    await expect(page.getByText('Passwort geändert. Alle anderen Sitzungen wurden beendet.')).toBeVisible();
    const after = await storedSession(page);
    expect(after.refreshToken).not.toBe(before.refreshToken);
    // The previous session family was revoked by the change.
    expect(await refreshStatus(before.refreshToken)).toBe(401);

    await logout(page);
    expect(await page.evaluate(() => localStorage.getItem('hn.auth'))).toBeNull();
    // Logout revoked the refresh token on the server (POST /api/auth/logout is sent after leaving the app).
    await expect.poll(() => refreshStatus(after.refreshToken), { timeout: 10_000 }).toBe(401);

    await page.locator('#login-email').fill(user.email);
    await page.locator('#login-password').fill(PASSWORD);
    await page.getByRole('button', { name: 'Anmelden' }).click();
    await expect(page.getByRole('alert')).toBeVisible();
    await expect(page).toHaveURL(/\/login/);

    await login(page, user.email, NEW_PASSWORD);
  });

  test('expired access token is refreshed transparently; sign out everywhere ends other sessions', async ({ browser }) => {
    const user = await registerUser('shop', 'all');
    const [a, b] = await Promise.all([newPage(browser), newPage(browser)]);
    await login(a, user.email, PASSWORD);
    await login(b, user.email, PASSWORD);

    // Let device B's access token look expired: the next API call refreshes first (single rotation).
    const old = await storedSession(b);
    await b.evaluate(() => {
      const s = JSON.parse(localStorage.getItem('hn.auth')!);
      s.expiresAt = new Date(Date.now() - 1000).toISOString();
      localStorage.setItem('hn.auth', JSON.stringify(s));
    });
    const refreshes: number[] = [];
    b.on('response', (r) => {
      if (r.url().endsWith('/api/auth/refresh')) refreshes.push(r.status());
    });
    await b.reload();
    await expect(b.getByTestId('user-menu')).toBeVisible();
    await expect.poll(() => refreshes).toEqual([200]);
    const rotated = await storedSession(b);
    expect(rotated.refreshToken).not.toBe(old.refreshToken);

    // Device A: "sign out everywhere".
    await a.getByTestId('user-menu').click();
    await a.getByTestId('account-link').click();
    await a.getByTestId('logout-all').click();
    await a.getByTestId('logout-all-confirm').click();
    await expect(a).toHaveURL(/\/login\?signedOut=all/);
    await expect(a.getByText('Sie wurden auf allen Geräten abgemeldet.')).toBeVisible();

    // Device B's refresh token no longer works.
    expect(await refreshStatus(rotated.refreshToken)).toBe(401);
    await Promise.all([a.context().close(), b.context().close()]);
  });
});

async function newPage(browser: Browser): Promise<Page> {
  const context = await browser.newContext({ locale: 'de-DE', baseURL: test.info().project.use.baseURL });
  return context.newPage();
}
