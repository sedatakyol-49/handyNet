import { Page, expect, test } from '@playwright/test';
import { ADMIN_EMAIL, ADMIN_PASSWORD, Api, PASSWORD, RUN, company, hasAdmin, login, logout, priceListCsv, randomEan } from './support/handynet';

/**
 * Disputes (Reklamationen) through the UI. Delivery is prepared via the API (place → confirm → ship → confirm receipt).
 * 1. Buyer reports a problem → seller writes a message and hands the case to SEHA → the platform admin decides
 *    "keine Erstattung" in the administration → the order is Completed.
 * 2. Buyer reports a problem → seller grants a refund → the order is Refunded ("Erstattet").
 * 3. Paid online: the payout is on hold while disputed; after the refund the payment is refunded by a background job.
 */
test.describe.serial('disputes', () => {
  test.skip(!hasAdmin, 'Set HN_ADMIN_EMAIL and HN_ADMIN_PASSWORD to run e2e tests against the API.');

  const supplier = { ...company('supplier'), name: `E2E Reklamation Lieferant ${RUN}`, email: `disp-supplier-${RUN}@e2e.handynet.test` };
  const shop = { ...company('shop'), name: `E2E Reklamation Shop ${RUN}`, email: `disp-shop-${RUN}@e2e.handynet.test` };
  const sku = `DS-${RUN}-1`;
  const productName = `iPhone Akku Reklamation ${RUN}`;
  let escalated = { id: '', number: '' };
  let refunded = { id: '', number: '' };
  let paid = { id: '', number: '' };
  let shopOrgId = '';
  let supplierOrgId = '';

  test.beforeAll(async () => {
    const supplierOrg = await Api.register(supplier);
    const shopOrg = await Api.register(shop);
    const admin = await Api.login(ADMIN_EMAIL, ADMIN_PASSWORD);
    await admin.verifyWithModules(supplierOrg, ['ProductImport', 'Inventory', 'B2BSales']);
    await admin.verifyWithModules(shopOrg, ['B2BPurchasing']);
    await admin.dispose();

    supplierOrgId = supplierOrg;
    shopOrgId = shopOrg;
    const s = await Api.login(supplier.email, PASSWORD);
    await s.importAndCommit(supplierOrg, priceListCsv([{ sku, ean: randomEan(), name: productName, price: '25,00', stock: 20 }]));
    const b = await Api.login(shop.email, PASSWORD);

    escalated = await b.placeOrder(shopOrg, productName, sku, 2);
    refunded = await b.placeOrder(shopOrg, productName, sku, 1);
    paid = await b.placeOrder(shopOrg, productName, sku, 3);
    for (const o of [escalated, refunded]) {
      await s.confirmOrder(supplierOrg, o.id);
      await s.shipOrder(supplierOrg, o.id);
      await b.confirmDelivery(shopOrg, o.id);
    }
    await s.dispose();
    await b.dispose();
  });

  /** Buyer reports a problem on a delivered order through the "Reklamation" section. */
  async function reportProblem(page: Page, orderId: string, reason: string, description: string): Promise<void> {
    await page.goto(`/app/bestellungen/${orderId}`);
    await expect(page.getByTestId('order-status')).toHaveText('Zugestellt');
    const section = page.getByTestId('dispute-section');
    await expect(section.getByTestId('report-hint')).toContainText('keine Auszahlung');
    await section.getByTestId('report-problem').click();

    const form = section.getByTestId('dispute-form');
    await expect(form.getByTestId('dispute-hold-note')).toContainText('automatische Fertigstellung der Bestellung gestoppt');
    // Client-side validation before anything is sent.
    await form.getByTestId('submit-dispute').click();
    await expect(form.getByText('Bitte wählen Sie einen Grund.')).toBeVisible();
    await expect(form.getByText('Pflichtfeld.')).toBeVisible();

    await form.getByLabel('Grund').selectOption({ label: reason });
    await form.getByLabel('Beschreibung').fill(description);
    await form.getByTestId('submit-dispute').click();

    await expect(page.getByTestId('order-status')).toHaveText('In Reklamation');
    const card = section.getByTestId('dispute-card');
    await expect(card.getByTestId('dispute-status')).toHaveText('Offen');
    await expect(card.getByTestId('dispute-reason')).toHaveText(reason);
    const first = card.getByTestId('dispute-message').first();
    await expect(first).toContainText('Käufer');
    await expect(first).toContainText(description);
    await expect(first).toHaveAttribute('data-own', 'true');
    // Only one active dispute: the report button is gone; the buyer may withdraw but not refund.
    await expect(section.getByTestId('report-problem')).toHaveCount(0);
    await expect(card.getByTestId('dispute-withdraw')).toBeVisible();
    await expect(card.getByTestId('dispute-refund')).toHaveCount(0);
  }

  test('buyer reports a problem', async ({ page }) => {
    await login(page, shop.email, PASSWORD);
    await reportProblem(page, escalated.id, 'Ware beschädigt', 'Zwei Akkus kamen mit aufgeblähtem Gehäuse an.');

    // Purchases list: new status and filter.
    await page.goto('/app/bestellungen/einkauf?status=Disputed');
    await expect(page.getByTestId('orders-table').getByRole('row').filter({ hasText: escalated.number })).toContainText('In Reklamation');
    await logout(page);
  });

  test('seller sees the complaint, writes a message and hands it over to SEHA', async ({ page }) => {
    await login(page, supplier.email, PASSWORD);
    // Dashboard counter of open complaints (Disputed sales) of this new company.
    await page.goto('/app/uebersicht');
    await expect(page.getByRole('link', { name: /Offene Reklamationen/ })).toContainText('1');

    await page.goto(`/app/bestellungen/${escalated.id}`);
    await expect(page.getByTestId('order-status')).toHaveText('In Reklamation');
    const section = page.getByTestId('dispute-section');
    const card = section.getByTestId('dispute-card');
    await expect(card.getByTestId('dispute-status')).toHaveText('Offen');
    await expect(card.getByTestId('dispute-explain')).toContainText('Abschluss und Auszahlung sind ausgesetzt');
    const buyerMessage = card.getByTestId('dispute-message').first();
    await expect(buyerMessage).toContainText('Käufer');
    await expect(buyerMessage).toHaveAttribute('data-own', 'false');
    await expect(card.getByTestId('dispute-withdraw')).toHaveCount(0);
    await expect(card.getByTestId('dispute-refund')).toBeVisible();
    await expect(section.getByTestId('report-problem')).toHaveCount(0);

    // Empty message is rejected client-side.
    await card.getByTestId('dispute-send').click();
    await expect(card.getByText('Pflichtfeld.')).toBeVisible();
    await card.getByTestId('dispute-message-input').fill('Die Akkus wurden geprüft verschickt. Bitte senden Sie Fotos.');
    await card.getByTestId('dispute-send').click();
    const sellerMessage = card.locator('[data-testid="dispute-message"][data-from="Seller"]');
    await expect(sellerMessage).toContainText('Verkäufer');
    await expect(sellerMessage).toContainText('Bitte senden Sie Fotos.');
    await expect(sellerMessage).toHaveAttribute('data-own', 'true');
    await expect(card.getByTestId('dispute-message-input')).toHaveValue('');

    // Escalate (optional text) through the confirmation dialog.
    await card.getByTestId('dispute-escalate').click();
    const dialog = page.getByRole('dialog');
    await expect(dialog.getByTestId('dispute-dialog-text')).toContainText('SEHA prüft den Fall');
    await dialog.getByLabel('Nachricht an SEHA').fill('Wir bitten um Entscheidung durch SEHA.');
    await dialog.getByTestId('dispute-confirm').click();
    await expect(card.getByTestId('dispute-status')).toHaveText('Bei SEHA');
    await expect(card.getByTestId('dispute-escalate')).toHaveCount(0);
    await expect(card.getByTestId('dispute-message')).toHaveCount(3);
    await expect(page.getByTestId('order-status')).toHaveText('In Reklamation');
    await logout(page);
  });

  test('platform admin decides "keine Erstattung"', async ({ page }) => {
    await login(page, ADMIN_EMAIL, ADMIN_PASSWORD);
    await page.goto('/app/administration');
    await page.getByTestId('admin-tabs').getByRole('link', { name: 'Reklamationen' }).click();
    await expect(page).toHaveURL(/\/app\/administration\/reklamationen$/);

    const row = page.getByTestId('admin-disputes-table').locator(`tbody tr[data-order-id="${escalated.id}"]`);
    await expect(row).toContainText(escalated.number);
    await expect(row).toContainText('Ware beschädigt');
    await row.getByTestId('admin-dispute-open').click();

    await expect(page.getByTestId('admin-dispute-status')).toHaveText('Bei SEHA');
    const thread = page.getByTestId('dispute-thread');
    await expect(thread.getByTestId('dispute-message')).toHaveCount(3);
    await expect(thread).toContainText('aufgeblähtem Gehäuse');
    await expect(thread).toContainText('Bitte senden Sie Fotos.');
    await expect(thread).toContainText('Wir bitten um Entscheidung');

    const form = page.getByTestId('admin-decision-form');
    await form.getByTestId('decision-submit').click();
    await expect(form.getByText('Bitte wählen Sie eine Entscheidung.')).toBeVisible();
    await form.getByLabel('Keine Erstattung').check();
    await form.getByLabel('Begründung').fill('Transportschaden nicht belegt; keine Fotos eingereicht.');
    await form.getByTestId('decision-submit').click();

    const dialog = page.getByRole('dialog');
    await expect(dialog.getByTestId('decision-confirm-text')).toContainText('ohne Erstattung geschlossen');
    await dialog.getByTestId('decision-confirm').click();
    await expect(page.getByTestId('admin-dispute-status')).toHaveText('Ohne Erstattung geschlossen');
    await expect(page.getByTestId('admin-dispute-resolution')).toContainText('Transportschaden nicht belegt');
    await expect(page.getByTestId('admin-decision-form')).toHaveCount(0);

    // Gone from the queue.
    await page.getByRole('link', { name: '← Reklamationen' }).click();
    await expect(page.getByTestId('admin-disputes-table').locator(`tbody tr[data-order-id="${escalated.id}"]`)).toHaveCount(0);
    await logout(page);
  });

  test('buyer sees the completed order with the platform decision', async ({ page }) => {
    await login(page, shop.email, PASSWORD);
    await page.goto(`/app/bestellungen/${escalated.id}`);
    await expect(page.getByTestId('order-status')).toHaveText('Abgeschlossen');
    const card = page.getByTestId('dispute-section').getByTestId('dispute-card');
    await expect(card.getByTestId('dispute-status')).toHaveText('Ohne Erstattung geschlossen');
    await expect(card.getByTestId('dispute-resolved-by')).toHaveText('Plattform');
    await expect(card.getByTestId('dispute-resolution')).toContainText('Transportschaden nicht belegt');
    // The decision is stored once, as the resolution — not duplicated as a thread message.
    await expect(card.locator('[data-testid="dispute-message"][data-from="Platform"]')).toHaveCount(0);
    // Closed: no further actions or messages.
    await expect(card.getByTestId('dispute-message-form')).toHaveCount(0);
    await expect(card.getByTestId('dispute-withdraw')).toHaveCount(0);
    await expect(card.getByTestId('dispute-escalate')).toHaveCount(0);
    await logout(page);
  });

  test('buyer reports a second problem and the seller grants a refund', async ({ page }) => {
    await login(page, shop.email, PASSWORD);
    await reportProblem(page, refunded.id, 'Falscher Artikel', 'Statt iPhone-Akkus wurden Samsung-Akkus geliefert.');
    await logout(page);

    await login(page, supplier.email, PASSWORD);
    await page.goto(`/app/bestellungen/${refunded.id}`);
    const card = page.getByTestId('dispute-section').getByTestId('dispute-card');
    await expect(card.getByTestId('dispute-status')).toHaveText('Offen');
    await card.getByTestId('dispute-refund').click();
    const dialog = page.getByRole('dialog');
    await expect(dialog.getByTestId('dispute-dialog-text')).toContainText('vollen Bestellbetrag zurück');
    // The text is required.
    await dialog.getByTestId('dispute-confirm').click();
    await expect(dialog.getByText('Pflichtfeld.')).toBeVisible();
    await dialog.getByLabel('Begründung für die Erstattung').fill('Verwechslung beim Kommissionieren, volle Erstattung.');
    await dialog.getByTestId('dispute-confirm').click();

    await expect(page.getByTestId('order-status')).toHaveText('Erstattet');
    await expect(card.getByTestId('dispute-status')).toHaveText('Erstattet');
    await expect(card.getByTestId('dispute-resolved-by')).toHaveText('Verkäufer');
    await expect(card.getByTestId('dispute-resolution')).toContainText('Verwechslung beim Kommissionieren');
    await expect(card.getByTestId('dispute-refund')).toHaveCount(0);
    await logout(page);

    await login(page, shop.email, PASSWORD);
    await page.goto(`/app/bestellungen/${refunded.id}`);
    await expect(page.getByTestId('order-status')).toHaveText('Erstattet');
    await expect(page.getByTestId('dispute-section').getByTestId('dispute-status')).toHaveText('Erstattet');
  });

  test('paid order: payout on hold while disputed, payment refunded after the seller grants a refund', async ({ page }) => {
    await login(page, shop.email, PASSWORD);
    await page.goto(`/app/bestellungen/${paid.id}`);
    const payment = page.getByTestId('payment-section');
    await payment.getByTestId('pay-online').click();
    await page.waitForURL(/\/api\/payments\/simulated\//);
    await page.getByRole('button', { name: 'Bezahlen', exact: true }).click();
    await page.waitForURL(new RegExp(`/app/bestellungen/${paid.id}$`));
    await expect(payment.getByTestId('payment-status')).toHaveText('Bezahlt');

    const s = await Api.login(supplier.email, PASSWORD);
    await s.confirmOrder(supplierOrgId, paid.id);
    await s.shipOrder(supplierOrgId, paid.id);
    await s.dispose();
    const b = await Api.login(shop.email, PASSWORD);
    await b.confirmDelivery(shopOrgId, paid.id);
    await b.dispose();

    await reportProblem(page, paid.id, 'Defekt / funktioniert nicht', 'Ein Akku lädt nicht.');
    await logout(page);

    await login(page, supplier.email, PASSWORD);
    await page.goto(`/app/bestellungen/${paid.id}`);
    const state = page.getByTestId('payment-section').getByTestId('payout-state');
    await expect(state).toHaveAttribute('data-state', 'onHold');
    await expect(state).toContainText('Auszahlung ist ausgesetzt');

    const card = page.getByTestId('dispute-section').getByTestId('dispute-card');
    await card.getByTestId('dispute-refund').click();
    await page.getByRole('dialog').getByLabel('Begründung für die Erstattung').fill('Defekt bestätigt.');
    await page.getByRole('dialog').getByTestId('dispute-confirm').click();
    await expect(page.getByTestId('order-status')).toHaveText('Erstattet');
    // The refund job runs a few seconds later; the page re-reads the payments once on its own (no reload here).
    await expect(page.getByTestId('payment-section').getByTestId('payment-status')).toHaveText('Erstattet', { timeout: 20_000 });
    await expect(state).toHaveAttribute('data-state', 'refunded');
  });
});
