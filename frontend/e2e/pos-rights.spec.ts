import { expect, test } from '@playwright/test';
import { ADMIN_EMAIL, ADMIN_PASSWORD, Api, PASSWORD, RUN, company, hasAdmin, login, logout, randomEan } from './support/handynet';

/**
 * Per-cashier POS rights against the real API:
 * 1. Setup via API: shop with Pos + Inventory, product with a POS price of 20,00 € in stock, a register with an open
 *    session, and a staff member.
 * 2. The owner restricts the staff member on the Team page: only "Rabatt geben", at most 10 %.
 * 3. The cashier sells: the price is locked (prefilled, price mode disabled), 15 % discount is refused before payment
 *    with the limit, 10 % sells; cash in/out, closing and price maintenance are not offered.
 */
test.describe.serial('POS cashier rights', () => {
  test.skip(!hasAdmin, 'Set HN_ADMIN_EMAIL and HN_ADMIN_PASSWORD to run e2e tests against the API.');

  const shop = { ...company('shop'), name: `E2E Kassenrechte ${RUN}`, email: `pos-rights-${RUN}@e2e.handynet.test` };
  const staff = { email: `kassierer-${RUN}@e2e.handynet.test`, displayName: `Kassierer ${RUN}` };
  const ean = randomEan();
  const product = `Panzerglas ${RUN}`;
  let registerId = '';

  test.beforeAll(async () => {
    const orgId = await Api.register(shop);
    const admin = await Api.login(ADMIN_EMAIL, ADMIN_PASSWORD);
    await admin.verifyWithModules(orgId, ['Inventory', 'ProductImport', 'Pos']);
    await admin.dispose();

    const api = await Api.login(shop.email, PASSWORD);
    const warehouse = await api.call<{ id: string }>('POST', '/api/inventory/warehouses', orgId, { code: 'LADEN', name: 'Laden' });
    const created = await api.call<{ id: string }>('POST', '/api/catalog/products', orgId, { brand: 'E2E', name: product, category: 'Accessory', ean });
    await api.call('POST', '/api/inventory/adjustments', orgId, { warehouseId: warehouse.id, productId: created.id, delta: 3, reason: 'Wareneingang', type: 'Receipt' }, 200);
    await api.call('PUT', `/api/pos/prices/${created.id}`, orgId, { priceMinor: 2000 });
    const reg = await api.call<{ id: string }>('POST', '/api/pos/registers', orgId, { name: `Kasse ${RUN}`, warehouseId: warehouse.id });
    registerId = reg.id;
    await api.call('POST', `/api/pos/registers/${reg.id}/sessions`, orgId, { openingCashMinor: 5000 });
    await api.call('POST', '/api/organization/staff', orgId, { ...staff, password: PASSWORD, role: 'Staff' }, 200);
    await api.dispose();
  });

  test('owner limits a cashier; the till enforces price lock and discount limit', async ({ page }) => {
    // ---- owner: Team page → Kassenrechte
    await login(page, shop.email, PASSWORD);
    await page.goto('/app/team');
    const cell = page.locator(`[data-staff="${staff.email}"]`);
    await expect(cell).toContainText('Standard');
    await page.locator('tr', { has: cell }).getByTestId('team-pos-edit').click();
    await page.getByTestId('team-pos-defaults').uncheck();
    for (const right of ['refund', 'cashMovement', 'closeSession']) await page.getByTestId(`team-pos-${right}`).uncheck();
    await expect(page.getByTestId('team-pos-discount')).toBeChecked();
    await expect(page.getByTestId('team-pos-priceOverride')).not.toBeChecked();
    await page.getByTestId('team-pos-max').fill('150');
    await page.getByTestId('team-pos-save').click();
    await expect(page.getByText('Bitte eine ganze Zahl von 0 bis 100 eingeben.')).toBeVisible();
    await page.getByTestId('team-pos-max').fill('10');
    await page.getByTestId('team-pos-save').click();
    await expect(cell).toContainText('Rabatt bis 10 %');
    await expect(cell).not.toContainText('Rückgabe');
    await logout(page);

    // ---- cashier at the till
    await login(page, staff.email, PASSWORD);
    await page.goto(`/kasse/${registerId}`);
    await expect(page.getByTestId('pos-code')).toBeFocused();
    await page.keyboard.type(ean);
    await page.keyboard.press('Enter');
    await expect(page.getByTestId('pos-line-total')).toHaveText('20,00 €');
    await expect(page.getByTestId('pos-mode-price')).toBeDisabled();

    await page.getByTestId('pos-mode-discount').click();
    await expect(page.getByTestId('pos-discount-limit')).toContainText('10 %');
    await page.getByTestId('pos-key-1').click();
    await page.getByTestId('pos-key-5').click();
    await expect(page.getByTestId('pos-line-issue')).toContainText('Höchstens 10 % Rabatt erlaubt');
    await page.getByTestId('pos-pay').click();
    await expect(page.getByTestId('pos-checkout-error')).toContainText('Höchstens 10 %');
    await expect(page.getByTestId('pos-payment')).toHaveCount(0);

    await page.getByTestId('pos-numpad-clear').click();
    await page.getByTestId('pos-key-1').click();
    await page.getByTestId('pos-key-0').click();
    await expect(page.getByTestId('pos-line-issue')).toHaveCount(0);
    await expect(page.getByTestId('pos-total')).toHaveText('18,00 €');
    await page.getByTestId('pos-pay').click();
    await page.getByTestId('pos-cash-exact').click();
    await page.getByTestId('pos-validate').click();
    await expect(page.getByTestId('pos-sale-done')).toBeVisible();
    await expect(page.getByTestId('receipt').getByTestId('receipt-total')).toHaveText('18,00 €');

    // ---- no cash in/out, closing, prices or reports for this cashier
    await page.getByTestId('pos-menu').click();
    await expect(page.getByTestId('pos-menu-receipts')).toBeVisible();
    await expect(page.getByTestId('pos-menu-cash')).toHaveCount(0);
    await expect(page.getByTestId('pos-menu-report')).toHaveCount(0);
    await expect(page.getByTestId('pos-menu-prices')).toHaveCount(0);
    await page.getByTestId('pos-menu-receipts').click();
    await page.locator('[data-receipt="1"]').getByRole('link').click();
    await expect(page.getByTestId('pos-detail-reprint')).toBeVisible();
    await expect(page.getByTestId('pos-start-return')).toHaveCount(0);
  });
});
