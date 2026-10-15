import { expect, test } from '@playwright/test';
import { ADMIN_EMAIL, ADMIN_PASSWORD, Api, PASSWORD, RUN, company, hasAdmin, login, logout, priceListCsv, randomEan } from './support/handynet';

/**
 * Online payment through the Simulated payment provider (dev only, no real money):
 * shop orders and pays (checkout page of the API, "Bezahlen") → order shows "Bezahlt"; the supplier sees gross,
 * commission and payout amount and the open balance on "Finanzen"; supplier confirms and ships (without shipment),
 * shop confirms receipt and accepts → the order is Completed and a background job pays the seller out.
 */
test.describe.serial('online payment and payout', () => {
  test.skip(!hasAdmin, 'Set HN_ADMIN_EMAIL and HN_ADMIN_PASSWORD to run e2e tests against the API.');

  const supplier = { ...company('supplier'), name: `E2E Zahlung Lieferant ${RUN}`, email: `pay-supplier-${RUN}@e2e.handynet.test` };
  const shop = { ...company('shop'), name: `E2E Zahlung Shop ${RUN}`, email: `pay-shop-${RUN}@e2e.handynet.test` };
  const sku = `PAY-${RUN}-1`;
  const productName = `iPhone Akku Zahlung ${RUN}`;
  let order = { id: '', number: '' };
  let sellerShare = '';

  test.beforeAll(async () => {
    const supplierOrg = await Api.register(supplier);
    const shopOrg = await Api.register(shop);
    const admin = await Api.login(ADMIN_EMAIL, ADMIN_PASSWORD);
    await admin.verifyWithModules(supplierOrg, ['ProductImport', 'Inventory', 'B2BSales']);
    await admin.verifyWithModules(shopOrg, ['B2BPurchasing']);
    await admin.dispose();

    const s = await Api.login(supplier.email, PASSWORD);
    await s.importAndCommit(supplierOrg, priceListCsv([{ sku, ean: randomEan(), name: productName, price: '100,00', stock: 5 }]));
    await s.dispose();

    // The marketplace search also matches the supplier SKU.
    const b = await Api.login(shop.email, PASSWORD);
    order = await b.placeOrder(shopOrg, sku, sku, 1);
    await b.dispose();
  });

  test('shop pays the order online via the simulated checkout', async ({ page }) => {
    await login(page, shop.email, PASSWORD);
    await page.goto(`/app/bestellungen/${order.id}`);
    await expect(page.getByTestId('order-status')).toHaveText('Eingegangen');

    const section = page.getByTestId('payment-section');
    await expect(section.getByTestId('no-payment')).toContainText('Online-Zahlung ist optional');
    await expect(section.getByTestId('escrow-note')).toContainText('erst nach Ablauf der Prüffrist');

    // Same tab: the provider's checkout page (served by the API in dev) …
    await section.getByTestId('pay-online').click();
    await page.waitForURL(/\/api\/payments\/simulated\//);
    await expect(page.getByRole('heading', { name: 'Simulierte Zahlung' })).toBeVisible();
    await page.getByRole('button', { name: 'Bezahlen', exact: true }).click();
    // … redirects back to this order in the app.
    await page.waitForURL(new RegExp(`/app/bestellungen/${order.id}$`));
    await expect(section.getByTestId('payment-status')).toHaveText('Bezahlt');
    await expect(section.getByTestId('payment-amount')).toContainText('119,00');
    await expect(section.getByTestId('payment-paid-at')).not.toHaveText('–');
    await expect(section).toContainText('Testzahlung');
    await expect(section.getByTestId('pay-online')).toHaveCount(0);
    await expect(section.getByTestId('resume-payment')).toHaveCount(0);
    // The buyer never sees the commission split.
    await expect(section.getByTestId('seller-share')).toHaveCount(0);
    await logout(page);
  });

  test('supplier sees the payout amount and open balance, then confirms and ships', async ({ page }) => {
    await login(page, supplier.email, PASSWORD);
    await page.goto(`/app/bestellungen/${order.id}`);

    const section = page.getByTestId('payment-section');
    await expect(section.getByTestId('payment-status')).toHaveText('Bezahlt');
    await expect(section.getByTestId('seller-payment')).toContainText('119,00');
    await expect(section.getByTestId('commission-net')).toContainText('-');
    sellerShare = ((await section.getByTestId('seller-share').textContent()) ?? '').trim();
    expect(sellerShare).toMatch(/^\d{1,3},\d{2}\s€$/);
    expect(sellerShare).not.toBe(await section.getByTestId('payment-amount').textContent());
    await expect(section.getByTestId('payout-state')).toHaveAttribute('data-state', 'pending');
    await expect(section.getByTestId('payout-state')).toContainText('Auszahlung nach Abschluss der Bestellung');

    // Finanzen: paid, not yet paid-out sale.
    await page.getByRole('navigation', { name: 'Hauptnavigation' }).getByRole('link', { name: 'Finanzen' }).click();
    await expect(page).toHaveURL(/\/app\/finanzen/);
    await expect(page.getByTestId('open-balance')).toHaveText(sellerShare);
    const credit = page.getByTestId('ledger-table').locator('tbody tr[data-kind="credit"]').first();
    await expect(credit).toContainText('Gutschrift');
    await expect(credit).toContainText('+' + sellerShare);
    await expect(page.getByTestId('payouts-table')).toContainText('Noch keine Auszahlungen');

    await page.goto(`/app/bestellungen/${order.id}`);
    await page.getByTestId('confirm-order').click();
    await expect(page.getByTestId('order-status')).toHaveText('Bestätigt');
    await page.getByTestId('ship-order').click();
    await expect(page.getByTestId('order-status')).toHaveText('Versendet');
    await logout(page);
  });

  test('shop confirms receipt and accepts the goods', async ({ page }) => {
    await login(page, shop.email, PASSWORD);
    await page.goto(`/app/bestellungen/${order.id}`);
    const shipping = page.getByTestId('shipping-section');
    await shipping.getByTestId('confirm-delivery').click();
    await page.getByRole('dialog').getByTestId('confirm-action').click();
    await expect(page.getByTestId('order-status')).toHaveText('Zugestellt');
    await shipping.getByTestId('accept-goods').click();
    await page.getByRole('dialog').getByTestId('confirm-action').click();
    await expect(page.getByTestId('order-status')).toHaveText('Abgeschlossen');
    await expect(page.getByTestId('payment-section').getByTestId('payment-status')).toHaveText('Bezahlt');
    await logout(page);
  });

  test('supplier finances show the payout after completion', async ({ page }) => {
    await login(page, supplier.email, PASSWORD);
    await page.goto('/app/finanzen');

    // Payouts are processed by a background job every ~2 s: refresh until it shows up.
    const payoutRow = page.getByTestId('payouts-table').locator(`tbody tr[data-order-id="${order.id}"]`);
    await expect(async () => {
      await page.getByTestId('finance-refresh').click();
      await expect(payoutRow).toBeVisible({ timeout: 2_000 });
    }).toPass({ timeout: 20_000, intervals: [1_000, 2_000] });
    await expect(payoutRow).toContainText(sellerShare);
    // The payout links to the order by its number (not a truncated id); new ledger entries name the order number too.
    await expect(payoutRow.getByRole('link', { name: `Bestellung ${order.number} öffnen` })).toHaveText(order.number);
    await expect(page.getByTestId('ledger-table')).toContainText(order.number);
    await expect(page.getByTestId('open-balance')).toHaveText(/^\s*0,00\s€\s*$/);
    const debit = page.getByTestId('ledger-table').locator('tbody tr[data-kind="debit"]').first();
    await expect(debit).toContainText('Auszahlung');
    await expect(debit).toContainText('-' + sellerShare);

    // The order shows the payout as well.
    await payoutRow.getByRole('link').click();
    await expect(page).toHaveURL(new RegExp(`/app/bestellungen/${order.id}`));
    const state = page.getByTestId('payment-section').getByTestId('payout-state');
    await expect(state).toHaveAttribute('data-state', 'paidOut');
    await expect(state).toContainText(sellerShare);
  });
});
