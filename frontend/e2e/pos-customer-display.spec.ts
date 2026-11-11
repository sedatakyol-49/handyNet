import { expect, test } from '@playwright/test';
import { ADMIN_EMAIL, ADMIN_PASSWORD, Api, PASSWORD, RUN, company, hasAdmin, login, randomEan } from './support/handynet';

/**
 * Local customer display against the real API: a second window of the same browser (`/kasse/:id/kundendisplay`) follows
 * the till over BroadcastChannel — idle, cart with total and discount, payment with tendered/change, thank-you screen
 * with change and the e-receipt QR — and never shows the attached customer or a line note. It also follows the till's
 * language (Arabic → right-to-left) and goes back to the welcome screen when the cashier leaves the selling screen.
 */
test.describe('POS customer display (local)', () => {
  test.skip(!hasAdmin, 'Set HN_ADMIN_EMAIL and HN_ADMIN_PASSWORD to run e2e tests against the API.');

  const shop = { ...company('shop'), name: `E2E Displayshop ${RUN}`, email: `display-${RUN}@e2e.handynet.test` };
  const product = `Panzerglas Display ${RUN}`;
  const customerName = `Kundin Geheim ${RUN}`;
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
    await api.call('PUT', `/api/pos/prices/${created.id}`, orgId, { priceMinor: 1250 });
    await api.call('POST', '/api/pos/customers', orgId, { name: customerName, email: `geheim-${RUN}@e2e.handynet.test`, phone: '0170 1234567' });
    registerId = (await api.call<{ id: string }>('POST', '/api/pos/registers', orgId, { name: `Theke ${RUN}`, warehouseId: warehouse.id })).id;
    await api.call('POST', `/api/pos/registers/${registerId}/sessions`, orgId, { openingCashMinor: 10000 });
    await api.dispose();
  });

  test('the display window follows cart, payment and receipt of the till', async ({ page, context }) => {
    await login(page, shop.email, PASSWORD);
    await page.goto(`/kasse/${registerId}`);
    const tile = page.getByTestId('pos-product').filter({ hasText: product });
    await expect(tile).toBeVisible();

    // The till menu opens the display as a second window of the same browser.
    await page.getByTestId('pos-menu').click();
    await page.getByTestId('pos-menu-display').click();
    const [display] = await Promise.all([context.waitForEvent('page'), page.getByTestId('pos-display-open-local').click()]);
    await display.waitForLoadState();
    await expect(display).toHaveURL(new RegExp(`/kasse/${registerId}/kundendisplay$`));
    await page.getByTestId('pos-display-dialog').getByRole('button', { name: 'Schließen' }).first().click();

    const view = display.getByTestId('customer-display');
    await expect(view).toHaveAttribute('data-phase', 'idle');
    await expect(display.getByTestId('cd-idle')).toContainText('Willkommen');
    await expect(display.getByTestId('pos-topbar')).toHaveCount(0); // full screen, no till chrome

    // ---- cart: two items, then a 10 % discount and a note and a customer that must stay private
    await tile.click();
    await tile.click();
    await expect(view).toHaveAttribute('data-phase', 'cart');
    await expect(display.getByTestId('cd-line')).toHaveCount(1);
    await expect(display.getByTestId('cd-line')).toContainText(product);
    await expect(display.getByTestId('cd-line')).toContainText('2 × 12,50 €');
    await expect(display.getByTestId('cd-total')).toHaveText('25,00 €');

    await page.getByTestId('pos-mode-discount').click();
    await page.getByTestId('pos-numpad-input').fill('10');
    await expect(page.getByTestId('pos-total')).toHaveText('22,50 €');
    await expect(display.getByTestId('cd-total')).toHaveText('22,50 €');
    await expect(display.getByTestId('cd-line-discount')).toHaveText('-2,50 €');
    await expect(display.getByTestId('cd-discount-total')).toHaveText('-2,50 €');

    await page.getByTestId('pos-note').click();
    await page.getByTestId('pos-note-text').fill('Geheime Notiz');
    await page.getByTestId('pos-note-save').click();
    await page.getByTestId('pos-customer').click();
    await page.getByText(customerName).first().click();
    await expect(page.getByTestId('pos-customer-name')).toHaveText(customerName);
    await expect(view).not.toContainText('Geheime Notiz');
    await expect(view).not.toContainText(customerName);

    // ---- payment: due amount, tendered cash and change
    await page.getByTestId('pos-pay').click();
    await expect(view).toHaveAttribute('data-phase', 'payment');
    await expect(display.getByTestId('cd-total')).toHaveText('22,50 €');
    await expect(display.getByTestId('cd-tendered')).toHaveText('0,00 €');
    await page.getByTestId('pos-method-Cash').click();
    await page.getByTestId('pos-pay-amount').fill('20');
    await expect(display.getByTestId('cd-tendered')).toHaveText('20,00 €');
    await expect(display.getByTestId('cd-remaining')).toHaveText('2,50 €');
    await page.getByTestId('pos-pay-amount').fill('50');
    await expect(display.getByTestId('cd-change')).toHaveText('27,50 €');
    await expect(display.getByTestId('cd-payment')).toContainText('Bar');
    await expect(view).not.toContainText(customerName);

    // ---- receipt: thank-you screen with total and change; the QR appears once the cashier creates the public link
    await page.getByTestId('pos-validate').click();
    await expect(page.getByTestId('pos-sale-done')).toBeVisible();
    await expect(view).toHaveAttribute('data-phase', 'done');
    await expect(display.getByTestId('cd-done')).toContainText('Vielen Dank für Ihren Einkauf!');
    await expect(display.getByTestId('cd-total')).toHaveText('22,50 €');
    await expect(display.getByTestId('cd-change')).toHaveText('27,50 €');
    await expect(display.getByTestId('cd-receipt-qr')).toHaveCount(0);
    await page.getByTestId('pos-ereceipt-show-qr').click();
    await expect(display.getByTestId('cd-receipt-qr')).toBeVisible();
    await expect(view).not.toContainText(customerName);

    // ---- next order: idle again
    await page.getByTestId('pos-new-sale').click();
    await expect(view).toHaveAttribute('data-phase', 'idle');

    // ---- a display opened later asks the till for the current state
    await tile.click();
    await expect(display.getByTestId('cd-total')).toHaveText('12,50 €');
    await display.reload();
    await expect(display.getByTestId('customer-display')).toHaveAttribute('data-phase', 'cart');
    await expect(display.getByTestId('cd-total')).toHaveText('12,50 €');

    // ---- the display follows the till's language: Arabic renders right-to-left
    await page.getByTestId('pos-menu').click();
    await page.locator('#pos-lang').selectOption('ar');
    await page.keyboard.press('Escape');
    await expect(display.locator('html')).toHaveAttribute('dir', 'rtl');
    await expect(display.getByTestId('cd-total')).toHaveText('12,50 €');
    await page.getByTestId('pos-menu').click();
    await page.locator('#pos-lang').selectOption('de');
    await page.keyboard.press('Escape');
    await expect(display.locator('html')).toHaveAttribute('dir', 'ltr');

    // ---- leaving the selling screen inside the app: welcome screen; coming back shows the parked cart again
    await page.getByTestId('pos-menu').click();
    await page.getByTestId('pos-menu-receipts').click();
    await expect(page).toHaveURL(/\/belege/);
    await expect(display.getByTestId('customer-display')).toHaveAttribute('data-phase', 'idle');
    await page.getByTestId('pos-tab-till').click();
    await expect(display.getByTestId('cd-total')).toHaveText('12,50 €');

    // ---- leaving by a full page load (or closing the tab): welcome screen as well
    await page.goto('/kasse');
    await expect(display.getByTestId('customer-display')).toHaveAttribute('data-phase', 'idle');
  });
});
