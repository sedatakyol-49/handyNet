import { expect, test } from '@playwright/test';
import { ADMIN_EMAIL, ADMIN_PASSWORD, Api, PASSWORD, RUN, company, hasAdmin, login, randomEan } from './support/handynet';

/**
 * Loyalty points against the real API: a manager enables points on the settings page (1 point per euro, 0,01 € per
 * point); a sale with a customer earns points (shown after the sale and printed); the next sale redeems them in the
 * payment dialog (limits, maximum), the amount due drops by their value, the reduced amount earns again and the
 * receipt shows the reduction; the
 * customer's history shows earn and redeem, and a manager adjusts the balance.
 */
test.describe('POS loyalty points', () => {
  test.skip(!hasAdmin, 'Set HN_ADMIN_EMAIL and HN_ADMIN_PASSWORD to run e2e tests against the API.');

  const shop = { ...company('shop'), name: `E2E Treuepunkte ${RUN}`, email: `treue-${RUN}@e2e.handynet.test` };
  const product = `Ladegerät Punkte ${RUN}`;
  const customer = `Stammkundin ${RUN}`;
  let orgId = '';
  let registerId = '';
  let customerId = '';

  test.beforeAll(async () => {
    orgId = await Api.register(shop);
    const admin = await Api.login(ADMIN_EMAIL, ADMIN_PASSWORD);
    await admin.verifyWithModules(orgId, ['Inventory', 'ProductImport', 'Pos']);
    await admin.dispose();
    const api = await Api.login(shop.email, PASSWORD);
    const warehouse = await api.call<{ id: string }>('POST', '/api/inventory/warehouses', orgId, { code: 'LADEN', name: 'Laden' });
    const p = await api.call<{ id: string }>('POST', '/api/catalog/products', orgId, { brand: 'E2E', name: product, category: 'Accessory', ean: randomEan() });
    await api.call('POST', '/api/inventory/adjustments', orgId, { warehouseId: warehouse.id, productId: p.id, delta: 10, reason: 'E2E', type: 'Receipt' }, 200);
    await api.call('PUT', `/api/pos/prices/${p.id}`, orgId, { priceMinor: 2490 });
    customerId = (await api.call<{ id: string }>('POST', '/api/pos/customers', orgId, { name: customer })).id;
    registerId = (await api.call<{ id: string }>('POST', '/api/pos/registers', orgId, { name: `Theke ${RUN}`, warehouseId: warehouse.id })).id;
    await api.call('POST', `/api/pos/registers/${registerId}/sessions`, orgId, { openingCashMinor: 0 });
    await api.dispose();
  });

  test('earn in one sale, redeem in the next, history and adjustment', async ({ page }) => {
    await login(page, shop.email, PASSWORD);

    // ---- settings: 1 point per euro, 0,01 € per point, at least 10 points
    await page.goto(`/kasse/${registerId}/treuepunkte`);
    await page.getByTestId('loyalty-enabled').check();
    await page.getByTestId('loyalty-ppe').fill('1');
    await page.getByTestId('loyalty-value').fill('0,01');
    await page.getByTestId('loyalty-min').fill('10');
    await expect(page.getByTestId('loyalty-example')).toContainText('100 Punkte');
    await page.getByTestId('loyalty-save').click();
    await expect(page.getByText('Einstellungen gespeichert.')).toBeVisible();

    // ---- sale 1 with the customer: 2 × 24,90 € = 49,80 € → 49 points
    await page.goto(`/kasse/${registerId}`);
    const tile = page.getByTestId('pos-product').filter({ hasText: product });
    await tile.click();
    await tile.click();
    await page.getByTestId('pos-customer').click();
    await page.locator(`[data-customer="${customer}"]`).getByTestId('pos-cust-choose').click();
    await expect(page.getByTestId('pos-customer-extra')).toContainText('0 P.');
    await page.getByTestId('pos-pay').click();
    await expect(page.getByTestId('pos-loyalty-balance')).toHaveText('0');
    await expect(page.getByTestId('pos-loyalty-earn')).toContainText('49 Punkte');
    await page.getByTestId('pos-cash-exact').click();
    await page.getByTestId('pos-validate').click();
    await expect(page.getByTestId('pos-done-loyalty')).toContainText('49 Punkte gesammelt.');
    await expect(page.getByTestId('pos-done-loyalty')).toContainText('Punktestand: 49');
    await expect(page.getByTestId('receipt')).toContainText('Treuepunkte gesammelt: 49');

    // ---- sale 2: one item, redeem points (minimum, then the maximum)
    await page.getByTestId('pos-new-sale').click();
    await tile.click();
    await page.getByTestId('pos-customer').click();
    await page.locator(`[data-customer="${customer}"]`).getByTestId('pos-cust-choose').click();
    await page.getByTestId('pos-pay').click();
    await expect(page.getByTestId('pos-loyalty-balance')).toHaveText('49');
    await page.getByTestId('pos-loyalty-points').fill('5');
    await page.getByTestId('pos-loyalty-apply').click();
    await expect(page.getByTestId('pos-loyalty')).toContainText('Mindestens 10 Punkte einlösen.');
    await page.getByTestId('pos-loyalty-points').fill('60');
    await page.getByTestId('pos-loyalty-apply').click();
    await expect(page.getByTestId('pos-loyalty')).toContainText('Der Kunde hat nur 49 Punkte.');
    await page.getByTestId('pos-loyalty-max').click();
    await expect(page.getByTestId('pos-loyalty-applied')).toContainText('49 Punkte eingelöst: −0,49 €');
    await expect(page.getByTestId('pos-loyalty-earn')).toContainText('24 Punkte');
    await expect(page.getByTestId('pos-pay-total')).toHaveText('24,41 €');
    await expect(page.getByTestId('pos-pay-total-before')).toHaveText('24,90 €');
    await page.getByTestId('pos-cash-exact').click();
    await expect(page.getByTestId('pos-change')).toHaveText('0,00 €');
    await page.getByTestId('pos-validate').click();
    await expect(page.getByTestId('pos-sale-done')).toBeVisible();
    await expect(page.getByTestId('pos-done-loyalty')).toContainText('49 Punkte eingelöst (−0,49 €).');
    await expect(page.getByTestId('pos-done-loyalty')).toContainText('24 Punkte gesammelt.'); // earned on what was paid
    await expect(page.getByTestId('pos-done-loyalty')).toContainText('Punktestand: 24');
    await expect(page.getByTestId('receipt').getByTestId('receipt-total')).toHaveText('24,41 €');
    await expect(page.getByTestId('receipt').getByTestId('receipt-line-loyalty')).toContainText('-0,49 €');
    await expect(page.getByTestId('receipt')).toContainText('Treuepunkte eingelöst: 49');

    // ---- history in the customer form; a manager adjusts the balance
    await page.getByTestId('pos-new-sale').click();
    await page.getByTestId('pos-customer').click();
    await page.locator(`[data-customer="${customer}"]`).getByTestId('pos-cust-edit').click();
    const history = page.getByTestId('pos-customer-loyalty');
    await expect(history.getByTestId('pos-cl-row')).toHaveCount(3);
    await expect(history).toContainText('Eingelöst');
    await expect(history).toContainText('Gesammelt');
    await history.getByTestId('pos-cl-adjust-points').fill('+15');
    await history.getByTestId('pos-cl-adjust-save').click();
    await expect(history).toContainText('Bitte einen Grund angeben.');
    await history.getByTestId('pos-cl-adjust-reason').fill('Kulanz');
    await history.getByTestId('pos-cl-adjust-save').click();
    await expect(history.getByTestId('pos-cl-points')).toHaveText('39');
    await expect(history.getByTestId('pos-cl-row')).toHaveCount(4);

    const api = await Api.login(shop.email, PASSWORD);
    const account = await api.call<{ points: number; transactions: { items: { type: string }[] } }>('GET', `/api/pos/customers/${customerId}/loyalty`, orgId);
    expect(account.points).toBe(39);
    expect(account.transactions.items.map((t) => t.type).sort()).toEqual(['Adjust', 'Earn', 'Earn', 'Redeem']);
    await api.dispose();
  });
});
