import { expect, test, type Page } from '@playwright/test';
import { ADMIN_EMAIL, ADMIN_PASSWORD, Api, PASSWORD, RUN, company, hasAdmin, login } from './support/handynet';

/**
 * Phone layout (400px): list pages show stacked cards instead of the wide table and the page never scrolls horizontally.
 */
test.describe.serial('mobile list layout', () => {
  test.skip(!hasAdmin, 'Set HN_ADMIN_EMAIL and HN_ADMIN_PASSWORD to run e2e tests against the API.');
  test.use({ viewport: { width: 400, height: 800 } });

  const seller = { ...company('supplier'), name: `E2E Mobil ${RUN}`, email: `mobil-${RUN}@e2e.handynet.test` };

  test.beforeAll(async () => {
    const orgId = await Api.register(seller);
    const admin = await Api.login(ADMIN_EMAIL, ADMIN_PASSWORD);
    await admin.verifyWithModules(orgId, ['Inventory', 'B2BSales']);
    await admin.dispose();
  });

  async function expectNoHorizontalOverflow(page: Page): Promise<void> {
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    expect(overflow).toBeLessThanOrEqual(0);
  }

  test('invoices, orders and stock fit a phone without horizontal scroll', async ({ page }) => {
    await login(page, seller.email, PASSWORD);

    await page.goto('/app/finanzen/rechnungen');
    await expect(page.getByRole('heading', { name: 'Provisionsrechnungen', level: 1 })).toBeVisible();
    await expect(page.getByTestId('invoices-cards')).toBeVisible();
    await expect(page.getByTestId('invoices-table')).toBeHidden();
    await expectNoHorizontalOverflow(page);

    await page.goto('/app/bestellungen/verkauf');
    await expect(page.getByTestId('orders-cards')).toBeVisible();
    await expect(page.getByTestId('orders-table')).toBeHidden();
    await expectNoHorizontalOverflow(page);

    await page.goto('/app/lager');
    await expect(page.getByTestId('stock-cards')).toBeVisible();
    await expect(page.getByTestId('stock-table')).toBeHidden();
    await expectNoHorizontalOverflow(page);

    await page.getByRole('tab', { name: 'Bewegungen' }).click();
    await expect(page.getByTestId('movements-cards')).toBeVisible();
    await expect(page.getByTestId('movements-table')).toBeHidden();
    await expectNoHorizontalOverflow(page);
  });

  test('finance payouts and ledger list as cards', async ({ page }) => {
    await login(page, seller.email, PASSWORD);

    await page.goto('/app/finanzen');
    await expect(page.getByTestId('payouts-cards')).toBeVisible();
    await expect(page.getByTestId('payouts-table')).toBeHidden();
    await expect(page.getByTestId('ledger-cards')).toBeVisible();
    await expect(page.getByTestId('ledger-table')).toBeHidden();
    await expectNoHorizontalOverflow(page);
  });

  test('admin companies and users list as cards', async ({ page }) => {
    await login(page, ADMIN_EMAIL, ADMIN_PASSWORD);

    await page.goto('/app/administration');
    await expect(page.getByTestId('admin-cards')).toBeVisible();
    await expect(page.getByTestId('admin-table')).toBeHidden();
    await expectNoHorizontalOverflow(page);

    await page.goto('/app/administration/benutzer');
    await expect(page.getByTestId('users-cards')).toBeVisible();
    await expect(page.getByTestId('users-table')).toBeHidden();
    await expectNoHorizontalOverflow(page);

    await page.goto('/app/administration/rechnungen');
    await expect(page.getByTestId('admin-invoices-cards')).toBeVisible();
    await expect(page.getByTestId('admin-invoices-table')).toBeHidden();
    await expectNoHorizontalOverflow(page);

    await page.goto('/app/administration/reklamationen');
    await expect(page.getByTestId('admin-disputes-cards')).toBeVisible();
    await expect(page.getByTestId('admin-disputes-table')).toBeHidden();
    await expectNoHorizontalOverflow(page);

    await page.goto('/app/administration/ankauf');
    await expect(page.getByTestId('pl-cards')).toBeVisible();
    await expect(page.getByTestId('pl-table')).toBeHidden();
    await expectNoHorizontalOverflow(page);
  });
});
