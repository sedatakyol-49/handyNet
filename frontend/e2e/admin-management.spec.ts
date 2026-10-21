import { APIRequestContext, Page, expect, request, test } from '@playwright/test';
import {
  ADMIN_EMAIL,
  ADMIN_PASSWORD,
  API_URL,
  Api,
  PASSWORD,
  RUN,
  company,
  hasAdmin,
  login,
} from './support/handynet';

/**
 * Platform administration of companies and user accounts (ADR-0013): the admin opens a company, edits its master data and
 * changes a member's role; locks a user who then cannot sign in, unlocks them again, and sends a password reset link.
 */
test.describe('admin: companies and users', () => {
  test.skip(
    !hasAdmin,
    'Set HN_ADMIN_EMAIL and HN_ADMIN_PASSWORD to run e2e tests against the API.',
  );

  const owner = {
    ...company('shop'),
    name: `E2E Verwaltung ${RUN}`,
    email: `admin-mgmt-owner-${RUN}@e2e.handynet.test`,
    displayName: 'Olga Inhaberin',
  };
  const staff = {
    email: `admin-mgmt-staff-${RUN}@e2e.handynet.test`,
    displayName: `Karl Kasse ${RUN}`,
  };
  let organizationId = '';

  async function signIn(email: string): Promise<number> {
    const ctx: APIRequestContext = await request.newContext({ baseURL: API_URL });
    try {
      return (await ctx.post('/api/auth/login', { data: { email, password: PASSWORD } })).status();
    } finally {
      await ctx.dispose();
    }
  }

  async function openAdmin(page: Page, path: string): Promise<void> {
    await login(page, ADMIN_EMAIL, ADMIN_PASSWORD);
    await page.goto(path);
  }

  test.beforeAll(async () => {
    organizationId = await Api.register(owner);
    const o = await Api.login(owner.email, PASSWORD);
    await o.call(
      'POST',
      '/api/organization/staff',
      organizationId,
      { email: staff.email, displayName: staff.displayName, password: PASSWORD, role: 'Staff' },
      200,
    );
    await o.dispose();
  });

  test('admin opens a company, edits its master data and changes a member role', async ({
    page,
  }) => {
    await openAdmin(page, '/app/administration');
    await expect(
      page.getByTestId('admin-tabs').getByRole('link', { name: 'Unternehmen' }),
    ).toHaveAttribute('aria-current', 'page');
    await page.getByTestId('admin-search').fill(owner.name);
    await page.getByTestId('admin-search').press('Enter');
    const row = page.locator(`tr[data-org="${owner.name}"]`);
    await expect(row.getByTestId('admin-members')).toHaveText('2');
    await row.getByTestId('admin-open').click();

    await expect(page).toHaveURL(new RegExp(`/app/administration/unternehmen/${organizationId}$`));
    await expect(page.getByRole('heading', { level: 1 })).toHaveText(owner.name);
    await expect(
      page.getByTestId('admin-tabs').getByRole('link', { name: 'Unternehmen' }),
    ).toHaveAttribute('aria-current', 'page');
    await expect(page.getByTestId('company-history')).toContainText('In Prüfung');

    // Master data: an invalid website is refused on the client; a valid change is saved and survives a reload.
    await page.getByTestId('cf-website').fill('www.ohne-schema.de');
    await page.getByTestId('cf-save').click();
    await expect(page.locator('#cf-web-error')).toContainText('https://');
    await page.getByTestId('cf-website').fill('https://www.e2e-verwaltung.example.de');
    await page.getByTestId('cf-city').fill('Hamburg');
    await page.getByTestId('cf-zip').fill('20095');
    await page.getByTestId('cf-email').fill(`kontakt-${RUN}@e2e.handynet.test`);
    await page.getByTestId('cf-reason').fill('Umzug laut Gewerbeanmeldung');
    await page.getByTestId('cf-save').click();
    await expect(page.getByText(`Stammdaten von ${owner.name} gespeichert.`)).toBeVisible();
    await page.reload();
    await expect(page.getByTestId('cf-city')).toHaveValue('Hamburg');
    await expect(page.getByTestId('cf-website')).toHaveValue(
      'https://www.e2e-verwaltung.example.de',
    );

    // The only owner is protected; the staff member becomes manager after the confirmation dialog.
    const ownerRow = page.locator(`tr[data-member="${owner.email}"]`);
    await expect(ownerRow.getByTestId('member-remove-open')).toBeDisabled();
    const staffRow = page.locator(`tr[data-member="${staff.email}"]`);
    await staffRow.getByTestId('member-role-select').selectOption('Manager');
    await staffRow.getByTestId('member-role-apply').click();
    const dialog = page.getByTestId('company-dialog');
    await expect(dialog.getByTestId('company-dialog-text')).toContainText('Manager');
    await dialog.getByTestId('company-reason').fill('Filialleitung');
    await dialog.getByTestId('company-confirm').click();
    await expect(dialog).toBeHidden();
    await expect(
      page.getByText(`${staff.displayName} hat jetzt die Rolle „Manager“.`),
    ).toBeVisible();
    await page.reload();
    await expect(
      page.locator(`tr[data-member="${staff.email}"]`).getByTestId('member-role-select'),
    ).toHaveValue('Manager');
  });

  test('admin locks a user who then cannot sign in, unlocks them and sends a password reset', async ({
    page,
  }) => {
    expect(await signIn(staff.email)).toBe(200);

    await openAdmin(page, '/app/administration/benutzer');
    await expect(
      page.getByTestId('admin-tabs').getByRole('link', { name: 'Benutzer' }),
    ).toHaveAttribute('aria-current', 'page');
    await page.getByTestId('users-search').fill(staff.email);
    await page.getByTestId('users-search').press('Enter');
    const row = page.locator(`tr[data-user="${staff.email}"]`);
    await expect(row).toContainText(owner.name);
    await row.getByTestId('users-open').click();
    await expect(page.getByRole('heading', { level: 1 })).toHaveText(staff.displayName);
    await expect(page.getByTestId('user-sessions')).not.toHaveText('0');

    // Lock: a reason is required; afterwards sessions are gone and sign-in fails.
    await page.getByTestId('user-action-disable').click();
    const dialog = page.getByTestId('user-dialog');
    await dialog.getByTestId('user-confirm').click();
    await expect(dialog.locator('#user-reason-error')).toBeVisible();
    await dialog.getByTestId('user-reason').fill('E2E: Gerät gestohlen');
    await dialog.getByTestId('user-confirm').click();
    await expect(dialog).toBeHidden();
    await expect(page.getByTestId('user-disabled-reason')).toContainText('E2E: Gerät gestohlen');
    await expect(page.getByTestId('user-sessions')).toHaveText('0');
    expect(await signIn(staff.email)).toBe(422);

    // The locked user also cannot sign in through the UI.
    const other = await page.context().browser()!.newPage();
    await other.goto('/login');
    await other.locator('#login-email').fill(staff.email);
    await other.locator('#login-password').fill(PASSWORD);
    await other.getByRole('button', { name: 'Anmelden' }).click();
    await expect(other).toHaveURL(/\/login/);
    await expect(other.getByRole('alert')).toBeVisible();
    await other.close();

    // Unlock: sign-in works again.
    await page.getByTestId('user-action-enable').click();
    await dialog.getByTestId('user-confirm').click();
    await expect(dialog).toBeHidden();
    await expect(page.getByTestId('user-action-disable')).toBeVisible();
    expect(await signIn(staff.email)).toBe(200);

    // Password reset link (the admin never sees the link).
    await page.getByTestId('user-action-password-reset').click();
    await expect(dialog.getByTestId('user-dialog-text')).toContainText(staff.email);
    await dialog.getByTestId('user-confirm').click();
    await expect(dialog).toBeHidden();
    await expect(page.getByText(`Link zum Zurücksetzen an ${staff.email} gesendet.`)).toBeVisible();
    await expect(page.getByTestId('user-profile')).toContainText('Passwort-Link zuletzt gesendet');
  });
});
