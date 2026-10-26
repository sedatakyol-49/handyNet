import { expect, request, test } from '@playwright/test';
import { deflateSync } from 'node:zlib';
import { ADMIN_EMAIL, ADMIN_PASSWORD, API_URL, Api, PASSWORD, RUN, company, hasAdmin, login, randomEan } from './support/handynet';

/** A real PNG (wide red/blue logo) built in memory: signature, IHDR, zlib IDAT, IEND with CRCs. */
function logoPng(width = 240, height = 80): Buffer {
  const crcTable = Array.from({ length: 256 }, (_, n) => {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    return c >>> 0;
  });
  const crc = (buf: Buffer) => {
    let c = 0xffffffff;
    for (const b of buf) c = crcTable[(c ^ b) & 0xff] ^ (c >>> 8);
    return (c ^ 0xffffffff) >>> 0;
  };
  const chunk = (type: string, data: Buffer) => {
    const len = Buffer.alloc(4);
    len.writeUInt32BE(data.length);
    const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
    const sum = Buffer.alloc(4);
    sum.writeUInt32BE(crc(body));
    return Buffer.concat([len, body, sum]);
  };
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 2; // RGB
  const rows: Buffer[] = [];
  for (let y = 0; y < height; y++) {
    const row = Buffer.alloc(1 + width * 3);
    for (let x = 0; x < width; x++) {
      const left = x < width / 3;
      row[1 + x * 3] = left ? 0xd0 : 0x1e;
      row[2 + x * 3] = left ? 0x20 : 0x40;
      row[3 + x * 3] = left ? 0x30 : 0xaf;
    }
    rows.push(row);
  }
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(Buffer.concat(rows))),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

/**
 * The owner maintains the company profile (ADR-0014) against the real API:
 * 1. Setup via API: shop with Pos + Inventory, a product at 9,99 € in stock, a register with an open session, a staff member.
 * 2. The owner opens "Unternehmen" from the side navigation, edits the receipt footer and bank details (IBAN checked on
 *    the client), saves; uploads a logo through the crop dialog; the logo shows in the back-office top bar.
 * 3. At the till the logo is in the POS top bar; after a sale the receipt shows the logo and the footer text.
 * 4. A staff member sees the page read-only.
 */
