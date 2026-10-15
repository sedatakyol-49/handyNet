import { expect, test } from '@playwright/test';
import {
  ADMIN_EMAIL,
  ADMIN_PASSWORD,
  Api,
  PASSWORD,
  RUN,
  ankaufPriceListXlsx,
  company,
  hasAdmin,
  localDate,
  localTime,
  login,
  logout,
} from './support/handynet';

/**
 * Ankauf (used-device buyback) through the UI:
 * 1. Platform admin uploads a generated XLSX price list (new models → pending), approves the pending prices in the
 *    review and publishes the list (valid from now, so it becomes the current list).
 * 2. Admin enables the module "Ankauf" for a verified shop.
 * 3. The shop searches the model, creates a quote (Neu + housing damage) and sees the numbers of the default policy:
 *    300,00 € → Neu +20 % → 360,00 € → housing −10 % → 324,00 €; max. consumer price 275,40 € (−15 %).
 *
 * Price lists and policies are global in the dev database: the brand is unique per run, and the active policy is
 * only read, never changed.
 */
test.describe.serial('Ankauf', () => {
  test.skip(!hasAdmin, 'Set HN_ADMIN_EMAIL and HN_ADMIN_PASSWORD to run e2e tests against the API.');

  const brand = `E2E${RUN}`;
  const listLabel = `E2E Ankauf ${RUN}`;
  const shop = { ...company('shop'), name: `E2E Ankauf Shop ${RUN}`, email: `ankauf-shop-${RUN}@e2e.handynet.test` };

  test.beforeAll(async () => {
    const shopOrg = await Api.register(shop);
    const admin = await Api.login(ADMIN_EMAIL, ADMIN_PASSWORD);
    await admin.verifyWithModules(shopOrg, ['B2BPurchasing']);
    await admin.dispose();
  });

  test('admin uploads, reviews and publishes a price list', async ({ page }) => {
    await login(page, ADMIN_EMAIL, ADMIN_PASSWORD);
    await page.goto('/app/administration');
    await page.getByTestId('admin-tabs').getByRole('link', { name: 'Ankaufspreise' }).click();
    await expect(page).toHaveURL(/\/app\/administration\/ankauf$/);

    // Defaults: first to last day of next month, browser time zone is stated.
    const now = new Date();
    await expect(page.getByTestId('pl-from')).toHaveValue(localDate(new Date(now.getFullYear(), now.getMonth() + 1, 1)));
    await expect(page.getByTestId('pl-until')).toHaveValue(localDate(new Date(now.getFullYear(), now.getMonth() + 2, 0)));
    await expect(page.getByTestId('pl-timezone')).toContainText('Zeitzone Ihres Browsers');

    // Client-side check: a file is required.
    await page.getByTestId('pl-label').fill(listLabel);
    await page.getByTestId('pl-upload').click();
    await expect(page.getByText('Bitte wählen Sie eine .xlsx-Datei aus.')).toBeVisible();

    const xlsx = await ankaufPriceListXlsx([
      { brand, model: 'Testphone 13', storage: '128 GB', price: 300 },
      { brand, model: 'Testphone 14', storage: '256 GB', price: 450.5 },
      { brand, model: 'Testphone 15', storage: '256 GB', price: 'auf Anfrage' },
    ]);
    await page.getByTestId('pl-file').setInputFiles({
      name: `ankauf-${RUN}.xlsx`,
      mimeType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
      buffer: xlsx,
    });
    // Valid from (about) now so the list becomes the current one; until the end of this month.
    const from = new Date(Date.now() - 60_000);
    await page.getByTestId('pl-from').fill(localDate(from));
    await page.getByTestId('pl-from-time').fill(localTime(from));
    await page.getByTestId('pl-until').fill(localDate(new Date(now.getFullYear(), now.getMonth() + 1, 0)));
    await page.getByTestId('pl-upload').click();

    // Review page: both models are new → pending; the text price is skipped.
    // The first XLSX upload after an API start can take ~10 s (workbook reader warm-up).
    await expect(page).toHaveURL(/\/app\/administration\/ankauf\/preislisten\//, { timeout: 30_000 });
    await expect(page.getByTestId('pl-status')).toHaveText('In Prüfung');
    const summary = page.getByTestId('pl-summary');
    await expect(summary.locator('[data-tile="total"] dd')).toHaveText('2');
    await expect(summary.locator('[data-tile="new"] dd')).toHaveText('2');
    await expect(summary.locator('[data-tile="pending"] dd')).toHaveText('2');
    await expect(summary.locator('[data-tile="skipped"] dd')).toHaveText('1');

    const entries = page.getByTestId('pl-entries');
    const row13 = entries.locator('tr[data-entry="Testphone 13"]');
    await expect(row13).toContainText('300,00');
    await expect(row13).toContainText('Neues Modell');
    await expect(row13).toContainText('Offen');

    await page.getByTestId('pl-skipped').getByRole('button').click();
    await expect(page.getByTestId('pl-skipped')).toContainText('Testphone 15');

    // Publishing is blocked while prices are pending.
    await expect(page.getByTestId('pl-publish')).toBeDisabled();
    await expect(page.getByTestId('pl-publish-blocked')).toContainText('2 offenen Preise');

    // Filter + bulk approve.
    await page.getByTestId('pl-filter-decision').selectOption('Pending');
    await expect(entries.locator('tbody tr')).toHaveCount(2);
    await page.getByTestId('pl-select-page').check();
    await expect(page.getByTestId('pl-selected-count')).toHaveText('2 ausgewählt');
    await page.getByTestId('pl-approve').click();
    await expect(summary.locator('[data-tile="pending"] dd')).toHaveText('0');
    await expect(summary.locator('[data-tile="approved"] dd')).toHaveText('2');
    await page.getByTestId('pl-filter-decision').selectOption('');
    await expect(row13).toContainText('Freigegeben');

    // Publish with confirmation (explains that missing/rejected models have no price).
    await expect(page.getByTestId('pl-publish')).toBeEnabled();
    await page.getByTestId('pl-publish').click();
    const dialog = page.getByRole('dialog');
    await expect(dialog).toContainText('keinen Ankaufspreis');
    await dialog.getByTestId('pl-publish-confirm').click();
    await expect(dialog).toBeHidden();
    await expect(page.getByTestId('pl-status')).toHaveText('Veröffentlicht');
    await expect(page.getByTestId('pl-approve')).toHaveCount(0);

    // The list appears in the overview as published.
    await page.getByRole('link', { name: 'Alle Preislisten' }).click();
    await expect(page.locator(`tr[data-list="${listLabel}"]`)).toContainText('Veröffentlicht');

    // Pricing policy page shows the active version (read only, not changed by this test).
    await page.getByRole('link', { name: 'Preisregeln' }).click();
    await expect(page.getByTestId('policy-values')).toBeVisible();
    await expect(page.getByTestId('policy-history')).toContainText('Aktiv');
  });

  test('admin enables the Ankauf module for the shop', async ({ page }) => {
    await login(page, ADMIN_EMAIL, ADMIN_PASSWORD);
    await page.goto('/app/administration');
    await page.getByTestId('admin-status').selectOption('');
    await page.getByTestId('admin-search').fill(shop.name);
    await page.getByTestId('admin-search').press('Enter');
    const row = page.locator(`tr[data-org="${shop.name}"]`);
    await expect(row).toBeVisible();
    await row.getByTestId('admin-modules').click();
    const dialog = page.getByRole('dialog');
    await expect(dialog.locator('input[data-module="B2BPurchasing"]')).toBeChecked();
    await dialog.locator('input[data-module="Ankauf"]').check();
    await dialog.getByTestId('admin-modules-save').click();
    await expect(dialog).toBeHidden();
    await expect(row.getByText('Ankauf', { exact: true })).toBeVisible();
  });

  test('shop creates a preliminary buyback quote', async ({ page }) => {
    await login(page, shop.email, PASSWORD);
    const nav = page.getByRole('navigation', { name: 'Hauptnavigation' });
    await nav.getByRole('link', { name: 'Ankauf' }).click();
    await expect(page).toHaveURL(/\/app\/ankauf$/);

    // Client-side validation before a model is chosen.
    await page.getByTestId('ak-submit').click();
    await expect(page.getByText('Bitte ein Modell auswählen.')).toBeVisible();
    await expect(page.getByText('Bitte den Zustand auswählen.')).toBeVisible();

    await page.getByTestId('ak-search').fill(`${brand} Testphone 13`);
    const models = page.getByTestId('ak-models');
    await expect(models.getByRole('button')).toHaveCount(1);
    await expect(models).not.toContainText('Testphone 15'); // skipped row → no price
    await models.getByRole('button', { name: /Testphone 13/ }).click();
    await expect(page.getByTestId('ak-selected-model')).toContainText(`${brand} Testphone 13 128 GB`);

    await page.locator('input[data-condition="Neu"]').check();
    await page.locator('input[data-defect="HousingDamage"]').check();
    await page.getByTestId('ak-consumer').fill('330');
    // Luhn check on the client.
    await page.getByTestId('ak-imei').fill('490154203237519');
    await page.getByTestId('ak-submit').click();
    await expect(page.getByText('Die IMEI muss aus 15 Ziffern mit gültiger Prüfziffer bestehen.')).toBeVisible();
    await page.getByTestId('ak-imei').fill('49-015420-323751-8');
    await page.getByTestId('ak-submit').click();

    const result = page.getByTestId('ak-result');
    await expect(result).toBeVisible();
    await expect(page.getByTestId('ak-notice')).toContainText('Vorläufiges Angebot');
    await expect(page.getByTestId('ak-reference-gut')).toHaveText(/^300,00\s€$/);
    await expect(page.getByTestId('ak-condition-adjustment')).toHaveText(/^\+20\s%$/);
    await expect(page.getByTestId('ak-deductions')).toHaveText(/^-10\s%$/);
    await expect(page.getByTestId('ak-reference-purchase')).toHaveText(/^324,00\s€$/);
    await expect(page.getByTestId('ak-platform-offer')).toHaveText(/^324,00\s€$/);
    await expect(page.getByTestId('ak-max-consumer')).toHaveText(/^275,40\s€$/);
    await expect(page.getByTestId('ak-consumer-price')).toHaveText(/^330,00\s€$/);
    await expect(page.getByTestId('ak-dealer-margin')).toHaveText(/^-6,00\s€$/);
    await expect(page.getByTestId('ak-negative-margin')).toBeVisible();
    await expect(result).toContainText('Preisregel #');
    await expect(result).toContainText('490154203237518');

    // Quote history.
    const history = page.getByTestId('ak-history');
    await expect(history).toContainText(`${brand} Testphone 13 128 GB`);
    await expect(history).toContainText('324,00');
    await logout(page);
  });
});
