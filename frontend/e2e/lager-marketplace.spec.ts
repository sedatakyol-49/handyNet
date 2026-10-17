import { expect, test } from '@playwright/test';
import { ADMIN_EMAIL, ADMIN_PASSWORD, Api, PASSWORD, RUN, company, hasAdmin, login, randomEan, randomImei } from './support/handynet';

/** 1×1 PNG (decodable, re-encoded to WebP by the API). */
const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==', 'base64');

/**
 * Phase 2b through the UI against the real API:
 * 1. Supplier (Inventory + ProductImport + B2BSales): Lager → "Artikel anlegen": scans an unknown EAN, creates a new
 *    product (category Akku, model + compatible models), receives 7 units with EK, creates a published offer
 *    (VK 19,90 €), saves; uploads an image right after creation (primary badge). A second entry checks the live IMEI
 *    feedback for a smartphone (Luhn error, duplicate) without saving.
 * 2. Shop (B2BPurchasing): the marketplace has no add-product action; the shop filters by seller (Verkäufer, URL
 *    `sellerId`), brand, price range and "Nur mit Bildern", sees the thumbnail, opens the detail modal by row click
 *    (net 19,90 €, gross 23,68 €, compatible models, gallery), closes it, re-opens via deep link `?offer=`, and the
 *    row's add-to-cart button adds without opening the modal.
 */
