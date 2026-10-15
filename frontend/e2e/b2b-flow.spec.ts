import { expect, test } from '@playwright/test';
import { ADMIN_EMAIL, ADMIN_PASSWORD, PASSWORD, RUN, company, hasAdmin, login, logout, priceListCsv, randomEan, registerViaUi } from './support/handynet';

/**
 * Complete B2B flow through the UI only:
 * register supplier + shop → admin verifies and enables modules → supplier imports a CSV price list
 * and approves it → shop browses, orders → supplier confirms and ships.
 */
test.describe.serial('B2B flow through the UI', () => {
  test.skip(!hasAdmin, 'Set HN_ADMIN_EMAIL and HN_ADMIN_PASSWORD to run e2e tests against the API.');

  const supplier = company('supplier');
  const shop = company('shop');
  const productName = `iPhone 13 Display E2E${RUN}`;
  let orderNumber = '';

  test('supplier and shop register and wait for verification', async ({ page }) => {
    await registerViaUi(page, supplier);
    await expect(page.getByText('Registrierung erfolgreich')).toBeVisible();
    await login(page, supplier.email, PASSWORD);
    await expect(page.getByText('Ihr Unternehmen wird geprüft')).toBeVisible();
    // No module pages while pending.
    await expect(page.getByRole('navigation', { name: 'Hauptnavigation' }).getByRole('link', { name: 'Importe' })).toHaveCount(0);
    await logout(page);

    await registerViaUi(page, shop);
  });

  test('platform admin verifies both companies and enables modules', async ({ page }) => {
    await login(page, ADMIN_EMAIL, ADMIN_PASSWORD);
    await page.goto('/app/administration');

    for (const [c, modules] of [
      [supplier, ['ProductImport', 'Inventory', 'B2BSales']],
      [shop, ['B2BPurchasing']],
    ] as const) {
      await page.getByTestId('admin-status').selectOption('');
      await page.getByTestId('admin-search').fill(c.name);
      await page.getByTestId('admin-search').press('Enter');
      const row = page.locator(`tr[data-org="${c.name}"]`);
      await expect(row).toBeVisible();
      await row.getByTestId('admin-verify').click();
      await expect(row.getByText('Verifiziert')).toBeVisible();

      await row.getByTestId('admin-modules').click();
      const dialog = page.getByRole('dialog');
      for (const m of modules) await dialog.locator(`input[data-module="${m}"]`).check();
      await dialog.getByTestId('admin-modules-save').click();
      await expect(dialog).toBeHidden();
      for (const label of modules.map((m) => ({ ProductImport: 'Produktimport', Inventory: 'Lager', B2BSales: 'B2B-Verkauf', B2BPurchasing: 'B2B-Einkauf' })[m])) {
        await expect(row.getByText(label, { exact: true })).toBeVisible();
      }
    }
  });

  test('supplier imports a CSV price list and approves it', async ({ page }) => {
    await login(page, supplier.email, PASSWORD);
    const nav = page.getByRole('navigation', { name: 'Hauptnavigation' });
    await nav.getByRole('link', { name: 'Importe' }).click();
    await page.getByTestId('new-import').click();

    const csv = priceListCsv([{ sku: `SKU-1-${RUN}`, ean: randomEan(), name: productName, price: '89,90', stock: 5 }]);
    await page.getByTestId('import-file').setInputFiles({ name: 'preisliste.csv', mimeType: 'text/csv', buffer: Buffer.from(csv, 'utf8') });
    await page.getByTestId('import-upload').click();

    const summary = page.getByTestId('import-summary');
    await expect(summary).toContainText('Gültig');
    await expect(summary.locator('div').filter({ hasText: 'Gültig' }).locator('dd')).toHaveText('1');
    await expect(page.getByTestId('import-rows')).toContainText(productName);
    await expect(page.getByTestId('import-rows')).toContainText('89,90');

    await page.getByTestId('import-commit').click();
    await expect(page.getByTestId('import-status')).toHaveText('Freigegeben');

    // The offer is now listed among the supplier's own offers.
    await nav.getByRole('link', { name: 'Angebote' }).click();
    await expect(page.getByTestId('offers-table')).toContainText(productName);
    await logout(page);
  });

  test('shop finds the offer and places an order', async ({ page }) => {
    await login(page, shop.email, PASSWORD);
    await page.getByRole('navigation', { name: 'Hauptnavigation' }).getByRole('link', { name: 'Marktplatz' }).click();
    await page.getByTestId('marketplace-search').fill(productName);
    const row = page.getByTestId('marketplace-results').getByRole('row').filter({ hasText: productName });
    await expect(row).toContainText(supplier.name);
    await expect(row).toContainText('89,90');

    await row.getByRole('spinbutton').fill('2');
    await row.getByTestId('add-to-cart').click();
    await expect(page.getByTestId('cart-count')).toHaveText('1');

    await page.getByRole('navigation', { name: 'Hauptnavigation' }).getByRole('link', { name: /Warenkorb/ }).click();
    const group = page.getByTestId('cart-group');
    await expect(group).toContainText(supplier.name);
    await expect(group).toContainText('179,80');
    await group.getByTestId('checkout').click();

    const placed = page.getByTestId('placed-order');
    await expect(placed).toContainText('179,80'); // net
    await expect(placed).toContainText('34,16'); // 19 % VAT
    await expect(placed).toContainText('213,96'); // gross
    const heading = await page.getByText(/Bestellung .* an .* übermittelt/).textContent();
    orderNumber = /Bestellung (\S+) an/.exec(heading ?? '')?.[1] ?? '';
    expect(orderNumber).not.toBe('');
    await expect(page.getByTestId('cart-group')).toHaveCount(0);

    await page.getByRole('link', { name: 'Bestellung anzeigen' }).click();
    await expect(page.getByTestId('order-status')).toHaveText('Eingegangen');
    await expect(page.getByTestId('order-gross')).toContainText('213,96');
    await logout(page);
  });

  test('supplier confirms and ships the order', async ({ page }) => {
    await login(page, supplier.email, PASSWORD);
    await page.getByRole('navigation', { name: 'Hauptnavigation' }).getByRole('link', { name: 'Verkauf', exact: true }).click();
    await page.getByTestId('orders-table').getByRole('link', { name: orderNumber }).click();

    await expect(page.getByTestId('order-status')).toHaveText('Eingegangen');
    await page.getByTestId('confirm-order').click();
    await expect(page.getByTestId('order-status')).toHaveText('Bestätigt');
    await page.getByTestId('ship-order').click();
    await expect(page.getByTestId('order-status')).toHaveText('Versendet');

    // Stock was consumed: 5 on hand − 2 shipped.
    await page.getByRole('navigation', { name: 'Hauptnavigation' }).getByRole('link', { name: 'Lager' }).click();
    const stockRow = page.getByTestId('stock-table').getByRole('row').filter({ hasText: productName });
    await expect(stockRow.locator('td').nth(3)).toHaveText('3');
  });
});
