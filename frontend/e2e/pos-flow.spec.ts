import { Locator, Page, expect, test } from '@playwright/test';
import { ADMIN_EMAIL, ADMIN_PASSWORD, Api, PASSWORD, RUN, company, hasAdmin, login, logout, randomEan } from './support/handynet';

/**
 * POS (standalone Kasse app under /kasse) through the UI against the real API (dev config: fiscal signer "Disabled"
 * → test-mode notice):
 * 1. Setup via API: a shop with the modules Pos + Inventory + ProductImport, a warehouse, a product with an EAN, 5 units
 *    received into stock and an own POS price of 12,50 €.
 * 2. The back office has no POS menu entry; the dashboard launches the full-screen app. The owner creates a register and
 *    opens the session by counting denominations (1 × 50 €, 2 × 20 €, 1 × 10 € = 100,00 €). The tile shows the price,
 *    the scan prefills it; quantity 2 via the numpad → 25,00 € (VAT 3,99 €); a line note and a newly created customer are
 *    attached to the ticket.
 * 3. Parked orders: a second ticket gets the product via its tile; switching tickets and reloading keep both (with the
 *    customer).
 * 4. Ticket 1 is paid in cash (50,00 €) → receipt no. 1 with 25,00 € change, the fiscal notice, the line note and the
 *    customer.
 * 5. Split payment for ticket 2 (12,50 €): card 10,00 € + cash 5,00 € → receipt no. 2 with 2,50 € change.
 * 6. Order history: filter by receipt number and by customer; receipt 1 is reprinted → "Kopie 1", 1 copy recorded.
 * 7. Return of one item of receipt 1 (cash) → receipt no. 3 with −12,50 €.
 * 8. Closing counted by denomination (1 × 100 €, 1 × 10 €, 1 × 5 € = expected 115,00 €) → difference 0,00 €; Z-report
 *    with both counts; the session report shows 25,00 € gross, cash 15,00 €, card 10,00 €. Old /app/kasse URLs redirect.
 */
