import { expect, test } from '@playwright/test';
import { ADMIN_EMAIL, ADMIN_PASSWORD, Api, PASSWORD, RUN, company, hasAdmin, login, randomEan } from './support/handynet';

/**
 * A sale at the till shows up in the back office under "Rechnungen, Angebote & Belege" -> "Kassenbelege" (all registers),
 * reachable from the other two tabs, with total, payment method and TSE state.
 */
test.describe.serial('Back office: till receipts', () => {
  test.skip(!hasAdmin, 'Set HN_ADMIN_EMAIL and HN_ADMIN_PASSWORD to run e2e tests against the API.');

  const shop = { ...company('shop'), name: `E2E Kassenbelege ${RUN}`, email: `receipts-${RUN}@e2e.handynet.test` };
  const ean = randomEan();
  const registerName = `Kasse ${RUN}`;
  let orgId = '';
  let registerId = '';

  test.beforeAll(async () => {
    orgId = await Api.register(shop);
    const admin = await Api.login(ADMIN_EMAIL, ADMIN_PASSWORD);
    await admin.verifyWithModules(orgId, ['Inventory', 'ProductImport', 'Pos']);
    await admin.dispose();

    const api = await Api.login(shop.email, PASSWORD);
    const warehouse = await api.call<{ id: string }>('POST', '/api/inventory/warehouses', orgId, { code: 'LADEN', name: 'Laden' });
    const created = await api.call<{ id: string }>('POST', '/api/catalog/products', orgId, { brand: 'E2E', name: `Hülle ${RUN}`, category: 'Accessory', ean });
    await api.call('POST', '/api/inventory/adjustments', orgId, { warehouseId: warehouse.id, productId: created.id, delta: 5, reason: 'E2E', type: 'Receipt' }, 200);
    await api.call('PUT', `/api/pos/prices/${created.id}`, orgId, { priceMinor: 990 });
    registerId = (await api.call<{ id: string }>('POST', '/api/pos/registers', orgId, { name: registerName, warehouseId: warehouse.id })).id;
    await api.call('POST', `/api/pos/registers/${registerId}/sessions`, orgId, { openingCashMinor: 0 });
    await api.dispose();
  });

  test('card sale at the till appears in the back office receipts list', async ({ page }) => {
    await login(page, shop.email, PASSWORD);
    await page.goto(`/kasse/${registerId}`);
    await expect(page.getByTestId('pos-code')).toBeFocused();
    await page.keyboard.type(ean);
    await page.keyboard.press('Enter');
    await expect(page.getByTestId('pos-total')).toHaveText('9,90 €');
    await page.getByTestId('pos-pay').click();
    await page.getByTestId('pos-method-Card').click();
    await page.getByTestId('pos-validate').click();
    await expect(page.getByTestId('pos-sale-done')).toBeVisible();

    // The three views are reachable from each other.
    await page.goto('/app/verkauf/rechnungen');
    await page.getByTestId('sales-tab-receipts').click();
    await expect(page).toHaveURL(/\/app\/verkauf\/kassenbelege$/);
    await expect(page.getByTestId('sales-tab-invoices')).toBeVisible();
    await expect(page.getByTestId('sales-tab-quotations')).toBeVisible();

    const row = page.getByTestId('receipts-table').locator('tbody tr').first();
    await expect(row).toContainText('9,90');
    await expect(row).toContainText(registerName);
    await expect(row).toContainText('Karte');
    await expect(row.getByTestId('receipt-tse')).toBeVisible();

    // Filters: another receipt number finds nothing, the right register does.
    await page.getByTestId('receipts-number').fill('999999');
    await page.getByTestId('receipts-apply').click();
    await expect(page.getByTestId('receipts-table')).toContainText('Keine Belege gefunden');
    await page.getByRole('button', { name: 'Zurücksetzen' }).click();
    await page.getByTestId('receipts-register').selectOption({ label: registerName });
    await page.getByTestId('receipts-apply').click();
    await expect(page.getByTestId('receipts-table').locator('tbody tr')).toHaveCount(1);

    // Row opens the receipt detail.
    await page.getByTestId('receipts-table').locator('tbody tr').first().click();
    await expect(page).toHaveURL(/\/kasse\/belege\//);
  });
});
