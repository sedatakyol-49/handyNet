import { Page, expect, test } from '@playwright/test';
import {
  ADMIN_EMAIL,
  ADMIN_PASSWORD,
  Api,
  PASSWORD,
  RUN,
  company,
  hasAdmin,
  login,
  logout,
  drawSignature,
  publishAnkaufList,
  randomImei,
  yearsAgo,
} from './support/handynet';

/**
 * Ankauf device intake through the UI (Quote → Received → Inspected → Completed | Rejected):
 * 1. Admin publishes a price list (API, unique brand, right before use) and enables "Ankauf" + "Inventory" for a shop
 *    in the administration.
 * 2. The shop quotes the model (Gut, with IMEI), starts the intake, records an adult seller, inspects a worse
 *    condition (Akzeptabel + screen damage + battery 70 % → 300,00 € becomes 144,00 €, recalculated with the quote's
 *    price version and the default policy), pays 120,50 € (below the offer, after a client-side "above offer" check)
 *    and completes. The Lager page shows the device product with on-hand 1.
 * 3. A second shop: a 16-year-old seller is refused client-side, the IMEI stocked in (2) shows the in-stock warning,
 *    then a fresh IMEI is received and the intake is rejected with a reason.
 *
 * Like ankauf-flow.spec.ts this assumes the default pricing policy is active (it is only read).
 */
