import { expect, test } from '@playwright/test';
import { ADMIN_EMAIL, ADMIN_PASSWORD, Api, PASSWORD, RUN, company, hasAdmin, login, randomEan } from './support/handynet';

/**
 * Pricelists against the real API: a manager creates a list with an item price on the "Preislisten" page, assigns it to a
 * customer in the customer form; at the till the line re-prices when the customer is attached (list price, regular price
 * struck through), the tile shows the list price, a manual price stays when the customer changes, removing the customer
 * restores the regular price, and the receipt prints "Preisliste: <name>".
 */
test.describe('POS pricelists', () => {
  test.skip(!hasAdmin, 'Set HN_ADMIN_EMAIL and HN_ADMIN_PASSWORD to run e2e tests against the API.');

  const shop = { ...company('shop'), name: `E2E Preislisten ${RUN}`, email: `preisliste-${RUN}@e2e.handynet.test` };
  const product = `Akku Preisliste ${RUN}`;
  const other = `Kabel Preisliste ${RUN}`;
  const customer = `Werkstatt Kunde ${RUN}`;
  const list = `Werkstätten ${RUN}`;
  let registerId = '';

  test.beforeAll(async () => {
    const orgId = await Api.register(shop);
    const admin = await Api.login(ADMIN_EMAIL, ADMIN_PASSWORD);
    await admin.verifyWithModules(orgId, ['Inventory', 'ProductImport', 'Pos']);
    await admin.dispose();
    const api = await Api.login(shop.email, PASSWORD);
    const warehouse = await api.call<{ id: string }>('POST', '/api/inventory/warehouses', orgId, { code: 'LADEN', name: 'Laden' });
    for (const [name, price] of [[product, 4990], [other, 1000]] as const) {
      const p = await api.call<{ id: string }>('POST', '/api/catalog/products', orgId, { brand: 'E2E', name, category: 'Accessory', ean: randomEan() });
      await api.call('POST', '/api/inventory/adjustments', orgId, { warehouseId: warehouse.id, productId: p.id, delta: 10, reason: 'E2E', type: 'Receipt' }, 200);
      await api.call('PUT', `/api/pos/prices/${p.id}`, orgId, { priceMinor: price });
    }
    await api.call('POST', '/api/pos/customers', orgId, { name: customer });
    registerId = (await api.call<{ id: string }>('POST', '/api/pos/registers', orgId, { name: `Theke ${RUN}`, warehouseId: warehouse.id })).id;
    await api.call('POST', `/api/pos/registers/${registerId}/sessions`, orgId, { openingCashMinor: 0 });
    await api.dispose();
  });

  test('list price applied when the customer is attached, manual prices kept, printed on the receipt', async ({ page }) => {
    await login(page, shop.email, PASSWORD);

    // ---- the manager creates the list (10 % on everything else) and an item price of 39,90 €
    await page.goto(`/kasse/${registerId}/preislisten`);
    await page.getByTestId('pos-pricelist-new').click();
    await page.getByTestId('pl-name').fill(list);
    await page.getByTestId('pl-discount').fill('10');
    await page.getByTestId('pl-save').click();
    const card = page.locator(`[data-pricelist="${list}"]`);
    await expect(card.getByTestId('pos-pricelist-state')).toHaveText('Gültig');
    await card.getByTestId('pos-pricelist-open').click();
    await page.getByTestId('pos-pricelist-add').click();
    await page.getByTestId('pl-product-search').fill(product);
    await page.getByTestId('pl-product').filter({ hasText: product }).click();
    await page.getByTestId('pl-item-price-input').fill('39,90');
    await page.getByTestId('pl-item-save').click();
    await expect(page.getByTestId('pl-item')).toHaveCount(1);
    await expect(page.getByTestId('pl-item-price')).toHaveText('39,90 €');

    // ---- at the till: a product at the regular price, then the customer gets the list in the customer form
    await page.goto(`/kasse/${registerId}`);
    const tile = page.getByTestId('pos-product').filter({ hasText: product });
    await expect(tile).toContainText('49,90 €');
    await tile.click();
    await page.getByTestId('pos-product').filter({ hasText: other }).click();
    // a manual price on the second line must survive the customer change
    await page.getByTestId('pos-mode-price').click();
    await page.getByTestId('pos-numpad-input').fill('8,00');
    await expect(page.getByTestId('pos-total')).toHaveText('57,90 €');

    await page.getByTestId('pos-customer').click();
    await page.locator(`[data-customer="${customer}"]`).getByTestId('pos-cust-edit').click();
    await page.getByTestId('pos-cust-pricelistId').selectOption({ label: list });
    await page.getByTestId('pos-cust-save').click();
    await page.locator(`[data-customer="${customer}"]`).getByTestId('pos-cust-choose').click();

    const listLine = page.getByTestId('pos-line').filter({ hasText: product });
    await expect(listLine.getByTestId('pos-line-total')).toHaveText('39,90 €');
    await expect(listLine.getByTestId('pos-line-pricelist')).toContainText(`Preisliste ${list}`);
    await expect(listLine.getByTestId('pos-line-base-price')).toHaveText('49,90 €');
    await expect(page.getByTestId('pos-line').filter({ hasText: other }).getByTestId('pos-line-total')).toHaveText('8,00 €'); // manual price kept
    await expect(page.getByTestId('pos-total')).toHaveText('47,90 €');
    await expect(page.getByTestId('pos-customer-extra')).toContainText(list);
    await expect(tile).toContainText('39,90 €'); // tiles show the customer's prices

    // ---- removing the customer brings the regular price back; attaching again applies the list again
    await page.getByTestId('pos-customer').click();
    await page.getByTestId('pos-cust-detach').click();
    await expect(listLine.getByTestId('pos-line-total')).toHaveText('49,90 €');
    await expect(listLine.getByTestId('pos-line-pricelist')).toHaveCount(0);
    await page.getByTestId('pos-customer').click();
    await page.locator(`[data-customer="${customer}"]`).getByTestId('pos-cust-choose').click();
    await expect(listLine.getByTestId('pos-line-total')).toHaveText('39,90 €');

    // ---- the sale: the API accepts the list price as the till price; the receipt names the list
    await page.getByTestId('pos-pay').click();
    await page.getByTestId('pos-cash-exact').click();
    await page.getByTestId('pos-validate').click();
    await expect(page.getByTestId('pos-sale-done')).toBeVisible();
    await expect(page.getByTestId('receipt')).toContainText(`Preisliste: ${list}`);
    await expect(page.getByTestId('receipt').getByTestId('receipt-total')).toHaveText('47,90 €');
  });
});
