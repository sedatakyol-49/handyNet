import { expect, request, test } from '@playwright/test';
import { ADMIN_EMAIL, ADMIN_PASSWORD, API_URL, Api, PASSWORD, RUN, company, hasAdmin, login, randomEan } from './support/handynet';

/**
 * Shared-till staff features and POS documents against the real API:
 * 1. Setup via API: shop (with VAT id) with Pos + Inventory, a product at 20,00 € in stock, a register with an open
 *    session, a cashier without discount right (PIN 4711) and a manager (PIN 2580).
 * 2. The owner's device switches to the cashier with the PIN pad; a manager approves a discount by PIN; the receipt
 *    records the cashier and the approval.
 * 3. E-receipt: email request and the public QR receipt view.
 * 4. Invoice for the sale from the receipt detail, PDF download.
 * 5. Locking the till requires the PIN again.
 */
test.describe.serial('POS staff switching, approvals and documents', () => {
  test.skip(!hasAdmin, 'Set HN_ADMIN_EMAIL and HN_ADMIN_PASSWORD to run e2e tests against the API.');

  const shop = { ...company('shop'), name: `E2E Kassenteam ${RUN}`, email: `pos-team-${RUN}@e2e.handynet.test` };
  const cashier = { email: `ben-${RUN}@e2e.handynet.test`, displayName: `Ben ${RUN}` };
  const manager = { email: `cleo-${RUN}@e2e.handynet.test`, displayName: `Cleo ${RUN}` };
  const ean = randomEan();
  const product = `Panzerglas ${RUN}`;
  let registerId = '';

  test.beforeAll(async () => {
    // Registration with a VAT id (needed for §14 UStG invoices).
    const ctx = await request.newContext({ baseURL: API_URL });
    const reg = await ctx.post('/api/registration', {
      data: {
        email: shop.email,
        displayName: shop.displayName,
        password: PASSWORD,
        organization: {
          name: shop.name, legalName: `${shop.name} GmbH`, vatId: 'DE' + String(Math.floor(1e8 + Math.random() * 9e8)), street: 'Teststraße 1',
          postalCode: '10115', city: 'Berlin', countryCode: 'DE', isWholesaler: false, isRetailer: true, isRepairProvider: false,
        },
      },
    });
    expect(reg.status(), await reg.text()).toBe(201);
    const orgId = (await reg.json()).organizationId as string;
    await ctx.dispose();

    const admin = await Api.login(ADMIN_EMAIL, ADMIN_PASSWORD);
    await admin.verifyWithModules(orgId, ['Inventory', 'ProductImport', 'Pos']);
    await admin.dispose();

    const api = await Api.login(shop.email, PASSWORD);
    const warehouse = await api.call<{ id: string }>('POST', '/api/inventory/warehouses', orgId, { code: 'LADEN', name: 'Laden' });
    const created = await api.call<{ id: string }>('POST', '/api/catalog/products', orgId, { brand: 'E2E', name: product, category: 'Accessory', ean });
    await api.call('POST', '/api/inventory/adjustments', orgId, { warehouseId: warehouse.id, productId: created.id, delta: 5, reason: 'Wareneingang', type: 'Receipt' }, 200);
    await api.call('PUT', `/api/pos/prices/${created.id}`, orgId, { priceMinor: 2000 });
    const register = await api.call<{ id: string }>('POST', '/api/pos/registers', orgId, { name: `Kasse ${RUN}`, warehouseId: warehouse.id });
    registerId = register.id;
    await api.call('POST', `/api/pos/registers/${register.id}/sessions`, orgId, { openingCashMinor: 5000 });
    const ben = await api.call<{ userId: string }>('POST', '/api/organization/staff', orgId, { ...cashier, password: PASSWORD, role: 'Staff' }, 200);
    const cleo = await api.call<{ userId: string }>('POST', '/api/organization/staff', orgId, { ...manager, password: PASSWORD, role: 'Manager' }, 200);
    await api.call('PUT', `/api/organization/staff/${ben.userId}/pos-rights`, orgId, { permissions: [], maxDiscountPercent: null });
    await api.call('PUT', `/api/pos/cashiers/${ben.userId}/pin`, orgId, { pin: '4711' });
    await api.call('PUT', `/api/pos/cashiers/${cleo.userId}/pin`, orgId, { pin: '2580' });
    await api.dispose();
  });

  async function enterPin(page: import('@playwright/test').Page, dialog: string, member: string, pin: string): Promise<void> {
    const d = page.getByTestId(dialog);
    await d.locator(`[data-member="${member}"]`).click();
    for (const digit of pin) await d.getByTestId(`pos-pin-key-${digit}`).click();
    // Four digits: submit (six digits would submit by themselves).
    await d.getByTestId('pos-pin-submit').click();
  }

  test('switch cashier by PIN, manager approves a discount, e-receipt and QR, invoice PDF, lock', async ({ page, context }) => {
    await login(page, shop.email, PASSWORD);
    await page.goto(`/kasse/${registerId}`);

    // ---- switch to the cashier on the owner's device
    await page.getByTestId('pos-switch-cashier').click();
    await enterPin(page, 'pos-pin-dialog', cashier.displayName, '9999');
    await expect(page.getByTestId('pos-pin-dialog').getByTestId('pos-pin-error')).toContainText('Noch 4 Versuche');
    for (const digit of '4711') await page.getByTestId('pos-pin-dialog').getByTestId(`pos-pin-key-${digit}`).click();
    await page.getByTestId('pos-pin-dialog').getByTestId('pos-pin-submit').click();
    await expect(page.getByTestId('pos-cashier')).toContainText(cashier.displayName);

    // ---- the cashier's own rights apply: no discount without approval
    await page.getByTestId('pos-code').fill(ean);
    await page.getByTestId('pos-code').press('Enter');
    await expect(page.getByTestId('pos-line-total')).toHaveText('20,00 €');
    await expect(page.getByTestId('pos-mode-discount')).toBeDisabled();

    // ---- manager approval on the spot
    await page.getByTestId('pos-menu').click();
    await page.getByTestId('pos-menu-approval').click();
    await enterPin(page, 'pos-approval-dialog', manager.displayName, '2580');
    await expect(page.getByTestId('pos-approval-active')).toContainText(manager.displayName);
    await page.getByTestId('pos-mode-discount').click();
    await page.getByTestId('pos-key-1').click();
    await page.getByTestId('pos-key-5').click();
    await expect(page.getByTestId('pos-line-issue')).toHaveCount(0);
    await expect(page.getByTestId('pos-total')).toHaveText('17,00 €');
    await page.getByTestId('pos-pay').click();
    await page.getByTestId('pos-cash-exact').click();
    await page.getByTestId('pos-validate').click();
    await expect(page.getByTestId('pos-sale-done')).toBeVisible();
    const receipt = page.getByTestId('pos-receipt-screen').getByTestId('receipt');
    await expect(receipt).toContainText(`Freigabe: ${manager.displayName}`);
    await expect(receipt).toContainText(cashier.displayName);
    await expect(page.getByTestId('pos-approval-active')).toHaveCount(0); // used once

    // ---- e-receipt by email and the public QR view
    await page.getByTestId('pos-ereceipt-email').fill(`kunde-${RUN}@e2e.handynet.test`);
    await page.getByTestId('pos-ereceipt-send').click();
    await expect(page.getByTestId('pos-ereceipt-sent')).toContainText('k***@e2e.handynet.test');
    await page.getByTestId('pos-ereceipt-show-qr').click();
    await expect(page.getByTestId('pos-ereceipt-qr').getByTestId('qr-code')).toBeVisible();
    const url = (await page.getByTestId('pos-ereceipt-url').textContent())!.trim();
    expect(url).toMatch(/\/beleg\/[A-Za-z0-9_-]{43}$/);
    const publicPage = await context.browser()!.newPage(); // fresh context: not signed in
    await publicPage.goto(url);
    await expect(publicPage.getByTestId('public-receipt').getByTestId('receipt-total')).toHaveText('17,00 €');
    await publicPage.goto(url.replace(/[A-Za-z0-9_-]{43}$/, 'x'.repeat(43)));
    await expect(publicPage.getByTestId('public-receipt-missing')).toBeVisible();
    await publicPage.close();

    // ---- invoice for the sale from the receipt detail
    await page.getByTestId('pos-new-sale').click();
    await page.getByTestId('pos-menu').click();
    await page.getByTestId('pos-menu-receipts').click();
    await page.locator('[data-receipt="1"]').getByRole('link').click();
    await page.getByTestId('pos-invoice-start').click();
    await page.getByTestId('pos-invoice-submit').click();
    await expect(page.getByText('Pflichtangabe für die Rechnung').first()).toBeVisible();
    await page.getByTestId('pos-invoice-name').fill('Erika Mustermann');
    await page.getByTestId('pos-invoice-street').fill('Lindenweg 3');
    await page.getByTestId('pos-invoice-postalCode').fill('10405');
    await page.getByTestId('pos-invoice-city').fill('Berlin');
    await page.getByTestId('pos-invoice-submit').click();
    await expect(page.getByTestId('pos-invoice-number')).toHaveText(/^RE-\d{4}-000001$/);
    const download = page.waitForEvent('download');
    await page.getByTestId('pos-invoice-pdf').click();
    expect((await download).suggestedFilename()).toMatch(/^RE-\d{4}-000001\.pdf$/);

    // ---- locking the till needs the PIN again
    await page.goto(`/kasse/${registerId}`);
    await page.getByTestId('pos-menu').click();
    await page.getByTestId('pos-menu-lock').click();
    await expect(page.getByTestId('pos-locked')).toBeVisible();
    await enterPin(page, 'pos-pin-dialog', cashier.displayName, '4711');
    await expect(page.getByTestId('pos-locked')).toHaveCount(0);
    await expect(page.getByTestId('pos-cashier')).toContainText(cashier.displayName);
  });
});
