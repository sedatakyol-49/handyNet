import { Page, expect, test } from '@playwright/test';
import { ADMIN_EMAIL, ADMIN_PASSWORD, Api, PASSWORD, RUN, company, hasAdmin, login, randomEan } from './support/handynet';

/**
 * Card terminal flow with the simulated terminal against the real API (the e2e API runs in Development, where the
 * simulated terminal is allowed):
 * - the "Kartenterminal" page: create the profile through the UI, default, honest hints (test only, ZVT unverified);
 * - approved: the sale is booked with the card transaction and the receipt prints the terminal data (marked TEST);
 * - declined / aborted: back in the payment dialog with a plain message, nothing booked;
 * - no answer (outcome unknown): the cashier must check the terminal; "not booked" goes back, nothing is sold;
 * - approved but the API refuses the sale (stock gone): the card payment is reversed and the cashier is told so;
 * - reversal fails: the approved payment stays attached to the ticket, blocks discarding, survives a reload, shows up
 *   under "Kartenzahlungen ohne Beleg" and is reversed from there;
 * - a return refunded to the card runs the refund on the terminal and links it to the return receipt.
 */
test.describe.serial('POS card terminal (simulated)', () => {
  test.skip(!hasAdmin, 'Set HN_ADMIN_EMAIL and HN_ADMIN_PASSWORD to run e2e tests against the API.');

  const shop = { ...company('shop'), name: `E2E Kartenshop ${RUN}`, email: `karte-${RUN}@e2e.handynet.test` };
  const product = `Ladekabel Karte ${RUN}`;
  let orgId = '';
  let registerId = '';
  let warehouseId = '';
  let productId = '';

  interface CardTx {
    id: string;
    status: string;
    amountMinor: number;
    receiptId: string | null;
    isTest: boolean;
    kind: string;
  }

  async function transactions(): Promise<CardTx[]> {
    const api = await Api.login(shop.email, PASSWORD);
    try {
      return (await api.call<{ items: CardTx[] }>('GET', `/api/pos/card-transactions?registerId=${registerId}&pageSize=100`, orgId)).items;
    } finally {
      await api.dispose();
    }
  }

  async function adjustStock(delta: number): Promise<void> {
    const api = await Api.login(shop.email, PASSWORD);
    try {
      await api.call('POST', '/api/inventory/adjustments', orgId, { warehouseId, productId, delta, reason: 'E2E', type: delta > 0 ? 'Receipt' : 'Adjustment' }, 200);
    } finally {
      await api.dispose();
    }
  }

  test.beforeAll(async () => {
    orgId = await Api.register(shop);
    const admin = await Api.login(ADMIN_EMAIL, ADMIN_PASSWORD);
    await admin.verifyWithModules(orgId, ['Inventory', 'ProductImport', 'Pos']);
    await admin.dispose();
    const api = await Api.login(shop.email, PASSWORD);
    warehouseId = (await api.call<{ id: string }>('POST', '/api/inventory/warehouses', orgId, { code: 'LADEN', name: 'Laden' })).id;
    productId = (await api.call<{ id: string }>('POST', '/api/catalog/products', orgId, { brand: 'E2E', name: product, category: 'Accessory', ean: randomEan() })).id;
    await api.call('POST', '/api/inventory/adjustments', orgId, { warehouseId, productId, delta: 10, reason: 'E2E', type: 'Receipt' }, 200);
    await api.call('PUT', `/api/pos/prices/${productId}`, orgId, { priceMinor: 1250 });
    registerId = (await api.call<{ id: string }>('POST', '/api/pos/registers', orgId, { name: `Theke ${RUN}`, warehouseId })).id;
    await api.call('POST', `/api/pos/registers/${registerId}/sessions`, orgId, { openingCashMinor: 0 });
    await api.dispose();
  });

  /** One product in the cart, card for the whole amount, payment dialog open. */
  async function cartWithCard(page: Page, quantity = 1): Promise<void> {
    await page.goto(`/kasse/${registerId}`);
    const tile = page.getByTestId('pos-product').filter({ hasText: product });
    await expect(tile).toBeVisible();
    for (let i = 0; i < quantity; i++) await tile.click();
    await page.getByTestId('pos-pay').click();
    await page.getByTestId('pos-method-Card').click();
  }

  test('terminal page: create the simulated terminal, default for the till, honest hints', async ({ page }) => {
    await login(page, shop.email, PASSWORD);
    await page.goto(`/kasse/${registerId}/kartenterminal`);
    await expect(page.getByRole('heading', { name: 'Kartenterminal', level: 1 })).toBeVisible();
    await expect(page.getByTestId('pos-tab-terminals')).toHaveAttribute('aria-current', 'page');
    await expect(page.getByTestId('pos-terminal-unverified')).toContainText('nicht an echten Geräten');
    await expect(page.getByText('Noch kein Kartenterminal eingerichtet')).toBeVisible();
    await expect(page.getByTestId('pos-terminal-effective')).toContainText('Manuell bestätigen');
    await expect(page.getByTestId('pos-card-orphans-empty')).toBeVisible();

    // ZVT profile: the form validates host, port and password before the API is asked
    await page.getByTestId('pos-terminal-new').click();
    const form = page.getByTestId('pos-terminal-form');
    await form.getByTestId('tf-name').fill('ZVT Theke');
    await form.getByTestId('tf-connection').selectOption('ZvtBridge');
    await expect(form.getByTestId('tf-help')).toContainText('Nicht an Hardware geprüft');
    await form.getByTestId('tf-host').fill('http://192.168.1.60/x');
    await form.getByTestId('tf-password').fill('12');
    await form.getByTestId('tf-save').click();
    await expect(form.getByText('Bitte eine IP-Adresse oder einen Hostnamen ohne http:// und Pfad eingeben.')).toBeVisible();
    await expect(form.getByText('Das ZVT-Passwort besteht aus genau 6 Ziffern.')).toBeVisible();
    await form.getByTestId('tf-host').fill('127.0.0.1');
    await form.getByTestId('tf-port').fill('47998');
    await form.getByTestId('tf-password').fill('000000');
    await form.getByTestId('tf-bridge').fill('http://127.0.0.1:47997'); // nothing listens here: like a bridge that is not running
    await form.getByTestId('tf-save').click();
    await expect(form).toBeHidden();
    const zvt = page.locator('[data-terminal="ZVT Theke"]');
    await expect(zvt.getByTestId('pos-terminal-default')).toBeVisible(); // first profile = default
    await expect(zvt.getByTestId('pos-terminal-address')).toHaveText('127.0.0.1:47998 ← http://127.0.0.1:47997');
    await zvt.getByTestId('pos-terminal-test').click();
    await expect(zvt.getByTestId('pos-terminal-status')).toContainText('SEHA Bridge nicht erreichbar', { timeout: 30_000 });

    // simulated terminal becomes the default
    await page.getByTestId('pos-terminal-new').click();
    await form.getByTestId('tf-name').fill('Testterminal');
    await form.getByTestId('tf-connection').selectOption('Simulated');
    await expect(form.getByTestId('tf-help')).toContainText('Nur zum Testen');
    await form.getByTestId('tf-default').check();
    await form.getByTestId('tf-save').click();
    await expect(form).toBeHidden();
    const sim = page.locator('[data-terminal="Testterminal"]');
    await expect(sim.getByTestId('pos-terminal-default')).toBeVisible();
    await expect(sim.getByTestId('pos-terminal-test-badge')).toBeVisible();
    await expect(zvt.getByTestId('pos-terminal-default')).toHaveCount(0);
    await expect(page.getByTestId('pos-terminal-effective')).toContainText('Testterminal');

    // the ZVT profile is not needed any more
    await zvt.getByTestId('pos-terminal-delete').click();
    await page.getByTestId('pos-terminal-delete-confirm').click();
    await expect(zvt).toHaveCount(0);
  });

  test('approved: sale booked with the card transaction, receipt shows the terminal data', async ({ page }) => {
    await login(page, shop.email, PASSWORD);
    await cartWithCard(page, 2);
    await page.getByTestId('pos-validate').click();

    const dialog = page.getByTestId('pos-terminal-dialog');
    await expect(dialog).toBeVisible();
    await expect(dialog.getByTestId('pos-terminal-amount')).toHaveText('25,00 €');
    await expect(dialog.getByTestId('pos-terminal-test-note')).toContainText('TEST');
    await dialog.getByTestId('pos-terminal-sim-approve').click();

    await expect(page.getByTestId('pos-sale-done')).toBeVisible();
    const receipt = page.getByTestId('receipt');
    await expect(receipt.getByTestId('receipt-payments')).toContainText('Karte');
    await expect(receipt.getByTestId('receipt-card-line').first()).toContainText('TEST');
    await expect(receipt.getByTestId('receipt-payments')).toContainText('TEST girocard ····0010');
    await expect(receipt.getByTestId('receipt-payments')).toContainText('Trace-Nr.');

    const all = await transactions();
    expect(all).toHaveLength(1);
    expect(all[0]).toMatchObject({ status: 'Approved', amountMinor: 2500, isTest: true, kind: 'Payment' });
    expect(all[0].receiptId).toBeTruthy();
  });

  test('declined, aborted and unknown outcome: nothing is booked and the cashier is told what to do', async ({ page }) => {
    await login(page, shop.email, PASSWORD);
    await cartWithCard(page);
    const dialog = page.getByTestId('pos-terminal-dialog');
    const error = page.getByTestId('pos-checkout-error');

    await page.getByTestId('pos-validate').click();
    await dialog.getByTestId('pos-terminal-sim-decline').click();
    await expect(error).toContainText('Vom Terminal abgelehnt. Es wurde nichts gebucht.');
    await expect(page.getByTestId('pos-payment')).toBeVisible(); // still in the payment dialog
    await expect(page.getByTestId('pos-card-attached')).toHaveCount(0);

    await page.getByTestId('pos-validate').click();
    await dialog.getByTestId('pos-terminal-sim-abort').click();
    await expect(error).toContainText('Am Terminal abgebrochen. Es wurde nichts gebucht.');

    // no answer from the terminal: only a look at the terminal can tell
    await page.getByTestId('pos-validate').click();
    await dialog.getByTestId('pos-terminal-sim-timeout').click();
    const uncertain = page.getByTestId('pos-card-uncertain');
    await expect(uncertain).toBeVisible();
    await expect(uncertain).toContainText('Das Ergebnis am Terminal ist unbekannt');
    await expect(uncertain).toContainText('Prüfen Sie am Terminal');
    await uncertain.getByTestId('pos-card-uncertain-no').click();
    await expect(uncertain).toBeHidden();
    await expect(page.getByTestId('pos-payment')).toBeVisible();

    // unknown outcome, the cashier checked the terminal: the sale is booked as a card payment confirmed by hand
    await page.getByTestId('pos-validate').click();
    await dialog.getByTestId('pos-terminal-sim-timeout').click();
    await uncertain.getByTestId('pos-card-uncertain-yes').click();
    await expect(page.getByTestId('pos-sale-done')).toBeVisible();
    await expect(page.getByTestId('receipt').getByTestId('receipt-payments')).toContainText('Karte');
    await expect(page.getByTestId('receipt').getByTestId('receipt-card-line')).toHaveCount(0); // no terminal data

    const all = await transactions();
    expect(all.map((t) => t.status).sort()).toEqual(['Aborted', 'Approved', 'Declined', 'Failed', 'Failed']);
    expect(all.filter((t) => t.receiptId)).toHaveLength(1); // only the approved sale of the previous test is linked
  });

  test('approved but the sale fails: the card payment is reversed and the cashier is told', async ({ page }) => {
    await login(page, shop.email, PASSWORD);
    await cartWithCard(page);
    // Someone else sells the remaining stock between scanning and paying: the API will refuse the sale.
    await adjustStock(-7);
    await page.getByTestId('pos-validate').click();
    const dialog = page.getByTestId('pos-terminal-dialog');
    await dialog.getByTestId('pos-terminal-sim-approve').click();
    // The sale is refused → the till starts the reversal on the terminal (the simulator asks again).
    await expect(dialog).toContainText('Kartenzahlung stornieren');
    await dialog.getByTestId('pos-terminal-sim-approve').click();

    const error = page.getByTestId('pos-checkout-error');
    await expect(error).toContainText('Der Verkauf wurde nicht gebucht');
    await expect(error).toContainText('Die Kartenzahlung über 12,50 € wurde am Terminal storniert');
    await expect(page.getByTestId('pos-card-attached')).toHaveCount(0);
    await expect(page.getByTestId('pos-sale-done')).toHaveCount(0);

    const all = await transactions();
    const reversed = all.filter((t) => t.status === 'Reversed');
    expect(reversed).toHaveLength(1);
    expect(reversed[0]).toMatchObject({ amountMinor: 1250, receiptId: null });
  });

  test('reversal fails: the charge stays attached, survives a reload, and a manager reverses it from the list', async ({ page }) => {
    await login(page, shop.email, PASSWORD);
    await adjustStock(1); // one piece is on the shelf when it is scanned …
    await cartWithCard(page);
    await adjustStock(-1); // … and gone when the sale reaches the API
    await page.getByTestId('pos-validate').click();
    const dialog = page.getByTestId('pos-terminal-dialog');
    await dialog.getByTestId('pos-terminal-sim-approve').click();
    await expect(dialog).toContainText('Kartenzahlung stornieren');
    await dialog.getByTestId('pos-terminal-sim-decline').click(); // the terminal refuses the reversal

    const error = page.getByTestId('pos-checkout-error');
    await expect(error).toContainText('ACHTUNG');
    await expect(error).toContainText('12,50 € ist genehmigt und konnte NICHT storniert werden');
    const attached = page.getByTestId('pos-card-attached');
    await expect(attached).toContainText('Kartenzahlung genehmigt: 12,50 €');

    // the approved charge is stored with the ticket: a reload keeps it, and the ticket cannot be discarded
    await page.reload();
    await page.getByTestId('pos-pay').click();
    await expect(attached).toContainText('Kartenzahlung genehmigt: 12,50 €');
    await page.getByTestId('pos-pay-back').click();
    await page.getByTestId('pos-discard').click();
    await expect(page.getByTestId('pos-discard-blocked')).toContainText('Kartenzahlung über 12,50 € genehmigt');
    await expect(page.getByTestId('pos-discard-confirm')).toHaveCount(0);
    await page.locator('dialog[open]').getByTestId('dialog-close').click();
    await expect(page.getByTestId('pos-discard-blocked')).toBeHidden();

    // changing the card amount is refused until the payment is reversed
    await page.getByTestId('pos-pay').click();
    await page.getByTestId('pos-pay-amount').fill('10');
    await page.getByTestId('pos-method-Cash').click();
    await page.getByTestId('pos-validate').click();
    await expect(error).toContainText('bereits eine Kartenzahlung über 12,50 €');

    // managers see it under "Kartenzahlungen ohne Beleg" and reverse it there
    await page.goto(`/kasse/${registerId}/kartenterminal`);
    const orphan = page.getByTestId('pos-card-orphan');
    await expect(orphan).toHaveCount(1);
    await expect(orphan.getByTestId('pos-card-orphan-amount')).toHaveText('12,50 €');
    await orphan.getByTestId('pos-card-orphan-reverse').click();
    await page.getByTestId('pos-card-orphan-confirm').click();
    await page.getByTestId('pos-terminal-dialog').getByTestId('pos-terminal-sim-approve').click();
    await expect(page.getByTestId('pos-card-orphans-empty')).toBeVisible();

    // back at the till the ticket learns that the payment is gone and can be paid again or discarded
    await page.goto(`/kasse/${registerId}`);
    await page.getByTestId('pos-pay').click();
    await page.getByTestId('pos-card-reverse').click();
    await expect(page.getByTestId('pos-checkout-error')).toContainText('nicht mehr offen');
    await expect(attached).toHaveCount(0);

    const all = await transactions();
    expect(all.filter((t) => t.status === 'Approved' && !t.receiptId)).toHaveLength(0);
    expect(all.filter((t) => t.status === 'Reversed')).toHaveLength(2);
  });

  test('return with a card refund: the refund runs on the terminal and is linked to the return receipt', async ({ page }) => {
    const api = await Api.login(shop.email, PASSWORD);
    const sold = (await api.call<{ items: { id: string; number: number }[] }>('GET', `/api/pos/sales?registerId=${registerId}&pageSize=50`, orgId)).items.find((r) => r.number === 1)!;
    await api.dispose();

    await login(page, shop.email, PASSWORD);
    await page.goto(`/kasse/belege/${sold.id}`);
    await page.getByTestId('pos-start-return').click();
    await page.getByTestId('pos-return-qty').fill('1');
    await page.getByTestId('pos-refund-Card').check();
    await page.getByTestId('pos-return-reason').fill('Kabel defekt');
    await page.getByTestId('pos-return-submit').click();

    // declined on the terminal: nothing is booked, the cashier is told in the dialog
    await page.getByTestId('pos-return-confirm').click();
    const dialog = page.getByTestId('pos-terminal-dialog');
    await expect(dialog).toContainText('Gutschrift auf Karte');
    await expect(dialog.getByTestId('pos-terminal-amount')).toHaveText('12,50 €');
    await dialog.getByTestId('pos-terminal-sim-decline').click();
    await expect(page.getByTestId('pos-return-dialog-error')).toContainText('Vom Terminal abgelehnt. Es wurde nichts gebucht.');

    // approved: the return receipt is booked with the terminal data
    await page.getByTestId('pos-return-confirm').click();
    await dialog.getByTestId('pos-terminal-sim-approve').click();
    await expect(page).not.toHaveURL(new RegExp(sold.id));
    const receipt = page.getByTestId('receipt');
    await expect(receipt).toContainText('-12,50 €');
    await expect(receipt.getByTestId('receipt-card-line').first()).toContainText('TEST');

    const refunds = (await transactions()).filter((t) => t.kind === 'Refund');
    expect(refunds.map((t) => t.status).sort()).toEqual(['Approved', 'Declined']);
    expect(refunds.find((t) => t.status === 'Approved')).toMatchObject({ amountMinor: 1250, isTest: true });
    expect(refunds.find((t) => t.status === 'Approved')!.receiptId).toBeTruthy();
  });
});
