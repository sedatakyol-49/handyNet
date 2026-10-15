import { expect, test } from '@playwright/test';
import { ADMIN_EMAIL, ADMIN_PASSWORD, Api, PASSWORD, RUN, company, hasAdmin, login, randomEan } from './support/handynet';

/**
 * POS (Kasse) through the UI against the real API (dev config: fiscal signer "Disabled" → test-mode notice):
 * 1. Setup via API: a shop with the modules Pos + Inventory + ProductImport, a warehouse, a product with an EAN and
 *    5 units received into stock.
 * 2. The shop owner creates a register, opens a session with 100,00 € opening cash, scans the EAN, enters the price
 *    (12,50 €), raises the quantity to 2 and pays 50,00 € cash → receipt no. 1 with 25,00 € change and the fiscal
 *    notice "TSE nicht angebunden – Testbetrieb".
 * 3. Return of one item (cash refund) → receipt no. 2 with −12,50 €.
 * 4. Closing with counted cash = expected (100 + 25 − 12,50 = 112,50 €) → difference 0,00 €; the Z-report shows
 *    receipts 1 to 2, gross 12,50 €, cash 12,50 €, standard taxation 12,50 €.
 */
test.describe.serial('POS', () => {
  test.skip(!hasAdmin, 'Set HN_ADMIN_EMAIL and HN_ADMIN_PASSWORD to run e2e tests against the API.');

  const shop = { ...company('shop'), name: `E2E Kasse ${RUN}`, email: `pos-shop-${RUN}@e2e.handynet.test` };
  const ean = randomEan();
  const product = `Ladekabel USB-C ${RUN}`;
  const register = `Theke ${RUN}`;

  test.beforeAll(async () => {
    const orgId = await Api.register(shop);
    const admin = await Api.login(ADMIN_EMAIL, ADMIN_PASSWORD);
    await admin.verifyWithModules(orgId, ['Inventory', 'ProductImport', 'Pos']);
    await admin.dispose();

    const api = await Api.login(shop.email, PASSWORD);
    const warehouse = await api.call<{ id: string }>('POST', '/api/inventory/warehouses', orgId, { code: 'LADEN', name: 'Ladengeschäft' });
    const created = await api.call<{ id: string }>('POST', '/api/catalog/products', orgId, {
      brand: 'E2E',
      name: product,
      category: 'Accessory',
      ean,
    });
    await api.call('POST', '/api/inventory/adjustments', orgId, {
      warehouseId: warehouse.id,
      productId: created.id,
      delta: 5,
      reason: 'Wareneingang E2E',
      type: 'Receipt',
    }, 200);
    await api.dispose();
  });

  test('sell, return and close the session', async ({ page }) => {
    await login(page, shop.email, PASSWORD);

    // ---- register + session
    await page.getByRole('navigation', { name: 'Hauptnavigation' }).getByRole('link', { name: 'Kasse', exact: true }).click();
    await expect(page).toHaveURL(/\/app\/kasse$/);
    await page.getByTestId('pos-new-name').fill(register);
    await page.getByTestId('pos-create').click();
    const card = page.locator(`[data-register="${register}"]`);
    await expect(card).toBeVisible();
    await card.getByTestId('pos-open-till').click();

    await page.getByTestId('pos-opening').fill('100,00');
    await page.getByTestId('pos-open').click();
    await expect(page.getByTestId('pos-session-banner')).toBeVisible();

    // ---- scan (the code input has the focus, like a scanner typing + Enter)
    await expect(page.getByTestId('pos-code')).toBeFocused();
    await page.keyboard.type(ean);
    await page.keyboard.press('Enter');
    const line = page.getByTestId('pos-cart').locator('tbody tr');
    await expect(line).toHaveCount(1);
    await expect(line).toContainText(product);
    // No price known yet in this browser session → the price input gets the focus.
    await expect(page.getByTestId('pos-price')).toBeFocused();
    await page.keyboard.type('12,50');
    await page.keyboard.press('Enter');
    await expect(page.getByTestId('pos-code')).toBeFocused();
    await page.getByTestId('pos-qty-plus').click();
    await expect(page.getByTestId('pos-qty')).toHaveText('2');
    await expect(page.getByTestId('pos-total')).toHaveText('25,00 €');

    // ---- pay cash with change
    await page.getByTestId('pos-cash').fill('50');
    await expect(page.getByTestId('pos-change')).toHaveText('25,00 €');
    await page.keyboard.press('F9');

    await expect(page.getByTestId('pos-sale-done')).toContainText('25,00 €');
    const receipt = page.getByTestId('receipt');
    await expect(receipt.getByTestId('receipt-number')).toHaveText('1');
    await expect(receipt.getByTestId('receipt-fiscal-notice')).toHaveText('TSE nicht angebunden – Testbetrieb');
    await expect(receipt.getByTestId('receipt-total')).toHaveText('25,00 €');
    await expect(receipt.getByTestId('receipt-change')).toHaveText('25,00 €');
    await expect(receipt.getByTestId('receipt-vat')).toContainText('A 19 %');
    // The cart was reset for the next customer.
    await page.getByTestId('pos-new-sale').click();
    await expect(page.getByTestId('pos-cart-empty')).toBeVisible();
    await expect(page.getByTestId('pos-code')).toBeFocused();

    // ---- return one item
    await page.getByTestId('pos-tab-receipts').click();
    const row = page.locator('[data-receipt="1"]');
    await expect(row).toContainText('25,00 €');
    await row.getByRole('link').click();
    await page.getByTestId('pos-start-return').click();
    await page.getByTestId('pos-return-qty').fill('1');
    await expect(page.getByTestId('pos-refund-total')).toHaveText('12,50 €');
    await page.getByTestId('pos-refund-Cash').check();
    await page.getByTestId('pos-return-reason').fill('Kunde hat sich umentschieden');
    await page.getByTestId('pos-return-submit').click();
    await page.getByTestId('pos-return-confirm').click();

    const refund = page.getByTestId('receipt');
    await expect(refund.getByTestId('receipt-number')).toHaveText('2');
    await expect(refund.getByTestId('receipt-title')).toHaveText('Rückgabe-Beleg');
    await expect(refund.getByTestId('receipt-reference')).toHaveText('1');
    await expect(refund.getByTestId('receipt-total')).toHaveText('-12,50 €');
    await expect(refund.getByTestId('receipt-fiscal-notice')).toHaveText('TSE nicht angebunden – Testbetrieb');

    // ---- close the session with counted = expected
    await page.getByRole('link', { name: 'Sitzung öffnen' }).click();
    await expect(page.getByTestId('z-report')).toContainText('Zwischenstand');
    await expect(page.getByTestId('z-expected')).toHaveText('112,50 €');
    await page.getByTestId('pos-counted').fill('112,50');
    await expect(page.getByTestId('pos-close-difference')).toHaveText('0,00 €');
    await page.getByTestId('pos-close-submit').click();
    await expect(page.getByTestId('pos-confirm-difference')).toHaveText('0,00 €');
    await page.getByTestId('pos-close-confirm').click();

    const z = page.getByTestId('z-report');
    await expect(z).toContainText('Z-Bericht');
    await expect(page.getByTestId('pos-session-closed')).toBeVisible();
    await expect(z.getByTestId('z-difference')).toHaveText('0,00 €');
    await expect(z.getByTestId('z-counted')).toHaveText('112,50 €');
    await expect(z.getByTestId('z-range')).toHaveText('1 bis 2');
    await expect(z.getByTestId('z-total-gross')).toHaveText('12,50 €');
    await expect(z.getByTestId('z-by-payment').locator('[data-method="Cash"]')).toHaveText('12,50 €');
    await expect(z.getByTestId('z-by-tax').locator('[data-treatment="Standard19"]')).toContainText('12,50 €');

    // The till now asks for a new session again.
    await page.getByTestId('pos-tab-till').click();
    await expect(page.getByTestId('pos-open-session')).toBeVisible();
  });
});