test.describe.serial('company profile and logo', () => {
  test.skip(!hasAdmin, 'Set HN_ADMIN_EMAIL and HN_ADMIN_PASSWORD to run e2e tests against the API.');

  const shop = { ...company('shop'), name: `E2E Firmendaten ${RUN}`, email: `firma-${RUN}@e2e.handynet.test` };
  const staff = { email: `firma-staff-${RUN}@e2e.handynet.test`, displayName: `Dana ${RUN}` };
  const ean = randomEan();
  const footer = `Umtausch innerhalb von 14 Tagen ${RUN}`;
  let registerId = '';

  test.beforeAll(async () => {
    const ctx = await request.newContext({ baseURL: API_URL });
    const reg = await ctx.post('/api/registration', {
      data: {
        email: shop.email,
        displayName: shop.displayName,
        password: PASSWORD,
        organization: {
          name: shop.name, legalName: `${shop.name} GmbH`, street: 'Teststraße 1', postalCode: '10115', city: 'Berlin', countryCode: 'DE',
          isWholesaler: false, isRetailer: true, isRepairProvider: false,
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
    const product = await api.call<{ id: string }>('POST', '/api/catalog/products', orgId, { brand: 'E2E', name: `Ladekabel ${RUN}`, category: 'Accessory', ean });
    await api.call('POST', '/api/inventory/adjustments', orgId, { warehouseId: warehouse.id, productId: product.id, delta: 5, reason: 'Wareneingang', type: 'Receipt' }, 200);
    await api.call('PUT', `/api/pos/prices/${product.id}`, orgId, { priceMinor: 999 });
    registerId = (await api.call<{ id: string }>('POST', '/api/pos/registers', orgId, { name: `Kasse ${RUN}`, warehouseId: warehouse.id })).id;
    await api.call('POST', `/api/pos/registers/${registerId}/sessions`, orgId, { openingCashMinor: 5000 });
    await api.call('POST', '/api/organization/staff', orgId, { ...staff, password: PASSWORD, role: 'Staff' }, 200);
    await api.dispose();
  });

  test('owner edits company data, uploads a logo and sees it in the POS top bar and on the receipt', async ({ page }) => {
    await login(page, shop.email, PASSWORD);
    await page.locator('#side-nav').getByRole('link', { name: 'Unternehmen', exact: true }).click();
    await expect(page).toHaveURL(/\/app\/unternehmen$/);
    await expect(page.getByTestId('company-name')).toHaveValue(shop.name);

    // Receipt footer with live 80 mm preview.
    await page.getByTestId('company-receiptFooter').fill(`Mo–Sa 10–19 Uhr\n${footer}`);
    await expect(page.getByTestId('company-preview-footer').last()).toHaveText(footer);

    // A wrong IBAN is refused on the client; the correct one is saved.
    await page.getByTestId('company-iban').fill('DE89 3704 0044 0532 0130 01');
    await page.getByTestId('company-save').click();
    await expect(page.locator('#company-iban-error')).toContainText('IBAN');
    await page.getByTestId('company-iban').fill('DE89370400440532013000');
    await page.getByTestId('company-bankName').fill('Commerzbank');
    await page.getByTestId('company-emailLanguage').selectOption('tr');
    await page.getByTestId('company-save').click();
    await expect(page.getByText('Firmendaten gespeichert.')).toBeVisible();
    await page.reload();
    await expect(page.getByTestId('company-iban')).toHaveValue('DE89 3704 0044 0532 0130 00');
    await expect(page.getByTestId('company-emailLanguage')).toHaveValue('tr');
    await expect(page.getByTestId('company-receiptFooter')).toHaveValue(`Mo–Sa 10–19 Uhr\n${footer}`);

    // Logo: crop dialog, upload, visible on the page and in the top bar.
    await page.getByTestId('company-logo-file').setInputFiles({ name: 'logo.png', mimeType: 'image/png', buffer: logoPng() });
    await expect(page.getByTestId('logo-crop')).toBeVisible();
    await page.getByTestId('logo-aspect-wide').check({ force: true });
    await page.getByTestId('logo-crop-confirm').click();
    await expect(page.getByText('Logo gespeichert.')).toBeVisible();
    const logo = page.getByTestId('company-logo-image');
    await expect(logo).toBeVisible();
    await expect.poll(() => logo.evaluate((img: HTMLImageElement) => img.naturalWidth)).toBeGreaterThan(0);
    await expect(page.getByTestId('shell-company-logo').locator('img')).toBeVisible();

    // POS: logo in the top bar, then a sale whose receipt shows logo and footer.
    await page.goto(`/kasse/${registerId}`);
    await expect(page.getByTestId('pos-topbar-logo').locator('img')).toBeVisible();
    await page.getByTestId('pos-code').fill(ean);
    await page.getByTestId('pos-code').press('Enter');
    await expect(page.getByTestId('pos-total')).toHaveText('9,99 €');
    await page.getByTestId('pos-pay').click();
    await page.getByTestId('pos-cash-exact').click();
    await page.getByTestId('pos-validate').click();
    await expect(page.getByTestId('pos-sale-done')).toBeVisible();
    const receipt = page.getByTestId('pos-receipt-screen').getByTestId('receipt');
    await expect(receipt.getByTestId('receipt-logo')).toBeVisible();
    await expect.poll(() => receipt.getByTestId('receipt-logo').evaluate((img: HTMLImageElement) => img.naturalWidth)).toBeGreaterThan(0);
    await expect(receipt.getByTestId('receipt-footer-text')).toContainText(footer);
  });

  test('a staff member sees the company data read-only', async ({ page }) => {
    await login(page, staff.email, PASSWORD);
    await page.goto('/app/unternehmen');
    await expect(page.getByTestId('company-read-only')).toBeVisible();
    await expect(page.getByTestId('company-iban')).toBeDisabled();
    await expect(page.getByTestId('company-save')).toHaveCount(0);
    await expect(page.getByTestId('company-logo-image')).toBeVisible();
  });
});
