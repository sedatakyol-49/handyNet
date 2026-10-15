import { expect, test } from '@playwright/test';
import { Api, PASSWORD, RUN, company, hasAdmin, login, priceListCsv, randomEan, ADMIN_EMAIL, ADMIN_PASSWORD } from './support/handynet';

/**
 * Login → marketplace browse. Test data (a verified supplier with a committed price list and a
 * verified shop) is created through the real API, then the shop browses the marketplace in the UI.
 */
test.describe('login and marketplace browsing', () => {
  test.skip(!hasAdmin, 'Set HN_ADMIN_EMAIL and HN_ADMIN_PASSWORD to run e2e tests against the API.');

  const supplier = { ...company('supplier'), name: `E2E Browse Lieferant ${RUN}`, email: `browse-supplier-${RUN}@e2e.handynet.test` };
  const shop = { ...company('shop'), name: `E2E Browse Shop ${RUN}`, email: `browse-shop-${RUN}@e2e.handynet.test` };
  const productName = `Galaxy S23 Akku B${RUN}`;

  test.beforeAll(async () => {
    const supplierOrg = await Api.register(supplier);
    const shopOrg = await Api.register(shop);
    const admin = await Api.login(ADMIN_EMAIL, ADMIN_PASSWORD);
    await admin.verifyWithModules(supplierOrg, ['ProductImport', 'Inventory', 'B2BSales']);
    await admin.verifyWithModules(shopOrg, ['B2BPurchasing']);
    await admin.dispose();

    const s = await Api.login(supplier.email, PASSWORD);
    await s.importAndCommit(
      supplierOrg,
      priceListCsv([
        { sku: `BR-${RUN}-1`, ean: randomEan(), name: productName, price: '1.234,50', stock: 7 },
        { sku: `BR-${RUN}-2`, ean: randomEan(), name: `${productName} Pro`, price: '19,99', stock: 0 },
      ]),
    );
    await s.dispose();
  });

  test('shop logs in, sees only its modules and finds offers with net prices', async ({ page }) => {
    await login(page, shop.email, PASSWORD);

    // Navigation shows purchasing, hides modules the shop does not have.
    const nav = page.getByRole('navigation', { name: 'Hauptnavigation' });
    await expect(nav.getByRole('link', { name: 'Marktplatz' })).toBeVisible();
    await expect(nav.getByRole('link', { name: 'Importe' })).toHaveCount(0);
    await expect(nav.getByRole('link', { name: 'Angebote' })).toHaveCount(0);

    await nav.getByRole('link', { name: 'Marktplatz' }).click();
    await expect(page.getByRole('heading', { name: 'Marktplatz' })).toBeVisible();

    // "/" focuses the search box.
    await page.locator('body').click();
    await page.keyboard.press('/');
    await expect(page.getByTestId('marketplace-search')).toBeFocused();
    await page.keyboard.type(productName);

    const results = page.getByTestId('marketplace-results');
    const row = results.getByRole('row').filter({ hasText: productName }).first();
    await expect(row).toBeVisible();
    await expect(row).toContainText(supplier.name);
    await expect(row).toContainText('1.234,50');
    await expect(row).toContainText('Service Pack');
    await expect(page.getByText('netto zzgl. MwSt.').first()).toBeVisible();

    // The out-of-stock variant is hidden by the default "Nur verfügbare" filter …
    await expect(results.getByRole('row').filter({ hasText: `${productName} Pro` })).toHaveCount(0);
    // … and appears when the filter is switched off.
    await page.getByLabel('Nur verfügbare').uncheck();
    await expect(results.getByRole('row').filter({ hasText: `${productName} Pro` })).toBeVisible();

    // Quantity above stock is rejected client-side.
    await page.getByLabel('Nur verfügbare').check();
    const qty = row.getByRole('spinbutton');
    await qty.fill('99');
    await expect(row.getByText('Nur 7 verfügbar.')).toBeVisible();
    await expect(row.getByTestId('add-to-cart')).toBeDisabled();
  });
});
