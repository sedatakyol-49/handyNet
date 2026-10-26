import { expect, test } from '@playwright/test';
import { PASSWORD, RUN } from './support/handynet';

/**
 * Server-side field validation is shown in the UI language: the API returns a stable code per field error
 * (`errorCodes`), the UI maps it to `errors.validation.*`. A whitespace-only company name passes the client checks
 * but fails `NotEmpty` on the server (422); nothing is created.
 */
test('server-side validation errors are shown in the selected UI language', async ({ page }) => {
  await page.goto('/registrierung');
  await page.locator('#reg-name').fill('   ');
  await page.locator('#reg-legal').fill(`E2E Valid ${RUN} GmbH`);
  await page.locator('#reg-street').fill('Teststraße 1');
  await page.locator('#reg-zip').fill('10115');
  await page.locator('#reg-city').fill('Berlin');
  await page.getByLabel('Einzelhandel').check();
  await page.locator('#reg-display').fill('Valid Tester');
  await page.locator('#reg-email').fill(`valid-${RUN}@e2e.handynet.test`);
  await page.locator('#reg-password').fill(PASSWORD);

  await page.locator('#auth-lang').selectOption('tr');
  const response = page.waitForResponse((r) => r.status() === 422 && r.request().method() === 'POST');
  await page.getByTestId('register-submit').click();
  const body = await (await response).json();
  expect(body.code).toBe('validation.failed');
  expect(body.errorCodes['Organization.Name'][0].code).toBe('validation.required');

  await expect(page.locator('#reg-name-error')).toHaveText(/Bu alan zorunludur\./);
  await expect(page.locator('#reg-name-error')).not.toContainText('must not be empty');

  // Switching the language re-renders the same server error.
  await page.locator('#auth-lang').selectOption('en');
  await expect(page.locator('#reg-name-error')).toHaveText(/This field is required\./);
});