test.describe.serial('Ankauf intake', () => {
  test.skip(!hasAdmin, 'Set HN_ADMIN_EMAIL and HN_ADMIN_PASSWORD to run e2e tests against the API.');

  const brand = `E2EIN${RUN}`;
  const model = 'Intakephone 13';
  const shop = { ...company('shop'), name: `E2E Ankauf Intake ${RUN}`, email: `intake-shop-${RUN}@e2e.handynet.test` };
  const shop2 = { ...company('shop'), name: `E2E Ankauf Intake 2 ${RUN}`, email: `intake-shop2-${RUN}@e2e.handynet.test` };
  const imei = randomImei();

  test.beforeAll(async () => {
    const admin = await Api.login(ADMIN_EMAIL, ADMIN_PASSWORD);
    await admin.verifyWithModules(await Api.register(shop), ['B2BPurchasing']);
    await admin.verifyWithModules(await Api.register(shop2), ['B2BPurchasing', 'Ankauf', 'Inventory']);
    await admin.dispose();
  });

  /** Shop UI: search the model, choose the condition and (optionally) an IMEI, calculate, then "Ankauf starten". */
  async function quoteAndStart(page: Page, quoteImei: string | null): Promise<void> {
    await page.getByRole('navigation', { name: 'Hauptnavigation' }).getByRole('link', { name: 'Ankauf', exact: true }).click();
    await expect(page).toHaveURL(/\/app\/ankauf$/);
    await page.getByTestId('ak-search').fill(`${brand} ${model}`);
    await page.getByTestId('ak-models').getByRole('button', { name: new RegExp(model) }).click();
    await page.locator('input[data-condition="Gut"]').check();
    if (quoteImei) await page.getByTestId('ak-imei').fill(quoteImei);
    await page.getByTestId('ak-submit').click();
    await expect(page.getByTestId('ak-platform-offer')).toHaveText(/^300,00\s€$/);
    await page.getByTestId('ak-start-intake').click();
    await expect(page).toHaveURL(/\/app\/ankauf\/ankaeufe\/neu\//);
    await expect(page.getByTestId('intake-quote')).toContainText(`${brand} ${model} 128 GB`);
    await expect(page.getByTestId('intake-privacy')).toContainText('nur für Ihr Unternehmen sichtbar');
  }

  async function fillSeller(page: Page, birthDate: string): Promise<void> {
    await page.getByTestId('in-first').fill('Erika');
    await page.getByTestId('in-last').fill('Mustermann');
    await page.getByTestId('in-street').fill('Heidestraße 17');
    await page.getByTestId('in-zip').fill('51147');
    await page.getByTestId('in-city').fill('Köln');
    await page.getByTestId('in-birth').fill(birthDate);
    await page.getByTestId('in-idtype').selectOption({ label: 'Personalausweis' });
    await page.getByTestId('in-idno').fill('L01X00T47');
    await page.getByTestId('in-expiry').fill('2031-01-31');
  }

  test('admin publishes a price list and enables Ankauf + Inventory for the shop', async ({ page }) => {
    await publishAnkaufList(`E2E Intake ${RUN}`, [{ brand, model, storage: '128 GB', price: 300 }]);

    await login(page, ADMIN_EMAIL, ADMIN_PASSWORD);
    await page.goto('/app/administration');
    await page.getByTestId('admin-status').selectOption('');
    await page.getByTestId('admin-search').fill(shop.name);
    await page.getByTestId('admin-search').press('Enter');
    const row = page.locator(`tr[data-org="${shop.name}"]`);
    await expect(row).toBeVisible();
    await row.getByTestId('admin-modules').click();
    const dialog = page.getByRole('dialog');
    await dialog.locator('input[data-module="Ankauf"]').check();
    await dialog.locator('input[data-module="Inventory"]').check();
    await dialog.getByTestId('admin-modules-save').click();
    await expect(dialog).toBeHidden();
    await expect(row.getByText('Ankauf', { exact: true })).toBeVisible();
  });

  test('shop buys a device: receive, inspect, pay below the offer, stocked', async ({ page }) => {
    await login(page, shop.email, PASSWORD);
    await quoteAndStart(page, imei);

    // IMEI comes from the quote (read-only); client validation for missing seller fields.
    await expect(page.getByTestId('in-imei')).toHaveValue(imei);
    await page.getByTestId('in-receive').click();
    await expect(page.getByText('Pflichtfeld.').first()).toBeVisible();
    await expect(page.getByText('Bitte die Ausweisart auswählen.')).toBeVisible();

    await fillSeller(page, '1990-05-17');
    await page.getByTestId('in-receive').click();
    await expect(page).toHaveURL(/\/app\/ankauf\/ankaeufe\/[0-9a-f-]{36}$/);
    await expect(page.getByTestId('intake-status')).toHaveText('Angenommen');
    await expect(page.getByTestId('intake-imei')).toHaveText(imei);
    await expect(page.getByTestId('intake-seller')).toHaveText('Erika Mustermann');
    await expect(page.locator('[data-step="inspect"]')).toHaveAttribute('data-state', 'current');

    // Inspection: worse than quoted → recalculated with the quote's price version.
    const inspect = page.getByTestId('intake-inspect');
    await expect(inspect.locator('input[data-condition="Gut"]')).toBeChecked(); // prefilled from the quote
    await inspect.locator('input[data-condition="Akzeptabel"]').check();
    await inspect.locator('input[data-defect="ScreenDamage"]').check();
    await page.getByTestId('in-battery').fill('70');
    await page.getByTestId('in-notes').fill('Kratzer am Display, Ladekabel fehlt');
    await page.getByTestId('in-inspect').click();
    await expect(page.getByTestId('intake-status')).toHaveText('Geprüft');
    await expect(page.getByTestId('intake-quoted-offer')).toHaveText(/^300,00\s€$/);
    await expect(page.getByTestId('intake-inspected-offer')).toHaveText(/^144,00\s€$/);
    await expect(page.getByTestId('intake-difference')).toHaveText(/^-156,00\s€$/);
    await expect(page.getByTestId('intake-max-consumer')).toHaveText(/^122,40\s€$/);
    await expect(page.getByTestId('intake-quote-prices-note')).toContainText('nicht mit den heutigen Preisen');

    // Price + signature: prefilled with the max. consumer price; above the offer is refused client-side.
    await expect(page.locator('[data-step="sign"]')).toHaveAttribute('data-state', 'current');
    await expect(page.getByTestId('in-paid')).toHaveValue('122,40');
    await page.getByTestId('in-paid').fill('150');
    await page.getByTestId('in-sign').click();
    await expect(page.getByText('Der Betrag darf das geprüfte Angebot (144,00 €) nicht übersteigen.')).toBeVisible();
    await page.getByTestId('in-paid').fill('120,50');
    await page.locator('input[data-method="BankTransfer"]').check();
    await page.getByTestId('in-sign').click();
    await expect(page.getByText('Bitte unterschreiben lassen.')).toBeVisible(); // no signature drawn yet
    await expect(page.getByTestId('intake-declaration')).toContainText('alleiniger Eigentümer');
    await drawSignature(page, 'intake-signature-pad');
    await page.getByTestId('in-sign').click();
    await expect(page.getByTestId('intake-signature-state')).toHaveAttribute('data-state', 'valid');
    await expect(page.getByTestId('intake-signed-amount')).toHaveText(/^120,50\s€$/);

    // Payout (transfer: no till) and stock.
    await expect(page.getByTestId('in-warehouse')).toContainText('Standardlager');
    await page.getByTestId('in-complete').click();

    const dialog = page.getByRole('dialog');
    await expect(dialog).toContainText(imei);
    await expect(page.getByTestId('intake-confirm-amount')).toHaveText(/^120,50\s€$/);
    await expect(dialog).toContainText('Überweisung');
    await expect(dialog).toContainText('§25a');
    await page.getByTestId('intake-confirm-submit').click();
    await expect(dialog).toBeHidden();
    await expect(page.getByTestId('intake-status')).toHaveText('Abgeschlossen');
    await expect(page.getByTestId('intake-completed')).toContainText('120,50');
    await expect(page.getByTestId('intake-paid')).toHaveText(/^120,50\s€$/);
    await expect(page.getByTestId('intake-inspect')).toHaveCount(0);
    await expect(page.getByTestId('in-reject')).toHaveCount(0);

    // Ankaufbeleg: authenticated blob request returning a PDF, downloaded as a file.
    const [belegResponse, beleg] = await Promise.all([
      page.waitForResponse((r) => r.url().includes('/api/ankauf/intakes/') && r.url().endsWith('/beleg')),
      page.waitForEvent('download'),
      page.getByTestId('intake-beleg').click(),
    ]);
    expect(belegResponse.status()).toBe(200);
    expect(belegResponse.headers()['content-type']).toContain('application/pdf');
    expect(beleg.suggestedFilename()).toBe(`ankaufbeleg-${imei}.pdf`);

    // List "Ankäufe" in the navigation.
    await page.getByRole('navigation', { name: 'Hauptnavigation' }).getByRole('link', { name: 'Ankäufe' }).click();
    await expect(page).toHaveURL(/\/app\/ankauf\/ankaeufe$/);
    const row = page.locator(`[data-intake="${imei}"]`);
    await expect(row).toContainText('Abgeschlossen');
    await expect(row).toContainText('120,50');

    // Stock: device product with on-hand 1.
    await row.getByRole('link', { name: 'Öffnen' }).click();
    await page.getByTestId('intake-stock-link').click();
    await expect(page).toHaveURL(/\/app\/lager$/);
    const stockRow = page.getByTestId('stock-table').locator('tbody tr', { hasText: `${model} 128 GB` });
    await expect(stockRow).toHaveCount(1);
    await expect(stockRow).toContainText(brand);
    await expect(stockRow.locator('td').nth(3)).toHaveText('1');
    await logout(page);
  });

  test('minor seller is refused client-side; a stocked IMEI is refused; intake rejected', async ({ page }) => {
    await login(page, shop2.email, PASSWORD);
    await quoteAndStart(page, null);

    // 16-year-old seller: refused before any request.
    await fillSeller(page, yearsAgo(16));
    await page.getByTestId('in-imei').fill(imei);
    await page.getByTestId('in-receive').click();
    await expect(page.getByText('Der Verkäufer ist jünger als 18 Jahre. Ein Ankauf ist nicht zulässig.')).toBeVisible();
    await expect(page).toHaveURL(/\/app\/ankauf\/ankaeufe\/neu\//);

    // Adult seller, but the IMEI is already in stock at the first shop.
    await page.getByTestId('in-birth').fill(yearsAgo(30));
    await page.getByTestId('in-receive').click();
    await expect(page.getByTestId('intake-imei-in-stock')).toContainText('Gerät nicht ankaufen, Herkunft prüfen');
    await expect(page).toHaveURL(/\/app\/ankauf\/ankaeufe\/neu\//);

    // Another device: received, then rejected with a reason.
    await page.getByTestId('in-imei').fill(randomImei());
    await page.getByTestId('in-receive').click();
    await expect(page.getByTestId('intake-status')).toHaveText('Angenommen');
    await page.getByTestId('in-reject').click();
    const dialog = page.getByRole('dialog');
    await page.getByTestId('in-reject-confirm').click();
    await expect(dialog).toContainText('Bitte einen Grund angeben.');
    await page.getByTestId('in-reason').fill('Verkäufer hat es sich anders überlegt');
    await page.getByTestId('in-reject-confirm').click();
    await expect(dialog).toBeHidden();
    await expect(page.getByTestId('intake-status')).toHaveText('Abgelehnt');
    await expect(page.getByTestId('intake-rejected')).toContainText('Verkäufer hat es sich anders überlegt');
    await logout(page);
  });
});