test.describe.serial('Lager manual entry and marketplace', () => {
  test.skip(!hasAdmin, 'Set HN_ADMIN_EMAIL and HN_ADMIN_PASSWORD to run e2e tests against the API.');

  const supplier = { ...company('supplier'), name: `E2E Lager ${RUN}`, email: `lager-${RUN}@e2e.handynet.test` };
  const shop = { ...company('shop'), name: `E2E Käufer ${RUN}`, email: `kaeufer-${RUN}@e2e.handynet.test` };
  const ean = randomEan();
  const brand = `E2E${RUN}`.toUpperCase();
  const product = `Akku iPhone 13 ${RUN}`;
  const sku = `AKKU-${RUN}`.toUpperCase();

  test.beforeAll(async () => {
    const supplierOrg = await Api.register(supplier);
    const shopOrg = await Api.register(shop);
    const admin = await Api.login(ADMIN_EMAIL, ADMIN_PASSWORD);
    await admin.verifyWithModules(supplierOrg, ['Inventory', 'ProductImport', 'B2BSales']);
    await admin.verifyWithModules(shopOrg, ['B2BPurchasing']);
    await admin.dispose();
  });

  test('supplier creates a product with stock and offer, uploads an image', async ({ page }) => {
    await login(page, supplier.email, PASSWORD);
    await page.goto('/app/lager');
    await page.getByTestId('entry-open').click();
    const dialog = page.getByTestId('entry-dialog');
    await expect(dialog).toBeVisible();
    await expect(page.getByTestId('entry-scan')).toBeFocused();

    // ---- 1. identify: unknown EAN → new product prefilled with the EAN
    await page.getByTestId('entry-scan').fill(ean);
    await page.getByTestId('entry-scan').press('Enter');
    await expect(page.getByTestId('entry-no-match')).toBeVisible();
    await page.getByTestId('entry-new-product').click();
    await expect(page.getByTestId('entry-ean')).toHaveValue(ean);
    // Next without required data shows field errors
    await page.getByTestId('entry-next').click();
    await expect(dialog.getByText('Pflichtangabe.').first()).toBeVisible();
    await page.getByTestId('entry-brand').fill(brand);
    await page.getByTestId('entry-name').fill(product);
    await page.getByTestId('entry-category').selectOption('Battery');
    await page.getByTestId('entry-model').fill('Akku A2653');
    await page.getByTestId('entry-compatible').fill('iPhone 13\niPhone 13 Pro');
    await page.getByTestId('entry-next').click();

    // ---- 2. stock: quantity + EK
    await expect(page.getByTestId('entry-qty')).toBeVisible();
    await page.getByTestId('entry-qty').fill('7');
    await page.getByTestId('entry-ek').fill('8,10');
    await page.getByTestId('entry-next').click();

    // ---- 3. offer: SKU suggested from the EAN, VK net, publish
    await expect(page.getByTestId('entry-sku')).toHaveValue(ean);
    await expect(page.getByTestId('entry-offer-enabled')).toBeChecked();
    await page.getByTestId('entry-sku').fill(sku);
    await page.getByTestId('entry-price').fill('19,90');
    await expect(dialog.getByText('23,68 € brutto inkl. 19 % MwSt.')).toBeVisible();
    await page.getByTestId('entry-moq').fill('1');
    await page.getByTestId('entry-days').fill('2');
    await page.getByTestId('entry-next').click();

    // ---- 4. summary + save
    await expect(page.getByTestId('summary-qty')).toHaveText('+7');
    await expect(page.getByTestId('summary-offer')).toContainText(sku);
    await expect(page.getByTestId('summary-offer')).toContainText('19,90 €');
    await page.getByTestId('entry-save').click();
    await expect(page.getByTestId('entry-success')).toContainText(`+7`);
    await expect(page.getByTestId('entry-success')).toContainText('Bestand jetzt 7');
    await expect(dialog.getByText('Neuer Artikel angelegt')).toBeVisible();
    await expect(page.getByTestId('done-offer')).toContainText('Angebot angelegt');

    // ---- image upload right after creation
    await dialog.getByTestId('image-file').setInputFiles({ name: 'akku.png', mimeType: 'image/png', buffer: PNG });
    await expect(dialog.getByTestId('image-item')).toHaveCount(1);
    await expect(dialog.getByTestId('primary-badge')).toBeVisible();

    // ---- next item: live IMEI feedback for a smartphone (not saved)
    await page.getByTestId('entry-next-item').click();
    await expect(page.getByTestId('entry-scan')).toBeFocused();
    await expect(page.getByTestId('entry-saved-banner')).toContainText(product);
    await page.getByTestId('entry-scan').fill(randomEan());
    await page.getByTestId('entry-scan').press('Enter');
    await page.getByTestId('entry-new-product').click();
    await page.getByTestId('entry-brand').fill(brand);
    await page.getByTestId('entry-name').fill(`Phone ${RUN}`);
    await page.getByTestId('entry-category').selectOption('Smartphone');
    await page.getByTestId('entry-next').click();
    const imei = randomImei();
    const bad = imei.slice(0, 14) + String((Number(imei[14]) + 1) % 10);
    await page.getByTestId('entry-imeis').fill(`${imei}\n${bad}\n${imei}`);
    await expect(page.getByTestId('entry-imei-summary')).toHaveText('1 gültig, 1 fehlerhaft, 1 doppelt');
    await expect(page.getByTestId('entry-imei-problems')).toContainText('Prüfziffer ungültig');
    await page.getByTestId('entry-next').click();
    await expect(dialog.getByText('Bitte die fehlerhaften IMEIs korrigieren.')).toBeVisible();
    // Smartphones (IMEI stock) have no offer step
    await expect(page.getByTestId('entry-steps')).not.toContainText('Angebot');
    await dialog.getByRole('button', { name: 'Abbrechen' }).click();

    // Lager shows the received stock
    await expect(page.getByTestId('stock-table')).toContainText(product);
  });

  test('shop finds the product with filters, opens the detail modal; no add-product action', async ({ page }) => {
    await login(page, shop.email, PASSWORD);
    await page.goto('/app/marktplatz');
    await expect(page.getByTestId('marketplace-results')).toBeVisible();

    // Read-only for products by design
    const main = page.locator('main');
    await expect(main.getByRole('button', { name: /artikel anlegen|produkt anlegen|artikel hinzufügen|neuer artikel/i })).toHaveCount(0);
    await expect(main.getByRole('link', { name: /artikel anlegen|produkt anlegen|artikel hinzufügen|neuer artikel/i })).toHaveCount(0);
    await expect(page.getByTestId('entry-open')).toHaveCount(0);

    // Seller (Verkäufer) filter: multi-select with facet counts, kept in the URL as sellerId
    await page.getByTestId('filter-seller').click();
    const sellerPanel = page.locator('#mp-seller-panel');
    if (await sellerPanel.getByRole('searchbox').count()) await sellerPanel.getByRole('searchbox').fill(supplier.name); // only for long lists
    await sellerPanel.getByLabel(new RegExp(supplier.name)).check();
    await expect(page).toHaveURL(/sellerId=/);
    await page.keyboard.press('Escape');
    await expect(page.getByTestId('filter-chips')).toContainText(`Verkäufer: ${supplier.name} (Berlin)`);
    await expect(page.getByTestId('offer-row')).toHaveCount(1);

    // Brand + price range + only with images
    await page.getByTestId('filter-brand').click();
    await page.locator('#mp-brand-panel').getByLabel(new RegExp(brand)).check();
    await page.keyboard.press('Escape');
    await expect(page).toHaveURL(new RegExp(`brand=${brand}`));
    await page.getByTestId('filter-price-min').fill('19');
    await page.getByTestId('filter-price-max').fill('20,00');
    await expect(page).toHaveURL(/min=19\.00/);
    await expect(page).toHaveURL(/max=20\.00/);
    await page.getByTestId('filter-images').check();
    await expect(page).toHaveURL(/images=1/);
    const row = page.getByTestId('offer-row').filter({ hasText: product });
    await expect(row).toHaveCount(1);
    await expect(row.getByTestId('row-thumb')).toBeVisible();

    // A price range that excludes the offer → empty; removing the chip brings it back
    await page.getByTestId('filter-price-max').fill('10');
    await expect(page.getByText('Der Mindestpreis liegt über dem Höchstpreis.')).toBeVisible(); // inverted range: not sent
    await page.getByTestId('filter-price-min').fill('');
    await expect(page).not.toHaveURL(/min=/);
    await expect(page.getByTestId('offer-row')).toHaveCount(0);
    await page.getByTestId('chip-remove').and(page.getByRole('button', { name: /bis 10,00/ })).click();
    await expect(row).toHaveCount(1);

    // Filters survive a reload (URL state)
    await page.reload();
    await expect(page.getByTestId('filter-chips')).toContainText(brand);
    await expect(row).toHaveCount(1);

    // Row click opens the modal with full data
    await row.locator('td').nth(2).click();
    const modal = page.getByTestId('offer-detail');
    await expect(modal).toBeVisible();
    await expect(page).toHaveURL(/offer=/);
    await expect(modal.getByTestId('detail-net')).toHaveText('19,90 €');
    await expect(modal.getByTestId('detail-gross')).toContainText('23,68 €');
    await expect(modal.getByTestId('detail-offer')).toContainText(sku);
    await expect(modal.getByTestId('detail-offer')).toContainText(supplier.name);
    await expect(modal.getByTestId('detail-product')).toContainText(ean);
    await expect(modal.getByTestId('detail-product')).toContainText('iPhone 13 Pro');
    await expect(modal.getByTestId('gallery-main')).toBeVisible();
    const offerUrl = page.url();
    await page.keyboard.press('Escape');
    await expect(modal).toBeHidden();
    await expect(page).not.toHaveURL(/offer=/);

    // Enter on a focused row opens it as well
    await row.focus();
    await page.keyboard.press('Enter');
    await expect(modal).toBeVisible();
    await page.keyboard.press('Escape');

    // Deep link
    await page.goto(offerUrl);
    await expect(page.getByTestId('offer-detail')).toBeVisible();
    await expect(page.getByTestId('detail-net')).toHaveText('19,90 €');
    await page.getByTestId('detail-add').click();
    await expect(page.getByText(/in den Warenkorb gelegt/).first()).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(page.getByTestId('offer-detail')).toBeHidden();

    // The row's add-to-cart button must not open the modal
    await row.getByTestId('add-to-cart').click();
    await expect(page.getByTestId('offer-detail')).toBeHidden();
    await expect(page).not.toHaveURL(/offer=/);
    await expect(row).toContainText('2 im Warenkorb');
    await expect(page.getByRole('link', { name: /Zum Warenkorb \(1\)/ })).toBeVisible();

    // Reset all filters
    await page.getByTestId('filters-reset').click();
    await expect(page).not.toHaveURL(/sellerId=|brand=|images=/);
  });
});
