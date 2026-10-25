import { expect, test } from '@playwright/test';
import { ADMIN_EMAIL, ADMIN_PASSWORD, Api, PASSWORD, RUN, company, hasAdmin, login, randomEan } from './support/handynet';

/** 1×1 PNG (decodable, re-encoded to WebP by the API). */
const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==', 'base64');

/**
 * Lager product panel against the real API:
 * 1. Owner (created the product; Inventory + ProductImport + B2BSales + Pos) opens a stock row by click: uploads an
 *    image, renames the product, sets the own POS price and changes the own offer price; sees the EK of the receipt.
 * 2. Another company stocking the same canonical product sees images and data read-only with the shared-catalog hint,
 *    but its own prices and stock.
 */
test.describe.serial('Lager product panel', () => {
  test.skip(!hasAdmin, 'Set HN_ADMIN_EMAIL and HN_ADMIN_PASSWORD to run e2e tests against the API.');

  const owner = { ...company('supplier'), name: `E2E Panel ${RUN}`, email: `panel-${RUN}@e2e.handynet.test` };
  const other = { ...company('supplier'), name: `E2E Panel B ${RUN}`, email: `panel-b-${RUN}@e2e.handynet.test` };
  const ean = randomEan();
  const name = `Display iPhone 14 ${RUN}`;
  const renamed = `Display iPhone 14 OLED ${RUN}`;
  const sku = `PANEL-${RUN}`.toUpperCase();

  test.beforeAll(async () => {
    const ownerOrg = await Api.register(owner);
    const otherOrg = await Api.register(other);
    const admin = await Api.login(ADMIN_EMAIL, ADMIN_PASSWORD);
    await admin.verifyWithModules(ownerOrg, ['Inventory', 'ProductImport', 'B2BSales', 'Pos']);
    await admin.verifyWithModules(otherOrg, ['Inventory', 'ProductImport', 'B2BSales']);
    await admin.dispose();

    const ownerApi = await Api.login(owner.email, PASSWORD);
    const created = await ownerApi.createItem(ownerOrg, {
      newProduct: { brand: 'Apple', name, category: 'Display', quality: 'ServicePack', ean, compatibleModels: ['iPhone 14'] },
      quantity: 5,
      purchasePrice: 8.1,
      offer: { supplierSku: sku, price: 19.9, minOrderQuantity: 1, deliveryDays: 2, condition: 'New', publish: true },
    });
    await ownerApi.dispose();
    const otherApi = await Api.login(other.email, PASSWORD);
    await otherApi.createItem(otherOrg, { productId: created.product.id, quantity: 2 });
    await otherApi.dispose();
  });

  test('owner uploads an image, edits the name, the POS price and the offer price', async ({ page }) => {
    await login(page, owner.email, PASSWORD);
    await page.goto('/app/lager');
    const row = page.getByTestId('stock-row').filter({ hasText: name });
    await expect(row).toHaveCount(1);
    await row.getByRole('cell').first().click();
    const panel = page.getByTestId('product-panel');
    await expect(panel).toBeVisible();
    await expect(panel.getByTestId('panel-summary')).toContainText(ean);

    // ---- Bilder: manager (own product)
    await expect(panel.getByTestId('panel-readonly')).toHaveCount(0);
    await panel.getByTestId('image-file').setInputFiles({ name: 'display.png', mimeType: 'image/png', buffer: PNG });
    await expect(panel.getByTestId('image-item')).toHaveCount(1);
    await expect(panel.getByTestId('primary-badge')).toBeVisible();

    // ---- Artikeldaten: rename (keyboard: arrow key moves to the next tab)
    await panel.getByTestId('panel-tab-images').focus();
    await page.keyboard.press('ArrowRight');
    await expect(panel.getByTestId('panel-tab-data')).toHaveAttribute('aria-selected', 'true');
    await expect(panel.getByTestId('pp-name')).toHaveValue(name);
    await panel.getByTestId('pp-name').fill(renamed);
    await panel.getByTestId('pp-save').click();
    await expect(page.getByText('Artikeldaten gespeichert.')).toBeVisible();
    await expect(page.getByTestId('stock-table')).toContainText(renamed);

    // ---- Preise & Angebot
    await panel.getByTestId('panel-tab-prices').click();
    await expect(panel.getByTestId('panel-ek')).toContainText('8,10 €');
    await expect(panel.getByTestId('panel-pos-current')).toContainText('23,68 €'); // offer net 19,90 + 19 %
    await panel.getByTestId('pp-pos').fill('24,90');
    await panel.getByTestId('pp-pos-save').click();
    await expect(panel.getByTestId('panel-pos-current')).toContainText('24,90 €');
    await expect(panel.getByTestId('panel-pos')).toContainText('Eigener Kassenpreis');

    await expect(panel.getByTestId('pp-offer-current')).toContainText(sku);
    await expect(panel.getByTestId('pp-price')).toHaveValue('19,90');
    await panel.getByTestId('pp-price').fill('21,50');
    await expect(panel.getByText('Brutto inkl. 19 % USt: 25,59 €')).toBeVisible();
    await panel.getByTestId('pp-offer-save').click();
    await expect(panel.getByTestId('pp-offer-current')).toContainText('21,50 €');

    // ---- Bestand
    await panel.getByTestId('panel-tab-stock').click();
    await expect(panel.getByTestId('panel-stock-row')).toHaveCount(1);
    await expect(panel.getByTestId('panel-stock-row')).toContainText('5');
    await expect(panel.getByTestId('panel-movements')).toContainText('8,10 €');

    await panel.getByTestId('dialog-close').click();
    await expect(panel).toBeHidden();

    // The offer list shows the new price
    await page.goto('/app/angebote');
    await expect(page.getByTestId('offers-table')).toContainText('21,50 €');
  });

  test('another company sees images and data read-only, but its own prices and stock', async ({ page }) => {
    await login(page, other.email, PASSWORD);
    await page.goto('/app/lager');
    const row = page.getByTestId('stock-row').filter({ hasText: renamed });
    await expect(row).toHaveCount(1);
    // Keyboard: focus the row and press Enter
    await row.focus();
    await page.keyboard.press('Enter');
    const panel = page.getByTestId('product-panel');
    await expect(panel).toBeVisible();

    await expect(panel.getByTestId('panel-readonly')).toContainText('gemeinsamen Katalog eines anderen Unternehmens');
    await expect(panel.getByTestId('image-manager')).toHaveCount(0);
    await expect(panel.getByTestId('image-file')).toHaveCount(0);
    await expect(panel.getByTestId('gallery-main')).toBeVisible(); // the owner's image

    await panel.getByTestId('panel-tab-data').click();
    await expect(panel.getByTestId('pp-readonly')).toContainText(renamed);
    await expect(panel.getByTestId('pp-name')).toHaveCount(0);

    await panel.getByTestId('panel-tab-prices').click();
    await expect(panel.getByTestId('panel-pos')).toHaveCount(0); // no Pos module
    await expect(panel.getByTestId('pp-offer-none')).toBeVisible(); // its own offers only
    await expect(panel.getByTestId('pp-offer-form')).toBeVisible();

    await panel.getByTestId('panel-tab-stock').click();
    await expect(panel.getByTestId('panel-stock-row')).toContainText('2');
  });
});
