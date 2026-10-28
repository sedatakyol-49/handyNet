import { expect, test, type Page } from '@playwright/test';
import { ADMIN_EMAIL, ADMIN_PASSWORD, Api, PASSWORD, RUN, company, hasAdmin, login, priceListCsv, randomEan } from './support/handynet';
import { ADMIN_ROUTES, TENANT_ROUTES } from './support/routes';

/**
 * Responsive back office: no horizontal page scroll on phone (375) and tablet (820) for every main /app route and every
 * admin tab, the navigation drawer opens/closes on phones, and the shared phone building blocks work (filter toggle,
 * "..." actions menu, language in the user menu).
 */
test.describe.serial('responsive back office', () => {
  test.skip(!hasAdmin, 'Set HN_ADMIN_EMAIL and HN_ADMIN_PASSWORD to run e2e tests against the API.');

  const seller = { ...company('supplier'), name: `E2E Responsive ${RUN}`, email: `rwd-${RUN}@e2e.handynet.test` };

  test.beforeAll(async () => {
    const orgId = await Api.register(seller);
    const admin = await Api.login(ADMIN_EMAIL, ADMIN_PASSWORD);
    await admin.verifyWithModules(orgId, ['Inventory', 'B2BSales', 'B2BPurchasing', 'ProductImport', 'Ankauf', 'Pos']);
    await admin.dispose();
    const own = await Api.login(seller.email, PASSWORD);
    await own.importAndCommit(orgId, priceListCsv([1, 2].map((i) => ({ sku: `RW-${RUN}-${i}`, ean: randomEan(), name: `Display Responsive ${i}`, price: '39,90', stock: 4 + i }))));
    await own.dispose();
  });

  async function overflow(page: Page): Promise<number> {
    return page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  }

  async function expectFits(page: Page, routes: string[], width: number): Promise<void> {
    await page.setViewportSize({ width, height: width < 600 ? 812 : 1180 });
    for (const route of routes) {
      await page.goto(route);
      await page.waitForLoadState('networkidle');
      await expect(page.getByRole('heading', { level: 1 }).first(), route).toBeVisible();
      expect(await overflow(page), `${route} at ${width}px`).toBeLessThanOrEqual(0);
    }
  }

  test('tenant pages fit a phone (375) and a tablet (820)', async ({ page }) => {
    await login(page, seller.email, PASSWORD);
    await expectFits(page, TENANT_ROUTES, 375);
    await expectFits(page, TENANT_ROUTES, 820);
  });

  test('admin tabs fit a phone (375) and a tablet (820)', async ({ page }) => {
    await login(page, ADMIN_EMAIL, ADMIN_PASSWORD);
    await expectFits(page, ADMIN_ROUTES, 375);
    await expectFits(page, ADMIN_ROUTES, 820);
  });

  test('phone: navigation drawer opens and closes, top bar stays compact', async ({ page }) => {
    await page.setViewportSize({ width: 375, height: 812 });
    await login(page, seller.email, PASSWORD);
    await page.goto('/app/uebersicht');
    const nav = page.getByTestId('side-nav');
    await expect(nav).toBeHidden();

    await page.getByTestId('nav-toggle').click();
    await expect(nav).toBeVisible();
    await expect(nav.getByRole('link', { name: 'Lager', exact: true })).toBeVisible();
    // Escape and the backdrop close it; a navigation closes it as well.
    await page.keyboard.press('Escape');
    await expect(nav).toBeHidden();

    await page.getByTestId('nav-toggle').click();
    await expect(nav).toBeVisible();
    await nav.getByRole('link', { name: 'Lager', exact: true }).click();
    await expect(page).toHaveURL(/\/app\/lager$/);
    await expect(nav).toBeHidden();

    // Top bar: everything within the viewport, language lives in the user menu.
    const header = page.locator('header').first();
    const box = await header.boundingBox();
    expect(box!.height).toBeLessThanOrEqual(60);
    await expect(page.locator('#lang-switcher')).toBeHidden();
    await page.getByTestId('user-menu').click();
    await expect(page.getByTestId('lang-switcher-menu')).toBeVisible();
    expect(await overflow(page)).toBeLessThanOrEqual(0);
  });

  test('phone: secondary actions in a "..." menu, filters behind a toggle, touch targets', async ({ page }) => {
    await page.setViewportSize({ width: 375, height: 812 });
    await login(page, seller.email, PASSWORD);

    await page.goto('/app/finanzen');
    await expect(page.getByTestId('finance-refresh')).toBeHidden();
    await page.getByTestId('actions-menu-toggle').click();
    await expect(page.getByTestId('finance-refresh')).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(page.getByTestId('finance-refresh')).toBeHidden();

    await page.goto('/app/angebote');
    await expect(page.getByRole('searchbox').first()).toBeHidden();
    await page.getByTestId('filter-toggle').click();
    await expect(page.getByRole('searchbox').first()).toBeVisible();

    // Inputs are at least 16 px (no zoom on focus) and tall enough to tap.
    const field = await page.getByRole('searchbox').first().evaluate((el) => {
      const cs = getComputedStyle(el);
      return { size: parseFloat(cs.fontSize), height: el.getBoundingClientRect().height };
    });
    expect(field.size).toBeGreaterThanOrEqual(16);
    expect(field.height).toBeGreaterThanOrEqual(44);
  });

  test('desktop: actions and filters stay inline', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await login(page, seller.email, PASSWORD);
    await page.goto('/app/finanzen');
    await expect(page.getByTestId('actions-menu-toggle')).toBeHidden();
    await expect(page.getByTestId('finance-refresh')).toBeVisible();
    await page.goto('/app/angebote');
    await expect(page.getByTestId('filter-toggle')).toBeHidden();
    await expect(page.getByRole('searchbox').first()).toBeVisible();
  });

  test('phone: moderation lists sellers and offers as cards with bulk select', async ({ page }) => {
    await page.setViewportSize({ width: 375, height: 812 });
    await login(page, ADMIN_EMAIL, ADMIN_PASSWORD);
    await page.goto('/app/administration/marktplatz');
    await expect(page.getByTestId('mod-sellers-cards')).toBeVisible();
    await expect(page.getByTestId('mod-sellers-table')).toBeHidden();
    await expect(page.getByTestId('mod-offers-cards')).toBeVisible();
    await expect(page.getByTestId('mod-offers-table')).toBeHidden();
    const first = page.getByTestId('mod-card-select').first();
    await first.check();
    await expect(page.getByTestId('mod-selected-count')).toContainText('1');
    await first.uncheck();
  });
});
