import { expect, request, test } from '@playwright/test';
import { ADMIN_EMAIL, ADMIN_PASSWORD, API_URL, Api, PASSWORD, RUN, company, hasAdmin, login, randomEan } from './support/handynet';

/**
 * Quotations and invoices outside the till (ADR-0015) against the real API:
 * 1. Setup via API: shop (with VAT id) with Pos + Inventory, a product at 25,00 € (5 in stock), a register with an open
 *    session and a business customer with a full address.
 * 2. POS app: new quotation (customer picker, EAN scan, quantity 2) → convert into an invoice draft → issue (RE number) →
 *    PDF download → collect the amount in cash at the till (a payment receipt) → the invoice is paid.
 * 3. The session is closed; the Z-report shows the Z number and the invoice payment.
 * 4. Back office: "Rechnungen & Angebote" lists the invoice as paid.
 */
test.describe.serial('Quotations and sales invoices', () => {
  test.skip(!hasAdmin, 'Set HN_ADMIN_EMAIL and HN_ADMIN_PASSWORD to run e2e tests against the API.');

  const shop = { ...company('shop'), name: `E2E Rechnungen ${RUN}`, email: `sales-${RUN}@e2e.handynet.test` };
  const ean = randomEan();
  const product = `Ladekabel ${RUN}`;
  const customerName = `Max Großhandel ${RUN}`;
  let orgId = '';
  let registerId = '';
  let sessionId = '';

  test.beforeAll(async () => {
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
    orgId = (await reg.json()).organizationId as string;
    await ctx.dispose();

    const admin = await Api.login(ADMIN_EMAIL, ADMIN_PASSWORD);
    await admin.verifyWithModules(orgId, ['Inventory', 'ProductImport', 'Pos']);
    await admin.dispose();

    const api = await Api.login(shop.email, PASSWORD);
    const warehouse = await api.call<{ id: string }>('POST', '/api/inventory/warehouses', orgId, { code: 'LADEN', name: 'Laden' });
    const created = await api.call<{ id: string }>('POST', '/api/catalog/products', orgId, { brand: 'E2E', name: product, category: 'Accessory', ean });
    await api.call('POST', '/api/inventory/adjustments', orgId, { warehouseId: warehouse.id, productId: created.id, delta: 5, reason: 'Wareneingang', type: 'Receipt' }, 200);
    await api.call('PUT', `/api/pos/prices/${created.id}`, orgId, { priceMinor: 2500 });
    const register = await api.call<{ id: string }>('POST', '/api/pos/registers', orgId, { name: `Kasse ${RUN}`, warehouseId: warehouse.id });
    registerId = register.id;
    sessionId = (await api.call<{ id: string }>('POST', `/api/pos/registers/${register.id}/sessions`, orgId, { openingCashMinor: 5000 })).id;
    await api.call('POST', '/api/pos/customers', orgId, {
      name: customerName, company: `Muster Handel ${RUN} GmbH`, street: 'Gewerbering 9', postalCode: '20095', city: 'Hamburg', countryCode: 'DE', vatId: 'DE123456789',
    });
    await api.dispose();
  });

  test('quotation → invoice → PDF → cash at the till → Z number on the Z-report', async ({ page }) => {
    await login(page, shop.email, PASSWORD);
    await page.goto(`/kasse/${registerId}`);
    await page.getByTestId('pos-menu').click();
    await page.getByTestId('pos-menu-quotations').click();
    await expect(page).toHaveURL(new RegExp(`/kasse/${registerId}/angebote$`));

    // ---- new quotation
    await page.getByTestId('sales-new').click();
    await page.getByTestId('sales-customer-pick').click();
    await page.getByTestId('pos-cust-search').fill(customerName);
    await page.locator(`[data-customer="${customerName}"]`).getByTestId('pos-cust-choose').click();
    await expect(page.getByTestId('sales-customer')).toContainText('Gewerbering 9');
    await page.getByTestId('sales-scan').fill(ean);
    await page.getByTestId('sales-scan').press('Enter');
    await expect(page.getByTestId('line-description')).toHaveValue(new RegExp(product));
    await expect(page.getByTestId('line-price')).toHaveValue('25,00');
    await page.getByTestId('line-quantity').fill('2');
    await expect(page.getByTestId('sales-total')).toContainText('50,00');
    await page.getByTestId('sales-form-save').click();
    await expect(page.getByTestId('sales-detail-number')).toHaveText(/^AN-\d{4}-0001$/);
    await expect(page.getByTestId('sales-detail-total')).toContainText('50,00');

    // ---- convert and issue
    await page.getByTestId('sales-convert').click();
    await expect(page).toHaveURL(new RegExp(`/kasse/${registerId}/rechnungen/`));
    await expect(page.getByTestId('sales-detail-number')).toContainText('Entwurf');
    await page.getByTestId('sales-issue').click();
    await page.getByTestId('sales-issue-confirm').click();
    await expect(page.getByTestId('sales-detail-number')).toHaveText(/^RE-\d{4}-000001$/);
    await expect(page.getByTestId('sales-detail-open')).toContainText('50,00');

    const download = page.waitForEvent('download');
    await page.getByTestId('sales-pdf').click();
    expect((await download).suggestedFilename()).toMatch(/^RE-\d{4}-000001\.pdf$/);
    const invoiceUrl = page.url();

    // ---- collect in cash at the till: a payment receipt, then the invoice is paid
    await page.getByTestId('sales-collect').click();
    await expect(page.getByTestId('sales-collect-amount')).toHaveValue('50,00');
    await page.getByTestId('sales-collect-tendered').fill('60,00');
    await expect(page.getByTestId('sales-collect-dialog')).toContainText('10,00');
    await page.getByTestId('sales-collect-confirm').click();
    await expect(page).toHaveURL(/\/kasse\/belege\//);
    await expect(page.getByText('Zahlung Rechnung RE-').first()).toBeVisible();
    await page.goto(invoiceUrl);
    await expect(page.getByTestId('sales-detail-status')).toContainText('Bezahlt');
    await expect(page.getByTestId('sales-collect')).toHaveCount(0);

    // ---- close the session; the Z-report shows the Z number and the invoice payment
    const api = await Api.login(shop.email, PASSWORD);
    await api.call('POST', `/api/pos/sessions/${sessionId}/close`, orgId, { countedCashMinor: 10000 }, 200);
    await api.dispose();
    await page.goto(`/kasse/sitzungen/${sessionId}`);
    await expect(page.getByTestId('z-number')).toContainText('Z-Nr. 1');
    await expect(page.getByTestId('z-invoice-payments')).toContainText('50,00');
    await page.goto(`/kasse/${registerId}/sitzungen`);
    await expect(page.getByTestId('pos-session-z').first()).toHaveText('1');

    // ---- back office: Verkauf → Rechnungen & Angebote
    await page.goto('/app/verkauf/rechnungen');
    const row = page.locator('[data-testid="sales-table"] tr', { hasText: /RE-\d{4}-000001/ });
    await expect(row).toContainText('Bezahlt');
    await page.getByTestId('sales-tab-quotations').click();
    await expect(page.locator('[data-testid="sales-table"] tr', { hasText: /AN-\d{4}-0001/ })).toContainText('Angenommen');
  });
});
