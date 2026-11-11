import { Page, expect, test } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { ADMIN_EMAIL, ADMIN_PASSWORD, Api, PASSWORD, RUN, company, hasAdmin, login, randomEan } from './support/handynet';

/**
 * Offline mode of the till against the real API. The API is cut by aborting every /api and /health request of the
 * page (the web server stays reachable, so a reload while "offline" can be tested without a service worker):
 * - a manager switches the register's offline mode on (with the legal caveat);
 * - the till notices the outage (failed request → readiness probe), shows the banner and sells from the cached grid:
 *   a cash and a card sale are queued on the device with provisional receipts (no receipt number, no TSE);
 * - the queue survives a reload while the API is still unreachable; closing the session is blocked;
 * - after reconnecting the queue syncs by itself in order: real receipts 1 and 2 carry the offline record;
 * - a sync conflict (stock sold elsewhere meanwhile) stays in the queue as "needs attention" with the API's reason,
 *   is retried by the manager after restocking, and another entry is discarded with a reason (saved as a file first).
 */
test.describe.serial('POS offline mode', () => {
  test.skip(!hasAdmin, 'Set HN_ADMIN_EMAIL and HN_ADMIN_PASSWORD to run e2e tests against the API.');

  const shop = { ...company('shop'), name: `E2E Offlineshop ${RUN}`, email: `offline-${RUN}@e2e.handynet.test` };
  const product = `Powerbank Offline ${RUN}`;
  let orgId = '';
  let registerId = '';
  let warehouseId = '';
  let productId = '';

  async function withApi<T>(fn: (api: Api) => Promise<T>): Promise<T> {
    const api = await Api.login(shop.email, PASSWORD);
    try {
      return await fn(api);
    } finally {
      await api.dispose();
    }
  }

  const adjustStock = (delta: number) =>
    withApi((api) => api.call('POST', '/api/inventory/adjustments', orgId, { warehouseId, productId, delta, reason: 'E2E', type: delta > 0 ? 'Receipt' : 'Adjustment' }, 200));

  interface Sale {
    number: number;
    totalGrossMinor: number;
    offline?: boolean;
  }
  const sales = () => withApi(async (api) => (await api.call<{ items: Sale[] }>('GET', `/api/pos/sales?registerId=${registerId}&pageSize=50`, orgId)).items);

  /** Cuts the API for this page: every /api and /health request fails like a dead network; the app shell still loads. */
  async function cutApi(page: Page): Promise<void> {
    await page.route(/\/(api|health)\//, (route) => route.abort('internetdisconnected'));
  }

  async function restoreApi(page: Page): Promise<void> {
    await page.unroute(/\/(api|health)\//);
  }

  /** The till only learns about the outage from a failed request (navigator.onLine stays true here): make one. */
  async function noticeOutage(page: Page): Promise<void> {
    await page.getByTestId('pos-code').fill('x');
    await expect(page.getByTestId('pos-offline-banner')).toBeVisible({ timeout: 15_000 });
    await page.getByTestId('pos-code').fill('');
  }

  /** Has the device a cached copy of the register's products (IndexedDB)? */
  function cachedProductCount(page: Page): Promise<number> {
    return page.evaluate(
      () =>
        new Promise<number>((resolve) => {
          const open = indexedDB.open('hn-pos-offline');
          open.onerror = () => resolve(-1);
          open.onsuccess = () => {
            const db = open.result;
            if (!db.objectStoreNames.contains('cache')) return resolve(-1);
            const all = db.transaction('cache', 'readonly').objectStore('cache').getAll();
            all.onsuccess = () => {
              const row = (all.result as { key: string; data: unknown[] }[]).find((r) => r.key.includes(':products:'));
              resolve(row ? row.data.length : -1);
            };
            all.onerror = () => resolve(-1);
          };
        }),
    );
  }

  async function sellOffline(page: Page, quantity: number, method: 'Cash' | 'Card', cash?: string): Promise<string> {
    const tile = page.getByTestId('pos-product').filter({ hasText: product });
    await expect(tile).toBeVisible();
    for (let i = 0; i < quantity; i++) await tile.click();
    await page.getByTestId('pos-pay').click();
    await expect(page.getByTestId('pos-offline-pay-note')).toContainText('Offline');
    await expect(page.getByTestId('pos-method-GiftCard')).toBeDisabled();
    await page.getByTestId(`pos-method-${method}`).click();
    if (cash) await page.getByTestId('pos-pay-amount').fill(cash);
    await page.getByTestId('pos-validate').click();
    await expect(page.getByTestId('pos-offline-sale-done')).toContainText('Offline gespeichert – noch nicht gebucht');
    const reference = (await page.getByTestId('pos-offline-reference').innerText()).trim();
    expect(reference).toMatch(/^OFF-[A-Z0-9]{4}-\d+$/);
    return reference;
  }

  test.beforeAll(async () => {
    orgId = await Api.register(shop);
    const admin = await Api.login(ADMIN_EMAIL, ADMIN_PASSWORD);
    await admin.verifyWithModules(orgId, ['Inventory', 'ProductImport', 'Pos']);
    await admin.dispose();
    await withApi(async (api) => {
      warehouseId = (await api.call<{ id: string }>('POST', '/api/inventory/warehouses', orgId, { code: 'LADEN', name: 'Laden' })).id;
      productId = (await api.call<{ id: string }>('POST', '/api/catalog/products', orgId, { brand: 'E2E', name: product, category: 'Accessory', ean: randomEan() })).id;
      await api.call('POST', '/api/inventory/adjustments', orgId, { warehouseId, productId, delta: 10, reason: 'E2E', type: 'Receipt' }, 200);
      await api.call('PUT', `/api/pos/prices/${productId}`, orgId, { priceMinor: 1990 });
      registerId = (await api.call<{ id: string }>('POST', '/api/pos/registers', orgId, { name: `Theke ${RUN}`, warehouseId })).id;
      await api.call('POST', `/api/pos/registers/${registerId}/sessions`, orgId, { openingCashMinor: 5000 });
    });
  });

  test('offline sales are queued on the device, survive a reload and sync after reconnecting', async ({ page }) => {
    await login(page, shop.email, PASSWORD);

    // ---- a manager switches the offline mode on (the legal caveat is part of the setting)
    await page.goto('/kasse');
    await page.getByTestId('pos-rename').click();
    await expect(page.locator('#pos-offline-mode-hint')).toContainText('rechtliche Prüfung steht aus');
    await page.getByTestId('pos-offline-mode').check();
    await page.getByTestId('pos-rename-save').click();
    await expect(page.getByTestId('pos-register-offline-mode')).toBeVisible();

    // ---- online: the till caches the register's products for offline use
    await page.goto(`/kasse/${registerId}`);
    await expect(page.getByTestId('pos-product').filter({ hasText: product })).toBeVisible();
    await expect(page.getByTestId('pos-offline-banner')).toHaveCount(0);
    await expect.poll(() => cachedProductCount(page), { timeout: 20_000 }).toBeGreaterThan(0);

    // ---- the API becomes unreachable
    await cutApi(page);
    await noticeOutage(page);
    await expect(page.getByTestId('pos-offline-banner')).toContainText('Keine Verbindung zum Server.');
    await expect(page.getByTestId('pos-online')).toContainText('Offline');
    await expect(page.getByTestId('pos-offline-grid-note')).toContainText('Offline: Artikel, Preise und Bestände vom');
    await expect(page.getByTestId('pos-sell-gift-card')).toBeDisabled(); // gift card codes come from the API

    // ---- cash sale: provisional receipt without receipt number and TSE
    const first = await sellOffline(page, 1, 'Cash', '50');
    await expect(page.getByTestId('pos-change-due')).toHaveText('30,10 €');
    const provisional = page.getByTestId('provisional-receipt');
    await expect(provisional.getByTestId('provisional-notice')).toContainText('Vorläufiger Offline-Beleg');
    await expect(provisional.getByTestId('provisional-notice')).toContainText('Kein Kassenbeleg – keine Beleg-Nr., TSE nicht erreichbar');
    await expect(provisional.getByTestId('provisional-reference')).toHaveText(first);
    await expect(provisional.getByTestId('provisional-total')).toHaveText('19,90 €');
    await expect(page.getByTestId('receipt')).toHaveCount(0); // no till receipt
    await expect(page.getByTestId('pos-offline-pill')).toHaveText('1 offline');
    await page.getByTestId('pos-new-sale').click();

    // ---- card sale (confirmed by hand while offline), two pieces; the cached stock went down by the first sale
    const second = await sellOffline(page, 2, 'Card');
    expect(second).not.toBe(first);
    await expect(page.getByTestId('pos-offline-pill')).toHaveText('2 offline');
    await page.getByTestId('pos-new-sale').click();
    await expect(page.getByTestId('pos-product').filter({ hasText: product })).toContainText('7');

    // ---- reload while the API is still unreachable: signed in, register and session from the cache, queue intact
    await page.reload();
    await expect(page.getByTestId('pos-offline-banner')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByTestId('pos-sell')).toBeVisible();
    await expect(page.getByTestId('pos-offline-pill')).toHaveText('2 offline');
    await page.getByTestId('pos-offline-pill').click();
    await expect(page).toHaveURL(new RegExp(`/kasse/${registerId}/offline$`));
    const entries = page.getByTestId('pos-offline-entry');
    await expect(entries).toHaveCount(2);
    await expect(entries.nth(0)).toHaveAttribute('data-reference', first);
    await expect(entries.nth(1)).toHaveAttribute('data-reference', second);
    await expect(entries.nth(0).getByTestId('pos-offline-status')).toHaveText('Wartet auf Verbindung');
    await expect(entries.nth(0).getByTestId('pos-offline-total')).toHaveText('19,90 €');
    await expect(entries.nth(1).getByTestId('pos-offline-total')).toHaveText('39,80 €');
    expect(await sales()).toHaveLength(0); // nothing reached the API

    // ---- reconnect: the queue syncs by itself, in order
    await restoreApi(page);
    await expect(page.getByTestId('pos-offline-synced-row')).toHaveCount(2, { timeout: 30_000 });
    await expect(page.getByTestId('pos-offline-empty')).toBeVisible();
    await expect(page.getByTestId('pos-offline-banner')).toHaveCount(0);
    await expect(page.getByTestId('pos-offline-pill')).toHaveCount(0);
    const synced = page.getByTestId('pos-offline-synced');
    await expect(synced).toContainText(`${first} → Beleg Nr. 1`);
    await expect(synced).toContainText(`${second} → Beleg Nr. 2`);

    const booked = (await sales()).sort((a, b) => a.number - b.number);
    expect(booked.map((s) => [s.number, s.totalGrossMinor, s.offline])).toEqual([
      [1, 1990, true],
      [2, 3980, true],
    ]);

    // ---- the real receipt carries the offline record; the list marks it
    await page.goto(`/kasse/${registerId}/belege`);
    await expect(page.locator('[data-receipt="1"]').getByTestId('pos-receipt-offline')).toBeVisible();
    await page.locator('[data-receipt="1"]').getByRole('link').first().click();
    await expect(page.getByTestId('receipt')).toContainText('Offline erfasst am');
    await expect(page.getByTestId('receipt')).toContainText(`Offline-Nr. ${first}`);
    await expect(page.getByTestId('receipt')).toContainText('nachträglich gebucht und signiert');
  });

  test('a sync conflict stays in the queue, blocks closing, is retried; another entry is discarded with a reason', async ({ page }) => {
    await login(page, shop.email, PASSWORD);
    await page.goto(`/kasse/${registerId}`);
    await expect(page.getByTestId('pos-product').filter({ hasText: product })).toBeVisible();
    await expect.poll(() => cachedProductCount(page), { timeout: 20_000 }).toBeGreaterThan(0);

    await cutApi(page);
    await noticeOutage(page);
    const conflicting = await sellOffline(page, 1, 'Cash');
    await page.getByTestId('pos-new-sale').click();

    // Meanwhile the remaining stock is sold elsewhere: the API will refuse the queued sale.
    await adjustStock(-7);

    // The session cannot be closed while this device holds unsynced sales (checked while still offline, from the queue page).
    await page.getByTestId('pos-offline-pill').click();
    await expect(page.getByTestId('pos-offline-entry')).toHaveCount(1);

    await restoreApi(page);
    const entry = page.getByTestId('pos-offline-entry');
    await expect(entry).toHaveAttribute('data-status', 'attention', { timeout: 30_000 });
    await expect(entry).toHaveAttribute('data-reference', conflicting);
    await expect(entry.getByTestId('pos-offline-status')).toHaveText('Abgelehnt – bitte prüfen');
    await expect(entry.getByTestId('pos-offline-error')).toContainText('Vom Server abgelehnt:');
    await expect(entry.getByTestId('pos-offline-error')).toContainText('stock.insufficient');
    await expect(page.getByTestId('pos-offline-pill')).toHaveText('1 offline');
    expect(await sales()).toHaveLength(2); // still only the two receipts of the first test

    // closing is blocked while the entry is unresolved
    const sessionId = await withApi(async (api) => (await api.call<{ id: string }>('GET', `/api/pos/registers/${registerId}/sessions/current`, orgId)).id);
    await page.goto(`/kasse/sitzungen/${sessionId}`);
    await expect(page.getByTestId('pos-close-blocked')).toContainText('nicht gebucht (Anzahl: 1)');
    await expect(page.getByTestId('pos-close-submit')).toHaveCount(0);
    await page.getByTestId('pos-close-blocked-link').click();

    // retry without a change: refused again, still in the queue
    await entry.getByTestId('pos-offline-retry').click();
    await expect(entry).toHaveAttribute('data-status', 'attention');
    await expect(entry.getByTestId('pos-offline-error')).toContainText('stock.insufficient');

    // goods arrive, the manager retries: booked as receipt 3 with the offline record
    await adjustStock(3);
    await entry.getByTestId('pos-offline-retry').click();
    await expect(page.getByTestId('pos-offline-empty')).toBeVisible({ timeout: 20_000 });
    await expect(page.getByTestId('pos-offline-synced')).toContainText(`${conflicting} → Beleg Nr. 3`);
    await expect(page.getByTestId('pos-offline-pill')).toHaveCount(0);
    const afterRetry = await sales();
    expect(afterRetry.find((s) => s.number === 3)).toMatchObject({ totalGrossMinor: 1990, offline: true });

    // ---- another offline sale is discarded: reason required, the entry is saved as a file first
    await page.goto(`/kasse/${registerId}`);
    await expect(page.getByTestId('pos-product').filter({ hasText: product })).toBeVisible();
    await cutApi(page);
    await noticeOutage(page);
    const discarded = await sellOffline(page, 1, 'Cash');
    await page.getByTestId('pos-offline-pill').click();
    const waiting = page.getByTestId('pos-offline-entry');
    await expect(waiting).toHaveAttribute('data-reference', discarded);
    await waiting.getByTestId('pos-offline-print').click();
    await expect(page.getByTestId('pos-offline-preview').getByTestId('provisional-reference')).toHaveText(discarded);
    await waiting.getByTestId('pos-offline-discard').click();
    await page.getByTestId('pos-offline-discard-confirm').click();
    await expect(page.getByText('Bitte einen Grund angeben (mindestens 3 Zeichen).')).toBeVisible();
    await page.getByTestId('pos-offline-reason').fill('Kunde hat den Kauf abgebrochen');
    const [download] = await Promise.all([page.waitForEvent('download'), page.getByTestId('pos-offline-discard-confirm').click()]);
    expect(download.suggestedFilename()).toBe(`offline-${discarded}-verworfen.json`);
    const record = JSON.parse(readFileSync(await download.path(), 'utf8'));
    expect(record).toMatchObject({ kind: 'handynet-offline-sale-discarded', reason: 'Kunde hat den Kauf abgebrochen' });
    expect(record.entry.reference).toBe(discarded);
    expect(record.entry.totalMinor).toBe(1990);
    await expect(page.getByTestId('pos-offline-empty')).toBeVisible();

    await restoreApi(page);
    await expect(page.getByTestId('pos-offline-banner')).toHaveCount(0, { timeout: 20_000 });
    expect(await sales()).toHaveLength(3); // the discarded sale never reached the API
  });
});
