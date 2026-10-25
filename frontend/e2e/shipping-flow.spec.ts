import { expect, test } from '@playwright/test';
import { ADMIN_EMAIL, ADMIN_PASSWORD, Api, PASSWORD, RUN, company, hasAdmin, login, logout, priceListCsv, randomEan } from './support/handynet';

/**
 * Shipping and delivery lifecycle through the UI:
 * seller books a Simulated shipment, downloads its (simulation) label and cancels it; then books a Manual
 * shipment with a tracking number and hands it over → order Shipped. The buyer confirms receipt → Delivered
 * (inspection window shown) and accepts the goods → Completed.
 * (Simulated tracking needs 2 minutes of transit in dev, so delivery is driven by the buyer via the Manual path.)
 */
test.describe.serial('shipping and delivery', () => {
  test.skip(!hasAdmin, 'Set HN_ADMIN_EMAIL and HN_ADMIN_PASSWORD to run e2e tests against the API.');

  const supplier = { ...company('supplier'), name: `E2E Versand Lieferant ${RUN}`, email: `ship-supplier-${RUN}@e2e.handynet.test` };
  const shop = { ...company('shop'), name: `E2E Versand Shop ${RUN}`, email: `ship-shop-${RUN}@e2e.handynet.test` };
  const sku = `SH-${RUN}-1`;
  const productName = `iPad Display Versand ${RUN}`;
  const tracking = `E2E${RUN}`.toUpperCase();
  let order = { id: '', number: '' };

  test.beforeAll(async () => {
    const supplierOrg = await Api.register(supplier);
    const shopOrg = await Api.register(shop);
    const admin = await Api.login(ADMIN_EMAIL, ADMIN_PASSWORD);
    await admin.verifyWithModules(supplierOrg, ['ProductImport', 'Inventory', 'B2BSales']);
    await admin.verifyWithModules(shopOrg, ['B2BPurchasing']);
    await admin.dispose();

    const s = await Api.login(supplier.email, PASSWORD);
    await s.importAndCommit(supplierOrg, priceListCsv([{ sku, ean: randomEan(), name: productName, price: '300,00', stock: 5 }]));

    // 2 × 300,00 € net = 714,00 € gross → above the 500 € insurance threshold.
    const b = await Api.login(shop.email, PASSWORD);
    order = await b.placeOrder(shopOrg, productName, sku, 2);
    expect(order.number).toBeTruthy();
    await b.dispose();

    await s.confirmOrder(supplierOrg, order.id);
    await s.dispose();
  });

  test('seller books a simulated label, downloads it, cancels it, then ships via a manual booking', async ({ page }) => {
    await login(page, supplier.email, PASSWORD);
    await page.goto(`/app/bestellungen/${order.id}`);
    await expect(page.getByTestId('order-status')).toHaveText('Bestätigt');

    const section = page.getByTestId('shipping-section');
    const form = section.getByTestId('shipment-form');
    await expect(form).toBeVisible();
    await expect(form.getByTestId('ship-declared-value')).toContainText('714,00');
    // The insurance threshold depends on the carrier's profile (GET /api/shipments/carrier-profiles).
    await expect(form.getByLabel('Versichert versenden')).not.toBeChecked();
    await expect(form).toContainText('Standardhaftung des gewählten Versanddienstleisters');
    await expect(form).toContainText('noch keine Übergabe');

    // Client-side validation before anything is sent.
    await form.getByTestId('create-shipment').click();
    await expect(form.getByText('Pflichtfeld.')).toBeVisible();

    // Simulated carrier: limits are shown; goods value 714 € > standard liability 500 € → insurance pre-checked.
    await form.getByLabel('Versanddienstleister').selectOption('Simulated');
    await expect(form.getByTestId('carrier-limits')).toContainText('Standardhaftung bis 500,00');
    await expect(form.getByTestId('carrier-limits')).toContainText('31,5 kg');
    await expect(form.getByLabel('Versichert versenden')).toBeChecked();
    await expect(form).toContainText('über der Standardhaftung von 500,00');
    await expect(form).toContainText('muss versichert werden');
    // No tracking number field for Simulated.
    await expect(form.getByLabel('Sendungsnummer')).toHaveCount(0);
    await form.getByLabel('Gewicht (kg)').fill('1,25');
    await form.getByLabel('Länge').fill('30');
    await form.getByLabel('Breite').fill('20');
    await form.getByLabel('Höhe').fill('10');
    await form.getByTestId('create-shipment').click();

    const card = section.getByTestId('shipment-card').first();
    await expect(card.getByTestId('shipment-status')).toHaveText('Etikett erstellt');
    await expect(card).toContainText('1,25 kg');
    await expect(card).toContainText('Simulation – kein gültiges Versandetikett');
    // Creating a label is not a handover: the order is still Confirmed.
    await expect(page.getByTestId('order-status')).toHaveText('Bestätigt');
    await expect(card).toContainText('noch nicht verlassen');
    await expect(page.getByTestId('ship-order')).toHaveCount(0);

    // Label download: authenticated blob request returning a PDF.
    const [response, download] = await Promise.all([
      page.waitForResponse((r) => r.url().includes('/api/shipments/') && r.url().endsWith('/label')),
      page.waitForEvent('download'),
      card.getByTestId('download-label').click(),
    ]);
    expect(response.status()).toBe(200);
    expect(response.headers()['content-type']).toContain('application/pdf');
    expect((await response.body()).subarray(0, 5).toString('latin1')).toBe('%PDF-');
    expect(download.suggestedFilename()).toMatch(/^versandetikett-SIM.+\.pdf$/);

    // Cancel before handover.
    await card.getByTestId('cancel-shipment').click();
    await page.getByRole('dialog').getByTestId('confirm-action').click();
    await expect(section.getByTestId('shipment-card').first().getByTestId('shipment-status')).toHaveText('Storniert');

    // Manual booking: tracking number is required.
    await expect(form).toBeVisible();
    await form.getByLabel('Versanddienstleister').selectOption('Manual');
    await form.getByLabel('Gewicht (kg)').fill('2');
    await form.getByLabel('Länge').fill('40');
    await form.getByLabel('Breite').fill('30');
    await form.getByLabel('Höhe').fill('15');
    await form.getByTestId('create-shipment').click();
    await expect(form.getByText('Bei manueller Buchung ist die Sendungsnummer erforderlich.')).toBeVisible();
    await form.getByLabel('Sendungsnummer').fill(tracking);
    await form.getByTestId('create-shipment').click();

    const manual = section.locator('[data-testid="shipment-card"][data-status="LabelCreated"]');
    await expect(manual.getByTestId('shipment-tracking')).toHaveText(tracking);
    await expect(manual.getByTestId('download-label')).toHaveCount(0);
    await expect(page.getByTestId('order-status')).toHaveText('Bestätigt');

    await manual.getByTestId('handover-shipment').click();
    const dialog = page.getByRole('dialog');
    await expect(dialog).toContainText('Bestand ausgebucht');
    await dialog.getByTestId('confirm-action').click();

    await expect(page.getByTestId('order-status')).toHaveText('Versendet');
    const handed = section.locator('[data-testid="shipment-card"][data-status="HandedOver"]');
    await expect(handed.getByTestId('shipment-status')).toHaveText('Übergeben');
    await expect(handed.getByTestId('cancel-shipment')).toHaveCount(0);
    await logout(page);
  });

  test('buyer confirms receipt, sees the inspection deadline and accepts the goods', async ({ page }) => {
    await login(page, shop.email, PASSWORD);
    await page.goto(`/app/bestellungen/${order.id}`);
    await expect(page.getByTestId('order-status')).toHaveText('Versendet');

    const section = page.getByTestId('shipping-section');
    const card = section.locator('[data-testid="shipment-card"][data-status="HandedOver"]');
    await expect(card.getByTestId('shipment-tracking')).toHaveText(tracking);
    // Read-only for the buyer.
    await expect(section.getByTestId('handover-shipment')).toHaveCount(0);
    await expect(section.getByTestId('download-label')).toHaveCount(0);
    await expect(section.getByTestId('shipment-form')).toHaveCount(0);

    await section.getByTestId('confirm-delivery').click();
    await page.getByRole('dialog').getByTestId('confirm-action').click();
    await expect(page.getByTestId('order-status')).toHaveText('Zugestellt');

    const inspection = section.getByTestId('inspection');
    await expect(inspection).toContainText('vor Ablauf der Prüffrist gemeldet');
    await expect(inspection.getByTestId('inspection-ends')).not.toHaveText('–');
    // 48 h window just started → "1 T. 23 Std. 59 Min." or "2 T. 0 Std. 0 Min.".
    await expect(inspection.getByTestId('inspection-remaining')).toHaveText(/^(1 T\. 23 Std\. \d+ Min\.|2 T\. 0 Std\. 0 Min\.)$/);

    await inspection.getByTestId('accept-goods').click();
    await page.getByRole('dialog').getByTestId('confirm-action').click();
    await expect(page.getByTestId('order-status')).toHaveText('Abgeschlossen');
    await expect(section).toContainText('Bestellung abgeschlossen am');

    // The purchases list shows and filters the new status.
    await page.getByRole('navigation', { name: 'Hauptnavigation' }).getByRole('link', { name: 'Einkauf', exact: true }).click();
    await page.getByRole('group', { name: 'Status' }).getByRole('button', { name: 'Abgeschlossen' }).click();
    const row = page.getByTestId('orders-table').getByRole('row').filter({ hasText: order.number });
    await expect(row).toContainText('Abgeschlossen');
  });
});
