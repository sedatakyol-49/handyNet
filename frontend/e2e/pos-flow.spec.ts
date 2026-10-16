import { expect, test } from '@playwright/test';
import { ADMIN_EMAIL, ADMIN_PASSWORD, Api, PASSWORD, RUN, company, hasAdmin, login, randomEan } from './support/handynet';

/**
 * POS (standalone Kasse app under /kasse) through the UI against the real API (dev config: fiscal signer "Disabled"
 * → test-mode notice):
 * 1. Setup via API: a shop with the modules Pos + Inventory + ProductImport, a warehouse, a product with an EAN and
 *    5 units received into stock.
 * 2. The back office has no POS menu entry; the dashboard launches the full-screen app. The owner creates a register,
 *    opens a session with 100,00 €, scans the EAN, types the price 12,50 € on the numpad display, sets quantity 2 with
 *    the numpad → total 25,00 € (VAT 3,99 €).
 * 3. Parked orders: a second ticket gets the product via its tile; switching tickets and reloading the page keep both.
 * 4. Ticket 1 is paid on the payment screen: cash 50,00 € → receipt no. 1 with 25,00 € change and the fiscal notice.
 * 5. Split payment for ticket 2 (12,50 €): card 10,00 € + cash 5,00 € → receipt no. 2 with 2,50 € change.
 * 6. Return of one item of receipt 1 (cash) → receipt no. 3 with −12,50 €.
 * 7. Closing with counted = expected (100 + 25 + 2,50 − 12,50 = 115,00 €) → difference 0,00 €; Z-report: receipts
 *    1 to 3, 25,00 € gross, cash 15,00 €, card 10,00 €. Old /app/kasse URLs redirect to /kasse.
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

  test('sell with numpad, park orders, split payment, return and close the session', async ({ page }) => {
    await login(page, shop.email, PASSWORD);

    // ---- launched from the dashboard, not from the back-office menu
    await expect(page.getByRole('navigation', { name: 'Hauptnavigation' }).getByRole('link', { name: 'Kasse', exact: true })).toHaveCount(0);
    await page.getByTestId('pos-launch-open').click();
    await expect(page).toHaveURL(/\/kasse$/);
    await expect(page.getByTestId('pos-topbar')).toBeVisible();
    await expect(page.locator('#side-nav')).toHaveCount(0);

    // ---- register + session
    await page.getByTestId('pos-new-name').fill(register);
    await page.getByTestId('pos-create').click();
    const card = page.locator(`[data-register="${register}"]`);
    await expect(card).toBeVisible();
    await card.getByTestId('pos-open-till').click();

    await page.getByTestId('pos-opening').fill('100,00');
    await page.getByTestId('pos-open').click();
    await expect(page.getByTestId('pos-topbar-register')).toHaveText(register);
    await expect(page.getByTestId('pos-topbar-session')).toContainText('Sitzung offen');
    const tile = page.getByTestId('pos-products').locator(`[data-product="${product}"]`);
    await expect(tile).toContainText('Bestand 5');

    // ---- scan (the code input has the focus, like a scanner typing + Enter)
    await expect(page.getByTestId('pos-code')).toBeFocused();
    await page.keyboard.type(ean);
    await page.keyboard.press('Enter');
    const lines = page.getByTestId('pos-line');
    await expect(lines).toHaveCount(1);
    await expect(lines).toContainText(product);
    // No price known yet in this browser session → the numpad display (price mode) gets the focus.
    await expect(page.getByTestId('pos-mode-price')).toHaveAttribute('aria-pressed', 'true');
    await expect(page.getByTestId('pos-numpad-input')).toBeFocused();
    await page.keyboard.type('12,50');
    await page.keyboard.press('Enter');
    await expect(page.getByTestId('pos-code')).toBeFocused();
    await page.getByTestId('pos-mode-quantity').click();
    await page.getByTestId('pos-key-2').click();
    await expect(page.getByTestId('pos-qty')).toHaveText('2');
    await expect(page.getByTestId('pos-total')).toHaveText('25,00 €');
    await expect(page.getByTestId('pos-vat')).toHaveText('3,99 €');
    await expect(page.getByTestId('pos-net')).toHaveText('21,01 €');

    // ---- parked orders: a second ticket, product added by tapping its tile
    await page.getByTestId('pos-order-new').click();
    await expect(page.getByTestId('pos-order-tab')).toHaveCount(2);
    await expect(page.getByTestId('pos-cart-empty')).toBeVisible();
    await tile.click();
    await expect(lines).toHaveCount(1);
    await expect(page.getByTestId('pos-numpad-input')).toBeFocused();
    await page.keyboard.type('12,50');
    await page.keyboard.press('Enter');
    await expect(page.getByTestId('pos-total')).toHaveText('12,50 €');

    await page.locator('[data-testid="pos-order-tab"][data-order="1"]').click();
    await expect(page.getByTestId('pos-total')).toHaveText('25,00 €');
    // Tickets survive a reload (stored per register in this browser).
    await page.reload();
    await expect(page.getByTestId('pos-order-tab')).toHaveCount(2);
    await expect(page.getByTestId('pos-total')).toHaveText('25,00 €');
    await expect(page.locator('[data-testid="pos-order-tab"][data-order="1"]')).toHaveAttribute('aria-pressed', 'true');

    // ---- pay ticket 1 in cash with change
    await expect(page.getByTestId('pos-code')).toBeFocused();
    await page.keyboard.press('F9');
    await expect(page.getByTestId('pos-payment')).toBeVisible();
    await expect(page.getByTestId('pos-remaining')).toHaveText('25,00 €');
    await page.getByTestId('pos-method-Cash').click();
    const amounts = page.getByTestId('pos-pay-amount');
    await expect(amounts).toHaveValue('25,00');
    await amounts.fill('50');
    await expect(page.getByTestId('pos-change')).toHaveText('25,00 €');
    await page.keyboard.press('F9');

    await expect(page.getByTestId('pos-sale-done')).toBeVisible();
    await expect(page.getByTestId('pos-change-due')).toHaveText('25,00 €');
    let receipt = page.getByTestId('receipt');
    await expect(receipt.getByTestId('receipt-number')).toHaveText('1');
    await expect(receipt.getByTestId('receipt-cashier')).not.toBeEmpty();
    await expect(receipt.getByTestId('receipt-fiscal-notice')).toHaveText('TSE nicht angebunden – Testbetrieb');
    await expect(receipt.getByTestId('receipt-total')).toHaveText('25,00 €');
    await expect(receipt.getByTestId('receipt-change')).toHaveText('25,00 €');
    await expect(receipt.getByTestId('receipt-vat')).toContainText('A 19 %');
    await page.getByTestId('pos-new-sale').click();
    await expect(page.getByTestId('pos-cart-empty')).toBeVisible();
    await expect(page.getByTestId('pos-code')).toBeFocused();
    // Ticket 2 is still parked; the sold one is gone.
    await expect(page.locator('[data-testid="pos-order-tab"][data-order="2"]')).toBeVisible();
    await expect(page.locator('[data-testid="pos-order-tab"][data-order="1"]')).toHaveCount(0);
    await expect(tile).toContainText('Bestand 3');

    // ---- split payment for ticket 2: card 10,00 + cash 5,00 → 2,50 change
    await page.locator('[data-testid="pos-order-tab"][data-order="2"]').click();
    await expect(page.getByTestId('pos-total')).toHaveText('12,50 €');
    await page.getByTestId('pos-pay').click();
    await page.getByTestId('pos-method-Card').click();
    await expect(amounts.nth(0)).toHaveValue('12,50');
    await amounts.nth(0).fill('10,00');
    await expect(page.getByTestId('pos-remaining')).toHaveText('2,50 €');
    await page.getByTestId('pos-method-Cash').click();
    await expect(amounts.nth(1)).toHaveValue('2,50');
    await amounts.nth(1).fill('5');
    await expect(page.getByTestId('pos-change')).toHaveText('2,50 €');
    await page.getByTestId('pos-validate').click();

    receipt = page.getByTestId('receipt');
    await expect(receipt.getByTestId('receipt-number')).toHaveText('2');
    await expect(receipt.getByTestId('receipt-total')).toHaveText('12,50 €');
    await expect(receipt.getByTestId('receipt-payments')).toContainText('Karte');
    await expect(receipt.getByTestId('receipt-payments')).toContainText('10,00 €');
    await expect(receipt.getByTestId('receipt-payments')).toContainText('Bar gegeben');
    await expect(receipt.getByTestId('receipt-change')).toHaveText('2,50 €');
    await page.getByTestId('pos-new-sale').click();

    // ---- return one item of receipt 1 (receipts via the top-bar menu)
    await page.getByTestId('pos-menu').click();
    await page.getByTestId('pos-menu-receipts').click();
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
    await expect(refund.getByTestId('receipt-number')).toHaveText('3');
    await expect(refund.getByTestId('receipt-title')).toHaveText('Rückgabe-Beleg');
    await expect(refund.getByTestId('receipt-reference')).toHaveText('1');
    await expect(refund.getByTestId('receipt-total')).toHaveText('-12,50 €');
    await expect(refund.getByTestId('receipt-fiscal-notice')).toHaveText('TSE nicht angebunden – Testbetrieb');

    // ---- close the session with counted = expected (via the top-bar menu)
    await page.getByTestId('pos-menu').click();
    await page.getByTestId('pos-menu-cash').click();
    await expect(page.getByTestId('z-report')).toContainText('Zwischenstand');
    await expect(page.getByTestId('z-expected')).toHaveText('115,00 €');
    await page.getByTestId('pos-counted').fill('115,00');
    await expect(page.getByTestId('pos-close-difference')).toHaveText('0,00 €');
    await page.getByTestId('pos-close-submit').click();
    await expect(page.getByTestId('pos-confirm-difference')).toHaveText('0,00 €');
    await page.getByTestId('pos-close-confirm').click();

    const z = page.getByTestId('z-report');
    await expect(z).toContainText('Z-Bericht');
    await expect(page.getByTestId('pos-session-closed')).toBeVisible();
    await expect(page.getByTestId('pos-topbar-session')).toContainText('Keine offene Sitzung');
    await expect(z.getByTestId('z-difference')).toHaveText('0,00 €');
    await expect(z.getByTestId('z-counted')).toHaveText('115,00 €');
    await expect(z.getByTestId('z-range')).toHaveText('1 bis 3');
    await expect(z.getByTestId('z-opened-by')).toContainText('Geöffnet von');
    await expect(z.getByTestId('z-closed-by')).toContainText('Abgeschlossen von');
    await expect(z.getByTestId('z-total-gross')).toHaveText('25,00 €');
    await expect(z.getByTestId('z-by-payment').locator('[data-method="Cash"]')).toHaveText('15,00 €');
    await expect(z.getByTestId('z-by-payment').locator('[data-method="Card"]')).toHaveText('10,00 €');
    await expect(z.getByTestId('z-by-tax').locator('[data-treatment="Standard19"]')).toContainText('25,00 €');

    // The till now asks for a new session again.
    await page.getByTestId('pos-tab-till').click();
    await expect(page.getByTestId('pos-open-session')).toBeVisible();

    // Old back-office URLs lead to the standalone app; the menu leads back to the back office.
    await page.goto('/app/kasse');
    await expect(page).toHaveURL(/\/kasse$/);
    await expect(page.getByTestId('pos-topbar')).toBeVisible();
    await page.getByTestId('pos-menu').click();
    await page.getByTestId('pos-menu-backoffice').click();
    await expect(page).toHaveURL(/\/app\/uebersicht$/);
  });
});
