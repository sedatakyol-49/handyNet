import { Page, expect, test } from '@playwright/test';
import { ADMIN_EMAIL, ADMIN_PASSWORD, Api, PASSWORD, RUN, company, hasAdmin, login, randomEan } from './support/handynet';

/**
 * POS responsive layout against the real API (phone 375 × 812 and tablet portrait 820 × 1180):
 * - no page and no POS main area scrolls horizontally on the till, receipts, sessions, prices, printers and register
 *   pages; the register tabs stay on one row (they scroll sideways);
 * - phone: the product grid fills the screen with a bottom bar (item count, total, cart, pay); the cart opens as a
 *   full-height sheet, dialogs are full-screen, numpad keys are ≥ 48 px and touch targets ≥ 44 px; a card sale is
 *   completed with the validate button below the payment area; Arabic renders right-to-left without overflow;
 * - tablet portrait: products and cart side by side (no bottom bar), a card sale through the regular pay button.
 */
test.describe.serial('POS responsive', () => {
  test.skip(!hasAdmin, 'Set HN_ADMIN_EMAIL and HN_ADMIN_PASSWORD to run e2e tests against the API.');

  const shop = { ...company('shop'), name: `E2E Kasse Mobil ${RUN}`, email: `pos-mobil-${RUN}@e2e.handynet.test` };
  const product = `Hülle Mobil ${RUN}`;
  let registerId = '';

  test.beforeAll(async () => {
    const orgId = await Api.register(shop);
    const admin = await Api.login(ADMIN_EMAIL, ADMIN_PASSWORD);
    await admin.verifyWithModules(orgId, ['Inventory', 'ProductImport', 'Pos']);
    await admin.dispose();
    const api = await Api.login(shop.email, PASSWORD);
    const warehouse = await api.call<{ id: string }>('POST', '/api/inventory/warehouses', orgId, { code: 'LADEN', name: 'Laden' });
    const created = await api.call<{ id: string }>('POST', '/api/catalog/products', orgId, { brand: 'E2E', name: product, category: 'Accessory', ean: randomEan() });
    await api.call('POST', '/api/inventory/adjustments', orgId, { warehouseId: warehouse.id, productId: created.id, delta: 10, reason: 'E2E', type: 'Receipt' }, 200);
    await api.call('PUT', `/api/pos/prices/${created.id}`, orgId, { priceMinor: 1490 });
    registerId = (await api.call<{ id: string }>('POST', '/api/pos/registers', orgId, { name: `Theke ${RUN}`, warehouseId: warehouse.id })).id;
    await api.call('POST', `/api/pos/registers/${registerId}/sessions`, orgId, { openingCashMinor: 0 });
    await api.dispose();
  });

  async function expectNoHorizontalOverflow(page: Page): Promise<void> {
    const overflow = await page.evaluate(() => {
      const d = document.documentElement;
      const main = document.getElementById('pos-main');
      return Math.max(d.scrollWidth - d.clientWidth, main ? main.scrollWidth - main.clientWidth : 0);
    });
    expect(overflow).toBeLessThanOrEqual(0);
  }

  async function expectTabsOnOneRow(page: Page): Promise<void> {
    const tops = await page.getByTestId('pos-subnav').locator('a').evaluateAll((links) => links.map((a) => Math.round(a.getBoundingClientRect().top)));
    expect(tops.length).toBeGreaterThan(3);
    expect(new Set(tops).size).toBe(1);
  }

  async function expectMinHeight(page: Page, testId: string, min: number): Promise<void> {
    const box = await page.getByTestId(testId).first().boundingBox();
    expect(box, testId).not.toBeNull();
    expect(box!.height, testId).toBeGreaterThanOrEqual(min);
  }

  async function checkListPages(page: Page): Promise<void> {
    for (const [path, testId] of [
      ['belege', 'pos-receipts-filter'],
      ['sitzungen', 'pos-tab-sessions'],
      ['preise', 'pos-prices-search'],
      ['drucker', 'pos-printer-device'],
    ] as const) {
      await page.goto(`/kasse/${registerId}/${path}`);
      await expect(page.getByTestId(testId)).toBeAttached();
      await expect(page.getByTestId('pos-subnav')).toBeVisible();
      await expectTabsOnOneRow(page);
      await expectNoHorizontalOverflow(page);
    }
    await page.goto('/kasse');
    await expect(page.getByTestId('pos-topbar')).toBeVisible();
    await expectNoHorizontalOverflow(page);
  }

  test('phone: product grid with bottom bar, cart sheet, full-screen dialogs, pay, lists and RTL', async ({ page }) => {
    await page.setViewportSize({ width: 375, height: 812 });
    await login(page, shop.email, PASSWORD);
    await page.goto(`/kasse/${registerId}`);
    const tile = page.getByTestId('pos-product').filter({ hasText: product });
    await expect(tile).toBeVisible();
    await expect(page.getByTestId('pos-bottom-bar')).toBeVisible();
    await expect(page.getByTestId('pos-cart-pane')).toBeHidden();
    await expect(page.getByTestId('pos-bar-pay')).toBeDisabled();
    await expectNoHorizontalOverflow(page);

    // add a product: the bar shows count and total; the cart stays closed (Odoo-style)
    await tile.click();
    await tile.click();
    await expect(page.getByTestId('pos-bar-count')).toHaveText('2');
    await expect(page.getByTestId('pos-bar-total')).toHaveText('29,80 €');
    await expectMinHeight(page, 'pos-pane-cart', 44);
    await expectMinHeight(page, 'pos-bar-pay', 44);

    // cart sheet: full height over the products, numpad keys ≥ 48 px, closes back to the grid
    await page.getByTestId('pos-pane-cart').click();
    const sheet = page.getByTestId('pos-cart-pane');
    await expect(sheet).toBeVisible();
    const sheetBox = (await sheet.boundingBox())!;
    expect(sheetBox.width).toBeGreaterThanOrEqual(374);
    expect(sheetBox.height).toBeGreaterThanOrEqual(800);
    await expect(sheet.getByTestId('pos-total')).toHaveText('29,80 €');
    await expect(sheet.getByTestId('pos-pay')).toBeInViewport();
    await expectMinHeight(page, 'pos-key-7', 48);
    await sheet.getByTestId('pos-numpad-minus').click();
    await expect(sheet.getByTestId('pos-total')).toHaveText('14,90 €');
    await expectNoHorizontalOverflow(page);

    // dialogs are full-screen sheets on phones
    await sheet.getByTestId('pos-customer').click();
    const dialog = page.locator('dialog[open]');
    await expect(dialog).toBeVisible();
    const dialogBox = (await dialog.boundingBox())!;
    expect(dialogBox.width).toBeGreaterThanOrEqual(374);
    expect(dialogBox.height).toBeGreaterThanOrEqual(800);
    expect((await dialog.getByTestId('dialog-close').boundingBox())!.height).toBeGreaterThanOrEqual(44);
    await dialog.getByTestId('dialog-close').click();
    await expect(dialog).toHaveCount(0);

    await page.getByTestId('pos-pane-products').click();
    await expect(sheet).toBeHidden();
    await expect(page.getByTestId('pos-pane-cart')).toBeFocused();
    await expect(page.getByTestId('pos-bar-count')).toHaveText('1');

    // pay from the bottom bar; the validate button stays visible below the scrolling payment area
    await page.getByTestId('pos-bar-pay').click();
    await expect(page.getByTestId('pos-payment')).toBeVisible();
    await page.getByTestId('pos-method-Card').click();
    await expect(page.getByTestId('pos-validate')).toBeHidden();
    await expect(page.getByTestId('pos-validate-bar')).toBeInViewport();
    await expectNoHorizontalOverflow(page);
    await page.getByTestId('pos-validate-bar').click();
    await expect(page.getByTestId('pos-sale-done')).toBeVisible();
    await expectNoHorizontalOverflow(page);
    await page.getByTestId('pos-new-sale').click();
    await expect(page.getByTestId('pos-bar-count')).toHaveText('0');

    // register tabs, lists and the register selection fit the phone
    await checkListPages(page);
    await page.goto(`/kasse/${registerId}/belege`);
    await expect(page.getByTestId('pos-receipts-cards')).toBeVisible();
    await expect(page.getByTestId('pos-receipts')).toBeHidden();
    await expect(page.getByTestId('pos-receipts-filter')).toBeHidden();
    await page.getByTestId('pos-receipts-filter-toggle').click();
    await expect(page.getByTestId('pos-receipts-filter')).toBeVisible();
    await page.goto(`/kasse/${registerId}/preise`);
    await expect(page.getByTestId('pos-prices-cards')).toContainText(product);
    await expect(page.getByTestId('pos-prices')).toBeHidden();

    // Arabic: right-to-left, still no horizontal overflow on the till
    await page.goto(`/kasse/${registerId}`);
    await page.getByTestId('pos-menu').click();
    await page.locator('#pos-lang').selectOption('ar');
    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
    await page.keyboard.press('Escape');
    await expect(page.getByTestId('pos-bottom-bar')).toBeVisible();
    await page.getByTestId('pos-product').filter({ hasText: product }).click();
    await expect(page.getByTestId('pos-bar-count')).toHaveText('1');
    await expectNoHorizontalOverflow(page);
    await page.getByTestId('pos-pane-cart').click();
    await expect(page.getByTestId('pos-cart-pane')).toBeVisible();
    await expectNoHorizontalOverflow(page);
    await page.getByTestId('pos-pane-products').click();
    await page.getByTestId('pos-menu').click();
    await page.locator('#pos-lang').selectOption('de');
    await expect(page.locator('html')).toHaveAttribute('dir', 'ltr');
  });

  test('tablet portrait: products and cart side by side, pay, lists', async ({ page }) => {
    await page.setViewportSize({ width: 820, height: 1180 });
    await login(page, shop.email, PASSWORD);
    await page.goto(`/kasse/${registerId}`);
    await expect(page.getByTestId('pos-bottom-bar')).toBeHidden();
    await expect(page.getByTestId('pos-cart-pane')).toBeVisible();
    const tile = page.getByTestId('pos-product').filter({ hasText: product });
    await tile.click();
    await expect(page.getByTestId('pos-line')).toHaveCount(1);
    await expect(page.getByTestId('pos-pay')).toBeInViewport();
    await expectMinHeight(page, 'pos-key-7', 48);
    await expectNoHorizontalOverflow(page);

    await page.getByTestId('pos-pay').click();
    await page.getByTestId('pos-method-Card').click();
    await expect(page.getByTestId('pos-pay-footer')).toBeHidden();
    await expect(page.getByTestId('pos-validate')).toBeVisible();
    await expectNoHorizontalOverflow(page);
    await page.getByTestId('pos-validate').click();
    await expect(page.getByTestId('pos-sale-done')).toBeVisible();

    await checkListPages(page);
    await page.goto(`/kasse/${registerId}/belege`);
    await expect(page.getByTestId('pos-receipts')).toBeVisible();
    await expect(page.getByTestId('pos-receipts-filter')).toBeVisible();
  });
});