test.describe.serial('POS', () => {
  test.skip(!hasAdmin, 'Set HN_ADMIN_EMAIL and HN_ADMIN_PASSWORD to run e2e tests against the API.');

  const shop = { ...company('shop'), name: `E2E Kasse ${RUN}`, email: `pos-shop-${RUN}@e2e.handynet.test` };
  const ean = randomEan();
  const product = `Ladekabel USB-C ${RUN}`;
  const register = `Theke ${RUN}`;
  const customer = `Erika Muster ${RUN}`;
  const note = 'Displayschutz montiert';

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
    await api.call('PUT', `/api/pos/prices/${created.id}`, orgId, { priceMinor: 1250 });
    await api.dispose();
  });

  test('sell with prices, notes and a customer, park orders, split payment, reprint, return, count and report', async ({ page }) => {
    // Printing is not part of the test (the browser dialog would block).
    await page.addInitScript(() => {
      window.print = () => undefined;
    });
    await login(page, shop.email, PASSWORD);

    // ---- launched from the dashboard, not from the back-office menu
    await expect(page.getByRole('navigation', { name: 'Hauptnavigation' }).getByRole('link', { name: 'Kasse', exact: true })).toHaveCount(0);
    await page.getByTestId('pos-launch-open').click();
    await expect(page).toHaveURL(/\/kasse$/);
    await expect(page.getByTestId('pos-topbar')).toBeVisible();
    await expect(page.locator('#side-nav')).toHaveCount(0);

    // ---- register + session opened by counting denominations
    await page.getByTestId('pos-new-name').fill(register);
    await page.getByTestId('pos-create').click();
    const card = page.locator(`[data-register="${register}"]`);
    await expect(card).toBeVisible();
    await card.getByTestId('pos-open-till').click();

    await page.getByTestId('pos-opening-mode-count').click();
    const opening = page.getByTestId('pos-opening-count');
    await opening.locator('[data-denomination="5000"]').getByTestId('pos-count-plus').click();
    await opening.locator('[data-denomination="2000"]').getByTestId('pos-count-input').fill('2');
    await opening.locator('[data-denomination="1000"]').getByTestId('pos-count-plus').click();
    await expect(page.getByTestId('pos-count-total')).toHaveText('100,00 €');
    await page.getByTestId('pos-open').click();
    await expect(page.getByTestId('pos-topbar-register')).toHaveText(register);
    await expect(page.getByTestId('pos-topbar-session')).toContainText('Sitzung offen');

    // ---- the tile shows the POS price and the stock; category chip with count
    const tile = page.getByTestId('pos-products').locator(`[data-product="${product}"]`);
    await expect(tile).toContainText('Bestand 5');
    await expect(tile.getByTestId('pos-product-price')).toHaveText('12,50 €');
    await expect(page.getByTestId('pos-category-Accessory')).toContainText('1');

    // ---- scan: the price is prefilled from the API, the scan field keeps the focus
    await expect(page.getByTestId('pos-code')).toBeFocused();
    await page.keyboard.type(ean);
    await page.keyboard.press('Enter');
    const lines = page.getByTestId('pos-line');
    await expect(lines).toHaveCount(1);
    await expect(lines).toContainText(product);
    await expect(page.getByTestId('pos-line-total')).toHaveText('12,50 €');
    await expect(page.getByTestId('pos-code')).toBeFocused();
    await page.getByTestId('pos-mode-quantity').click();
    await page.getByTestId('pos-key-2').click();
    await expect(page.getByTestId('pos-qty')).toHaveText('2');
    await expect(page.getByTestId('pos-total')).toHaveText('25,00 €');
    await expect(page.getByTestId('pos-vat')).toHaveText('3,99 €');
    await expect(page.getByTestId('pos-net')).toHaveText('21,01 €');

    // ---- line note (numpad area, like Odoo)
    await page.getByTestId('pos-note').click();
    await page.getByTestId('pos-note-text').fill(note);
    await page.getByTestId('pos-note-save').click();
    await expect(page.getByTestId('pos-line-note')).toContainText(note);

    // ---- customer: create in the picker and attach to the ticket
    await page.getByTestId('pos-customer').click();
    await page.getByTestId('pos-cust-new').click();
    await page.getByTestId('pos-cust-name').fill(customer);
    await page.getByTestId('pos-cust-company').fill('Muster GmbH');
    await page.getByTestId('pos-cust-street').fill('Hauptstraße 5');
    await page.getByTestId('pos-cust-postalCode').fill('10115');
    await page.getByTestId('pos-cust-city').fill('Berlin');
    await page.getByTestId('pos-cust-email').fill(`erika-${RUN}@e2e.handynet.test`);
    await page.getByTestId('pos-cust-save').click();
    await expect(page.getByTestId('pos-customer-name')).toHaveText(customer);

    // ---- parked orders: a second ticket, product added by tapping its tile (price prefilled)
    await page.getByTestId('pos-order-new').click();
    await expect(page.getByTestId('pos-order-tab')).toHaveCount(2);
    await expect(page.getByTestId('pos-cart-empty')).toBeVisible();
    await expect(page.getByTestId('pos-customer-name')).toHaveText('Kunde');
    await tile.click();
    await expect(lines).toHaveCount(1);
    await expect(page.getByTestId('pos-total')).toHaveText('12,50 €');

    await page.locator('[data-testid="pos-order-tab"][data-order="1"]').click();
    await expect(page.getByTestId('pos-total')).toHaveText('25,00 €');
    // Tickets (with note and customer) survive a reload (stored per register in this browser).
    await page.reload();
    await expect(page.getByTestId('pos-order-tab')).toHaveCount(2);
    await expect(page.getByTestId('pos-total')).toHaveText('25,00 €');
    await expect(page.getByTestId('pos-customer-name')).toHaveText(customer);
    await expect(page.getByTestId('pos-line-note')).toContainText(note);
    await expect(page.locator('[data-testid="pos-order-tab"][data-order="1"]')).toHaveAttribute('aria-pressed', 'true');

    // ---- pay ticket 1 in cash with change
    await expect(page.getByTestId('pos-code')).toBeFocused();
    await page.keyboard.press('F9');
    await expect(page.getByTestId('pos-payment')).toBeVisible();
    await expect(page.getByTestId('pos-pay-customer')).toContainText(customer);
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
    await expect(receipt.getByTestId('receipt-line-note')).toHaveText(note);
    await expect(receipt.getByTestId('receipt-customer-name')).toHaveText(customer);
    await expect(receipt.getByTestId('receipt-customer')).toContainText('10115 Berlin');
    await expect(receipt.getByTestId('receipt-customer')).not.toContainText('@');
    await expect(receipt.getByTestId('receipt-copy')).toHaveCount(0);
    await page.getByTestId('pos-new-sale').click();
    await expect(page.getByTestId('pos-cart-empty')).toBeVisible();
    await expect(page.getByTestId('pos-code')).toBeFocused();
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
    await expect(receipt.getByTestId('receipt-customer')).toHaveCount(0);
    await page.getByTestId('pos-new-sale').click();

    // ---- order history: filters (number, customer) and reprint "Kopie 1"
    await page.getByTestId('pos-menu').click();
    await page.getByTestId('pos-menu-receipts').click();
    await expect(page.locator('[data-receipt]')).toHaveCount(2);
    await page.getByTestId('pos-f-number').fill('2');
    await page.getByTestId('pos-f-apply').click();
    await expect(page).toHaveURL(/nr=2/);
    await expect(page.locator('[data-receipt]')).toHaveCount(1);
    await expect(page.locator('[data-receipt="2"]')).toBeVisible();
    await page.getByTestId('pos-f-reset').click();
    await expect(page.locator('[data-receipt]')).toHaveCount(2);
    await page.getByTestId('pos-f-customer-pick').click();
    await page.getByTestId('pos-cust-search').fill(customer);
    await page.locator(`[data-customer="${customer}"]`).getByTestId('pos-cust-choose').click();
    await expect(page.getByTestId('pos-f-customer')).toContainText(customer);
    await expect(page.locator('[data-receipt]')).toHaveCount(1);
    const row = page.locator('[data-receipt="1"]');
    await expect(row).toContainText('25,00 €');
    await expect(row.getByTestId('pos-receipt-customer')).toHaveText(customer);
    await expect(row.getByTestId('pos-receipt-reprints')).toHaveText('0');
    await row.getByRole('link').click();
    await page.getByTestId('pos-detail-reprint').click();
    await expect(page.getByTestId('receipt').getByTestId('receipt-copy')).toHaveText('Kopie 1');
    await expect(page.getByTestId('pos-detail-reprint-count')).toContainText('1');

    // ---- return one item of receipt 1
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
    await expect(refund.getByTestId('receipt-customer-name')).toHaveText(customer);

    // ---- close the session counted by denomination (via the top-bar menu)
    await page.getByTestId('pos-menu').click();
    await page.getByTestId('pos-menu-cash').click();
    await expect(page.getByTestId('z-report')).toContainText('Zwischenstand');
    await expect(page.getByTestId('z-expected')).toHaveText('115,00 €');
    await expect(page.getByTestId('z-openingCount-total')).toHaveText('100,00 €');
    await page.getByTestId('pos-close-mode-count').click();
    const closing = page.getByTestId('pos-closing-count');
    await closing.locator('[data-denomination="10000"]').getByTestId('pos-count-plus').click();
    await closing.locator('[data-denomination="1000"]').getByTestId('pos-count-plus').click();
    await closing.locator('[data-denomination="500"]').getByTestId('pos-count-input').fill('1');
    await expect(closing.getByTestId('pos-count-total')).toHaveText('115,00 €');
    await expect(page.getByTestId('pos-close-difference')).toHaveText('0,00 €');
    await page.getByTestId('pos-close-submit').click();
    await expect(page.getByTestId('pos-confirm-counted')).toHaveText('115,00 €');
    await expect(page.getByTestId('pos-confirm-difference')).toHaveText('0,00 €');
    await page.getByTestId('pos-close-confirm').click();

    const z = page.getByTestId('z-report');
    await expect(z).toContainText('Z-Bericht');
    await expect(page.getByTestId('pos-session-closed')).toBeVisible();
    await expect(page.getByTestId('pos-topbar-session')).toContainText('Keine offene Sitzung');
    await expect(z.getByTestId('z-difference')).toHaveText('0,00 €');
    await expect(z.getByTestId('z-counted')).toHaveText('115,00 €');
    await expect(z.getByTestId('z-closingCount-total')).toHaveText('115,00 €');
    await expect(z.getByTestId('z-closingCount').locator('[data-denomination="500"]')).toContainText('× 1');
    await expect(z.getByTestId('z-range')).toHaveText('1 bis 3');
    await expect(z.getByTestId('z-opened-by')).toContainText('Geöffnet von');
    await expect(z.getByTestId('z-closed-by')).toContainText('Abgeschlossen von');
    await expect(z.getByTestId('z-total-gross')).toHaveText('25,00 €');
    await expect(z.getByTestId('z-by-payment').locator('[data-method="Cash"]')).toHaveText('15,00 €');
    await expect(z.getByTestId('z-by-payment').locator('[data-method="Card"]')).toHaveText('10,00 €');
    await expect(z.getByTestId('z-by-tax').locator('[data-treatment="Standard19"]')).toContainText('25,00 €');

    // ---- session report by product / cashier / payment method
    await page.getByTestId('pos-session-report').click();
    const report = page.getByTestId('pos-report');
    await expect(report.getByTestId('pos-report-count')).toHaveText('3');
    await expect(report.getByTestId('pos-report-total')).toHaveText('25,00 €');
    const productRow = report.getByTestId('pos-report-products').locator(`[data-product="E2E ${product}"]`);
    await expect(productRow).toContainText('3');
    await expect(productRow).toContainText('25,00 €');
    await expect(report.getByTestId('pos-report-cashiers')).toContainText(shop.displayName);
    await expect(report.getByTestId('pos-report-methods').locator('[data-method="Cash"]')).toContainText('15,00 €');
    await expect(report.getByTestId('pos-report-methods').locator('[data-method="Card"]')).toContainText('10,00 €');
    await page.getByTestId('pos-report-print').click();

    // The till now asks for a new session again.
    await page.goto(page.url().replace(/\/kasse\/berichte.*$/, '/kasse'));
    await page.locator(`[data-register="${register}"]`).getByTestId('pos-open-till').click();
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

/**
 * Test TSE (ADR-0012) through the UI: the platform admin switches a shop to the simulated TSE ("Kassen & TSE"), the till
 * shows the TSE state in the top bar, a card sale prints the TSE block (serial, transaction no., signature counter,
 * shortened signature, "Test-TSE" label) with a QR code; a forced outage shows the outage banner and the next receipt
 * carries "Sicherheitseinrichtung ausgefallen"; the recorded outage ends with the next signed sale.
 */
test.describe.serial('POS test TSE', () => {
  test.skip(!hasAdmin, 'Set HN_ADMIN_EMAIL and HN_ADMIN_PASSWORD to run e2e tests against the API.');

  const shop = { ...company('shop'), name: `E2E TSE ${RUN}`, email: `pos-tse-${RUN}@e2e.handynet.test` };
  const ean = randomEan();
  let orgId = '';
  let registerId = '';

  test.beforeAll(async () => {
    orgId = await Api.register(shop);
    const admin = await Api.login(ADMIN_EMAIL, ADMIN_PASSWORD);
    await admin.verifyWithModules(orgId, ['Inventory', 'ProductImport', 'Pos']);
    await admin.dispose();

    const api = await Api.login(shop.email, PASSWORD);
    const warehouse = await api.call<{ id: string }>('POST', '/api/inventory/warehouses', orgId, { code: 'TSE', name: 'Laden' });
    const created = await api.call<{ id: string }>('POST', '/api/catalog/products', orgId, { brand: 'E2E', name: `Hülle ${RUN}`, category: 'Accessory', ean });
    await api.call('POST', '/api/inventory/adjustments', orgId, { warehouseId: warehouse.id, productId: created.id, delta: 5, reason: 'E2E', type: 'Receipt' }, 200);
    await api.call('PUT', `/api/pos/prices/${created.id}`, orgId, { priceMinor: 990 });
    registerId = (await api.call<{ id: string }>('POST', '/api/pos/registers', orgId, { name: `TSE-Kasse ${RUN}`, warehouseId: warehouse.id })).id;
    await api.call('POST', `/api/pos/registers/${registerId}/sessions`, orgId, { openingCashMinor: 0 });
    await api.dispose();
  });

  async function sellByCard(page: Page): Promise<Locator> {
    await expect(page.getByTestId('pos-code')).toBeFocused();
    await page.keyboard.type(ean);
    await page.keyboard.press('Enter');
    await expect(page.getByTestId('pos-total')).toHaveText('9,90 €');
    await page.getByTestId('pos-pay').click();
    await page.getByTestId('pos-method-Card').click();
    await page.getByTestId('pos-validate').click();
    await expect(page.getByTestId('pos-sale-done')).toBeVisible();
    return page.getByTestId('receipt');
  }

  test('admin enables the test TSE; receipts show the TSE block and QR code; an outage is marked and recorded', async ({ page }) => {
    await page.addInitScript(() => {
      window.print = () => undefined;
    });

    // ---- platform admin: "Kassen & TSE" → simulated TSE for the shop
    await login(page, ADMIN_EMAIL, ADMIN_PASSWORD);
    await page.goto('/app/administration/kassen-tse');
    await expect(page.getByTestId('admin-fiscal-disclaimer')).toContainText('nicht zertifiziert');
    await page.getByTestId('admin-fiscal-search').fill(shop.name);
    await page.getByTestId('admin-fiscal-search').press('Enter');
    await page.locator(`[data-org="${shop.name}"]`).click();
    await expect(page.getByTestId('admin-fiscal-detail')).toBeVisible();
    await page.getByTestId('admin-fiscal-mode').selectOption('SimulatedTse');
    await page.getByTestId('admin-fiscal-save').click();
    await expect(page.getByTestId('admin-fiscal-serial')).not.toBeEmpty();
    const serial = (await page.getByTestId('admin-fiscal-serial').textContent())!.trim();
    await expect(page.getByTestId('admin-fiscal-tx-counter')).toHaveText('0');
    await logout(page);

    // ---- till: TSE state "Test-TSE"; a sale prints the TSE block with the QR code
    await login(page, shop.email, PASSWORD);
    await page.goto(`/kasse/${registerId}`);
    await expect(page.getByTestId('pos-tse-status')).toHaveAttribute('data-state', 'Test');
    await expect(page.getByTestId('pos-tse-status')).toContainText('Test-TSE');
    let receipt = await sellByCard(page);
    const tse = receipt.getByTestId('receipt-tse');
    await expect(tse).toBeVisible();
    await expect(tse.getByTestId('receipt-tse-test')).toHaveText('Test-TSE – nicht für den Echtbetrieb');
    await expect(tse.getByTestId('receipt-tse-serial')).toHaveText(serial);
    await expect(tse.getByTestId('receipt-tse-transaction')).toHaveText('1');
    await expect(tse.getByTestId('receipt-tse-counter')).toHaveText('2');
    await expect(tse.getByTestId('receipt-tse-signature')).toContainText('…');
    await expect(tse.getByTestId('qr-code')).toBeVisible();
    await expect(receipt.getByTestId('receipt-fiscal-notice')).toHaveCount(0);
    await page.getByTestId('pos-new-sale').click();

    // ---- forced outage (admin API): banner at the till, next receipt carries the outage marker
    const admin = await Api.login(ADMIN_EMAIL, ADMIN_PASSWORD);
    await admin.call('PUT', `/api/admin/pos/fiscal/${orgId}`, '', { mode: 'SimulatedTse', forcedOutage: true });
    await page.reload();
    await expect(page.getByTestId('pos-tse-status')).toHaveAttribute('data-state', 'Outage');
    await expect(page.getByTestId('pos-tse-outage-banner')).toContainText('TSE ausgefallen');
    receipt = await sellByCard(page);
    await expect(receipt.getByTestId('receipt-fiscal-notice')).toHaveText('Sicherheitseinrichtung ausgefallen');
    await expect(receipt.getByTestId('receipt-tse-outage')).toBeVisible();
    await expect(receipt.getByTestId('qr-code')).toHaveCount(0);
    const status = await admin.call<{ outages: { endedAt: string | null }[]; simulatedTse: { transactionCounter: number } }>(
      'GET', `/api/admin/pos/fiscal/${orgId}`, '');
    expect(status.outages).toHaveLength(1);
    expect(status.outages[0].endedAt).toBeNull();
    expect(status.simulatedTse.transactionCounter).toBe(1);

    // ---- TSE back: the next sale is signed again and ends the outage
    await admin.call('PUT', `/api/admin/pos/fiscal/${orgId}`, '', { mode: 'SimulatedTse', forcedOutage: false });
    await page.getByTestId('pos-new-sale').click();
    receipt = await sellByCard(page);
    await expect(receipt.getByTestId('receipt-tse-transaction')).toHaveText('2');
    await expect(page.getByTestId('pos-tse-outage-banner')).toHaveCount(0);
    const after = await admin.call<{ outages: { endedAt: string | null }[] }>('GET', `/api/admin/pos/fiscal/${orgId}`, '');
    expect(after.outages[0].endedAt).not.toBeNull();
    await admin.dispose();
  });
});
