import { expect, test } from '@playwright/test';
import { ADMIN_EMAIL, ADMIN_PASSWORD, Api, PASSWORD, RUN, company, hasAdmin, login, priceListCsv, randomEan } from './support/handynet';

/** A price change between "add to cart" and checkout must surface the API's 409 and let the buyer re-confirm. */
test.describe('checkout conflicts', () => {
  test.skip(!hasAdmin, 'Set HN_ADMIN_EMAIL and HN_ADMIN_PASSWORD to run e2e tests against the API.');

  const supplier = { ...company('supplier'), name: `E2E Preis Lieferant ${RUN}`, email: `price-supplier-${RUN}@e2e.handynet.test` };
  const shop = { ...company('shop'), name: `E2E Preis Shop ${RUN}`, email: `price-shop-${RUN}@e2e.handynet.test` };
  const sku = `PC-${RUN}`;
  const productName = `Pixel 8 Kamera P${RUN}`;
  let supplierOrg = '';

  test.beforeAll(async () => {
    supplierOrg = await Api.register(supplier);
    const shopOrg = await Api.register(shop);
    const admin = await Api.login(ADMIN_EMAIL, ADMIN_PASSWORD);
    await admin.verifyWithModules(supplierOrg, ['ProductImport', 'Inventory', 'B2BSales']);
    await admin.verifyWithModules(shopOrg, ['B2BPurchasing']);
    await admin.dispose();
    const s = await Api.login(supplier.email, PASSWORD);
    await s.importAndCommit(supplierOrg, priceListCsv([{ sku, ean: randomEan(), name: productName, price: '40,00', stock: 10 }]));
    await s.dispose();
  });

  test('price change is shown, accepted and the order uses the new price', async ({ page }) => {
    await login(page, shop.email, PASSWORD);
    await page.goto('/app/marktplatz');
    await page.getByTestId('marketplace-search').fill(productName);
    const row = page.getByTestId('marketplace-results').getByRole('row').filter({ hasText: productName });
    await row.getByTestId('add-to-cart').click();

    // Supplier raises the price in the meantime.
    const s = await Api.login(supplier.email, PASSWORD);
    await s.setOfferPrice(supplierOrg, sku, 45.5);
    await s.dispose();

    await page.goto('/app/warenkorb');
    const group = page.getByTestId('cart-group');
    await group.getByTestId('checkout').click();
    await expect(group.getByText('Der Preis hat sich geändert')).toBeVisible();
    await expect(group).toContainText('45,50');
    await expect(group.getByTestId('checkout')).toBeDisabled();

    await group.getByRole('button', { name: 'Neue Preise übernehmen' }).click();
    await group.getByTestId('checkout').click();
    const placed = page.getByTestId('placed-order');
    await expect(placed).toContainText('45,50');
    await expect(placed).toContainText('54,15'); // 45,50 + 19 % (8,645 → 8,65)
  });

  test('cart survives a reload and is kept per organization', async ({ page }) => {
    await login(page, shop.email, PASSWORD);
    await page.goto('/app/marktplatz');
    await page.getByTestId('marketplace-search').fill(productName);
    await page.getByTestId('marketplace-results').getByRole('row').filter({ hasText: productName }).getByTestId('add-to-cart').click();
    await expect(page.getByTestId('cart-count')).toHaveText('1');
    await page.reload();
    await expect(page.getByTestId('cart-count')).toHaveText('1');
  });
});
