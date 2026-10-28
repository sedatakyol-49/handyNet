import { Page, expect, test } from '@playwright/test';
import { ADMIN_EMAIL, ADMIN_PASSWORD, Api, PASSWORD, RUN, company, hasAdmin, login, randomEan, randomImei } from './support/handynet';

/** Optional: directory for review screenshots (not written when unset). */
const SHOTS = process.env['HN_SCREENSHOT_DIR'] ?? '';
async function shot(page: Page, name: string): Promise<void> {
  if (SHOTS) await page.screenshot({ path: `${SHOTS}/${name}.png`, fullPage: false });
}

/**
 * Lager "Artikel anlegen" with several products at once, through the UI against the real API:
 * the supplier ticks two batteries in one search and a smartphone in a second search (the selection survives the new
 * search), sets quantities ("Menge für alle" + one override), an offer for one row and an IMEI for the phone that is
 * already in stock. Saving books the two batteries and fails only the phone row (unit.imei_in_stock); closing asks
 * first; the phone row is fixed and "Fehlgeschlagene erneut senden" resends only that row with its original
 * Idempotency-Key. The stock table shows the increased stock, nothing was booked twice.
 */
test.describe.serial('Lager bulk entry (multi-select)', () => {
  test.skip(!hasAdmin, 'Set HN_ADMIN_EMAIL and HN_ADMIN_PASSWORD to run e2e tests against the API.');

  const supplier = { ...company('supplier'), name: `E2E Sammel ${RUN}`, email: `sammel-${RUN}@e2e.handynet.test` };
  const brand = `BULK${RUN}`.toUpperCase();
  const akkuA = `Akku ${RUN} Alpha`;
  const akkuB = `Akku ${RUN} Beta`;
  const phone = `Phone ${RUN} Delta`;
  const stockedImei = randomImei();
  const freshImei = randomImei();
  let orgId = '';

  test.beforeAll(async () => {
    orgId = await Api.register(supplier);
    const admin = await Api.login(ADMIN_EMAIL, ADMIN_PASSWORD);
    await admin.verifyWithModules(orgId, ['Inventory', 'ProductImport', 'B2BSales']);
    await admin.dispose();
    const api = await Api.login(supplier.email, PASSWORD);
    const product = (name: string, category: string, mpn: string) => ({ brand, name, category, quality: 'NotApplicable', ean: randomEan(), mpn });
    await api.createItem(orgId, { newProduct: product(akkuA, 'Battery', `AKA-${RUN}`), quantity: 2 });
    await api.createItem(orgId, { newProduct: product(akkuB, 'Battery', `AKB-${RUN}`), quantity: 1 });
    await api.createItem(orgId, { newProduct: product(phone, 'Smartphone', `PHD-${RUN}`), imeis: [stockedImei] });
    await api.dispose();
  });

  test('selects products from two searches, saves per row, retries only the failed row', async ({ page }) => {
    const posts: { productId: string; key: string }[] = [];
    page.on('request', (r) => {
      if (r.method() === 'POST' && /\/api\/inventory\/items$/.test(r.url())) {
        posts.push({ productId: r.postDataJSON().productId, key: r.headers()['idempotency-key'] });
      }
    });

    await login(page, supplier.email, PASSWORD);
    await page.goto('/app/lager');
    await page.getByTestId('entry-open').click();
    const dialog = page.getByTestId('entry-dialog');
    const scan = page.getByTestId('entry-scan');
    await expect(scan).toBeFocused();

    // ---- search 1: two batteries – one by clicking the row, one by Space on its checkbox
    await scan.fill(`Akku ${RUN}`);
    await scan.press('Enter');
    const matches = page.getByTestId('entry-match');
    await expect(matches).toHaveCount(2);
    await matches.filter({ hasText: akkuA }).getByText('Eigener Bestand').click();
    await expect(matches.filter({ hasText: akkuA }).getByTestId('entry-select')).toBeChecked();
    await matches.filter({ hasText: akkuB }).getByTestId('entry-select').focus();
    await page.keyboard.press('Space');
    await expect(matches.filter({ hasText: akkuB }).getByTestId('entry-select')).toBeChecked();
    await expect(page.getByTestId('entry-tray-title')).toHaveText('Ausgewählt (2)');
    // new products are single entries: not offered while a selection exists
    await expect(page.getByTestId('entry-new-product')).toHaveCount(0);

    // ---- search 2: the phone; the batteries stay selected
    await scan.fill(`Phone ${RUN}`);
    await scan.press('Enter');
    await expect(matches).toHaveCount(1);
    await expect(page.getByTestId('entry-tray-title')).toHaveText('Ausgewählt (2)');
    await matches.getByTestId('entry-select').check();
    await expect(page.getByTestId('entry-tray-title')).toHaveText('Ausgewählt (3)');
    // remove via chip and tick again
    await page.getByRole('button', { name: `${brand} ${phone} aus der Auswahl entfernen` }).click();
    await expect(page.getByTestId('entry-tray-title')).toHaveText('Ausgewählt (2)');
    await expect(matches.getByTestId('entry-select')).not.toBeChecked();
    await matches.getByTestId('entry-select').check();
    await expect(page.getByTestId('entry-chip')).toHaveCount(3);
    await shot(page, '01-selection-tray');

    // ---- bulk table
    await page.getByTestId('entry-next').click();
    await expect(page.getByTestId('entry-steps')).toContainText('Mengen');
    const rows = page.getByTestId('bulk-row');
    await expect(rows).toHaveCount(3);
    const rowA = rows.filter({ hasText: akkuA });
    const rowB = rows.filter({ hasText: akkuB });
    const rowPhone = rows.filter({ hasText: phone });
    await page.getByTestId('bulk-qty-all').fill('4');
    await page.getByTestId('bulk-qty-apply').click();
    await expect(rowA.getByTestId('bulk-qty')).toHaveValue('4');
    await expect(rowB.getByTestId('bulk-qty')).toHaveValue('4');
    await rowB.getByTestId('bulk-qty').fill('6');
    await rowA.getByTestId('bulk-ek').fill('7,40');
    // offer for battery A: SKU prefilled from the MPN
    await rowA.getByTestId('bulk-offer-enabled').check();
    await expect(rowA.getByTestId('bulk-sku')).toHaveValue(new RegExp(`^AKA-${RUN}$`, 'i'));
    await rowA.getByTestId('bulk-price').fill('14,90');
    await expect(rowA.getByText('17,73 € brutto inkl. 19 % MwSt.')).toBeVisible();
    // the phone is stocked by IMEI only, without offer; live IMEI feedback
    await expect(rowPhone.getByTestId('bulk-qty')).toHaveCount(0);
    await expect(rowPhone.getByTestId('bulk-offer-enabled')).toHaveCount(0);
    await rowPhone.getByTestId('bulk-imeis').fill(`${stockedImei}\n${stockedImei.slice(0, 14)}${(Number(stockedImei[14]) + 1) % 10}`);
    await expect(rowPhone.getByTestId('bulk-imei-summary')).toHaveText('1 gültig, 1 fehlerhaft, 0 doppelt');
    await page.getByTestId('entry-next').click();
    await expect(rowPhone.getByText('Bitte die fehlerhaften IMEIs korrigieren.')).toBeVisible();
    // the IMEI that is already in stock passes the client checks; the API refuses it
    await rowPhone.getByTestId('bulk-imeis').fill(stockedImei);
    await expect(rowPhone.getByTestId('bulk-imei-summary')).toHaveText('1 gültig, 0 fehlerhaft, 0 doppelt');
    await shot(page, '02-bulk-rows');
    await page.getByTestId('entry-next').click();

    // ---- review + save: 2 of 3 saved, the phone row fails
    const review = page.getByTestId('bulk-review-row');
    await expect(review).toHaveCount(3);
    await expect(review.filter({ hasText: akkuB }).getByTestId('bulk-review-qty')).toHaveText('+6');
    await expect(review.filter({ hasText: phone }).getByTestId('bulk-review-qty')).toHaveText('1 Geräte');
    await page.getByTestId('bulk-save').click();
    await expect(page.getByTestId('bulk-summary')).toContainText('2 von 3 gespeichert');
    await expect(review.filter({ hasText: akkuA }).getByTestId('bulk-review-status')).toHaveText('Gespeichert');
    await expect(review.filter({ hasText: akkuB }).getByTestId('bulk-review-status')).toHaveText('Gespeichert');
    await expect(review.filter({ hasText: phone }).getByTestId('bulk-review-status')).toHaveText('Fehler');
    await expect(review.filter({ hasText: phone }).getByTestId('bulk-review-error')).toHaveText(
      `Die IMEI ${stockedImei} ist bereits im Bestand. Dasselbe Gerät kann nicht zweimal eingebucht werden.`,
    );
    await expect(page.getByTestId('bulk-save')).toHaveText(/Fehlgeschlagene erneut senden \(1\)/);
    await shot(page, '03-review-one-failed');
    expect(posts).toHaveLength(3);
    expect(new Set(posts.map((p) => p.key)).size).toBe(3);

    // ---- closing with a failed row asks first (Esc and ✕)
    await page.keyboard.press('Escape');
    await expect(page.getByTestId('entry-close-confirm')).toBeVisible();
    await expect(page.getByTestId('entry-close-keep')).toBeFocused();
    await page.getByTestId('entry-close-keep').click();
    await expect(page.getByTestId('entry-close-confirm')).toHaveCount(0);
    await expect(dialog).toBeVisible();

    // ---- fix the phone row and resend only that row
    await review.filter({ hasText: phone }).getByTestId('bulk-edit-row').click();
    await expect(rowPhone.getByTestId('bulk-imeis')).toBeFocused();
    await expect(rowA.getByTestId('bulk-qty')).toBeDisabled(); // saved rows are locked
    await expect(rowA.getByTestId('bulk-row-saved')).toContainText('+4');
    await rowPhone.getByTestId('bulk-imeis').fill(freshImei);
    await page.getByTestId('entry-next').click();
    await page.getByTestId('bulk-save').click();
    await expect(page.getByTestId('bulk-summary')).toContainText('3 von 3 gespeichert');
    await expect(review.filter({ hasText: phone }).getByTestId('bulk-review-status')).toHaveText('Gespeichert');
    await shot(page, '04-review-all-saved');

    // only the phone was sent again, with its own original key
    expect(posts).toHaveLength(4);
    const byProduct = (id: string) => posts.filter((p) => p.productId === id);
    const phoneId = posts[3].productId;
    expect(byProduct(phoneId)).toHaveLength(2);
    expect(byProduct(phoneId)[0].key).toBe(byProduct(phoneId)[1].key);
    expect(posts.filter((p) => p.productId !== phoneId)).toHaveLength(2);
    expect(new Set(posts.filter((p) => p.productId !== phoneId).map((p) => p.productId)).size).toBe(2);

    // ---- "Weitere Artikel erfassen": empty selection, focus in the search
    await page.getByTestId('bulk-more').click();
    await expect(scan).toBeFocused();
    await expect(scan).toHaveValue('');
    await expect(page.getByTestId('entry-tray')).toHaveCount(0);
    await expect(page.getByTestId('entry-saved-banner')).toContainText('3 Artikel gespeichert');
    // nothing unsaved now: closing does not ask
    await dialog.getByRole('button', { name: 'Abbrechen' }).click();
    await expect(dialog).toBeHidden();

    // ---- Lager shows the received stock (2 + 4, 1 + 6, 1 + 1 devices)
    const table = page.getByTestId('stock-table');
    const onHand = (name: string) => table.locator('tr', { hasText: name }).locator('td').nth(3);
    await expect(onHand(akkuA)).toHaveText('6');
    await expect(onHand(akkuB)).toHaveText('7');
    await expect(onHand(phone)).toHaveText('2');
    await shot(page, '05-lager-stock');
  });

  test('bulk table renders right-to-left in Arabic', async ({ page }) => {
    await login(page, supplier.email, PASSWORD);
    await page.goto('/app/lager');
    await page.locator('#lang-switcher').selectOption('ar');
    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
    await page.getByTestId('entry-open').click();
    await page.getByTestId('entry-scan').fill(`Akku ${RUN}`);
    await page.getByTestId('entry-scan').press('Enter');
    await expect(page.getByTestId('entry-match')).toHaveCount(2);
    for (const box of await page.getByTestId('entry-select').all()) await box.check();
    await expect(page.getByTestId('entry-tray-title')).toHaveText('المحدد (2)');
    await page.getByTestId('entry-next').click();
    await expect(page.getByTestId('bulk-row')).toHaveCount(2);
    // the remove button sits on the left (end side) in RTL
    const row = page.getByTestId('bulk-row').first();
    const rowBox = (await row.boundingBox())!;
    const removeBox = (await row.getByTestId('bulk-remove').boundingBox())!;
    expect(removeBox.x - rowBox.x).toBeLessThan(rowBox.width / 2);
    await shot(page, '06-bulk-rows-rtl');
    // phone width: the rows stack, nothing overflows horizontally
    await page.setViewportSize({ width: 390, height: 844 });
    const overflow = await page.getByTestId('entry-dialog').evaluate((el) => el.scrollWidth - el.clientWidth);
    expect(overflow).toBeLessThanOrEqual(1);
    await shot(page, '07-bulk-rows-rtl-phone');
    // unsaved rows: cancelling asks, discarding closes
    await page.getByTestId('entry-cancel').click();
    await expect(page.getByTestId('entry-close-confirm')).toBeVisible();
    await page.getByTestId('entry-close-discard').click();
    await expect(page.getByTestId('entry-dialog')).toBeHidden();
    // phone: the language selector lives in the user menu
    await page.getByTestId('user-menu').click();
    await page.getByTestId('lang-switcher-menu').selectOption('de');
  });
});
