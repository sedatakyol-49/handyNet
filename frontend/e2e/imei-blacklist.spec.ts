import { expect, test } from '@playwright/test';
import { ADMIN_EMAIL, ADMIN_PASSWORD, Api, PASSWORD, RUN, company, hasAdmin, login, logout, randomEan, randomImei } from './support/handynet';

/**
 * Platform IMEI blacklist and the device list through the UI against the real API (ADR-0019):
 * (a) the platform admin blocks two IMEIs on "IMEI-Sperrliste" (one of them is already in a shop's stock);
 * (b) the shop sees its devices on "Geräte (IMEI)" with the history of one device;
 * (c) scanning the blocked, stocked device at the till is refused;
 * (d) a Lager entry with a blocked and an already stocked IMEI lists both and books nothing.
 */
test.describe.serial('IMEI blacklist and devices', () => {
  test.skip(!hasAdmin, 'Set HN_ADMIN_EMAIL and HN_ADMIN_PASSWORD to run e2e tests against the API.');

  const shop = { ...company('shop'), name: `E2E Gerätehandel ${RUN}`, email: `bl-shop-${RUN}@e2e.handynet.test` };
  const ean = randomEan();
  const product = `Neo 12 128 GB ${RUN}`;
  const [kept, reported] = [randomImei(), randomImei()];
  const stolen = randomImei();
  const fresh = randomImei();
  let orgId = '';
  let registerId = '';

  test.beforeAll(async () => {
    orgId = await Api.register(shop);
    const admin = await Api.login(ADMIN_EMAIL, ADMIN_PASSWORD);
    await admin.verifyWithModules(orgId, ['Inventory', 'ProductImport', 'Pos']);
    await admin.dispose();

    const api = await Api.login(shop.email, PASSWORD);
    await api.createItem(orgId, {
      newProduct: { brand: 'E2E', name: product, category: 'Smartphone', quality: 'NotApplicable', ean },
      imeis: [kept, reported],
      purchasePrice: 449.9,
    });
    registerId = (await api.call<{ id: string }>('POST', '/api/pos/registers', orgId, { name: `Theke ${RUN}` })).id;
    await api.call('POST', `/api/pos/registers/${registerId}/sessions`, orgId, { openingCashMinor: 0 });
    await api.dispose();
  });

  test('(a) the platform admin blocks IMEIs', async ({ page }) => {
    await login(page, ADMIN_EMAIL, ADMIN_PASSWORD);
    await page.goto('/app/administration');
    await page.getByTestId('admin-tabs').getByRole('link', { name: 'IMEI-Sperrliste' }).click();
    await expect(page).toHaveURL(/\/app\/administration\/imei-sperrliste$/);

    for (const [imei, reason] of [[stolen, 'Stolen'], [reported, 'Lost']] as const) {
      await page.getByTestId('bl-new').click();
      // client checks first: the source is required
      await page.getByTestId('bl-imei').fill(imei);
      await page.getByTestId('bl-reason').selectOption(reason);
      await page.getByTestId('bl-submit').click();
      await expect(page.getByText('Bitte die Quelle angeben.')).toBeVisible();
      await page.getByTestId('bl-source').fill(`Polizei Berlin, Az. ${RUN}`);
      await page.getByTestId('bl-submit').click();
      await expect(page.getByTestId('bl-imei')).toBeHidden();
    }

    await page.getByTestId('bl-search').fill(stolen);
    const entry = page.getByTestId('bl-table').locator(`tr[data-imei="${stolen}"]`);
    await expect(entry).toHaveCount(1);
    await expect(entry).toContainText('Gestohlen');
    await expect(entry).toContainText('Gesperrt');

    // The same IMEI cannot be blocked twice while active.
    await page.getByTestId('bl-new').click();
    await page.getByTestId('bl-imei').fill(stolen);
    await page.getByTestId('bl-source').fill('Händlermeldung');
    await page.getByTestId('bl-submit').click();
    await expect(page.getByText('Diese IMEI ist bereits gesperrt.')).toBeVisible();
    await page.getByRole('button', { name: 'Abbrechen' }).click();
    await logout(page);
  });

  test('(b) the shop sees its devices and the history of one', async ({ page }) => {
    await login(page, shop.email, PASSWORD);
    await page.getByRole('navigation', { name: 'Hauptnavigation' }).getByRole('link', { name: 'Geräte (IMEI)' }).click();
    await expect(page).toHaveURL(/\/app\/lager\/geraete$/);
    const rows = page.getByTestId('units-table').locator('tbody tr[data-imei]');
    await expect(rows).toHaveCount(2);
    const row = page.getByTestId('units-table').locator(`tr[data-imei="${kept}"]`);
    await expect(row).toContainText('Auf Lager');
    await expect(row).toContainText('449,90 €');
    await expect(row).toContainText('Regelbesteuerung');

    await row.getByTestId('units-open').click();
    await expect(page.getByTestId('unit-detail')).toContainText(kept);
    await expect(page.getByTestId('unit-history').locator('li')).toHaveCount(1);
    await expect(page.getByTestId('unit-history')).toContainText('Gerätezugang (IMEI)');
    await page.keyboard.press('Escape');

    // IMEI prefix search
    await page.getByTestId('units-imei').fill(kept.slice(0, 13));
    await expect(rows).toHaveCount(1);
  });

  test('(c) the till refuses the blocked device', async ({ page }) => {
    await login(page, shop.email, PASSWORD);
    await page.goto(`/kasse/${registerId}`);
    await expect(page.getByTestId('pos-code')).toBeFocused();
    await page.keyboard.type(reported);
    await page.keyboard.press('Enter');
    await expect(page.getByTestId('pos-scan-error')).toContainText('Gerät gesperrt');
    await expect(page.getByTestId('pos-line')).toHaveCount(0);

    // The device that is not reported is sold normally.
    await page.getByTestId('pos-code').fill(kept);
    await page.getByTestId('pos-code').press('Enter');
    await expect(page.getByTestId('pos-line')).toHaveCount(1);
  });

  test('(d) a Lager entry lists every blocked or stocked IMEI and books nothing', async ({ page }) => {
    await login(page, shop.email, PASSWORD);
    await page.goto('/app/lager');
    await page.getByTestId('entry-open').click();
    const dialog = page.getByTestId('entry-dialog');
    await page.getByTestId('entry-scan').fill(ean);
    await page.getByTestId('entry-scan').press('Enter');
    await expect(page.getByTestId('entry-match')).toHaveCount(1);
    await page.getByTestId('entry-next').click();

    await page.getByTestId('entry-imeis').fill(`${fresh}\n${stolen}\n${kept}`);
    await expect(page.getByTestId('entry-imei-summary')).toHaveText('3 gültig, 0 fehlerhaft, 0 doppelt');
    await page.getByTestId('entry-next').click();
    await page.getByTestId('entry-save').click();

    const error = page.getByTestId('entry-error');
    await expect(error).toContainText('Diese Geräte können nicht eingebucht werden');
    await expect(error).toContainText(`${stolen} (gesperrt)`);
    await expect(error).toContainText(`${kept} (bereits im Bestand)`);
    await expect(error).not.toContainText(fresh);
    await expect(page.getByTestId('entry-imeis')).toBeVisible(); // back on the stock step to fix the list
    await dialog.getByRole('button', { name: 'Abbrechen' }).click();

    // All or nothing: the clean device was not booked either.
    await page.goto('/app/lager/geraete');
    await page.getByTestId('units-imei').fill(fresh);
    await expect(page.getByTestId('units-table').getByText('Keine Geräte gefunden')).toBeVisible();
  });
});
