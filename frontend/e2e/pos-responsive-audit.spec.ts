import { Page, expect, test } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { ADMIN_EMAIL, ADMIN_PASSWORD, Api, PASSWORD, RUN, company, hasAdmin, login, randomEan } from './support/handynet';

/**
 * Visual audit of the POS app (not an assertion test; runs only with HN_POS_AUDIT=1): opens every POS route and the
 * main dialogs at phone, tablet portrait, tablet landscape and desktop size and writes full-viewport screenshots plus a
 * horizontal-overflow report to `test-results/pos-audit/`. Used to review the responsive layout by eye.
 */
const VIEWPORTS = [
  { name: 'phone', width: 375, height: 812 },
  { name: 'tablet-portrait', width: 820, height: 1180 },
  { name: 'tablet-landscape', width: 1180, height: 820 },
  { name: 'desktop', width: 1440, height: 900 },
];
const OUT = resolve(__dirname, '../test-results/pos-audit');

test.describe.serial('POS responsive audit', () => {
  test.skip(!hasAdmin || process.env['HN_POS_AUDIT'] !== '1', 'Visual audit only (HN_POS_AUDIT=1).');

  const shop = { ...company('shop'), name: `E2E Kasse Audit ${RUN}`, email: `pos-audit-${RUN}@e2e.handynet.test` };
  let orgId = '';
  let registerId = '';
  const eans: string[] = [];

  test.beforeAll(async () => {
    orgId = await Api.register(shop);
    const admin = await Api.login(ADMIN_EMAIL, ADMIN_PASSWORD);
    await admin.verifyWithModules(orgId, ['Inventory', 'ProductImport', 'Pos', 'Ankauf']);
    await admin.dispose();
    const api = await Api.login(shop.email, PASSWORD);
    const warehouse = await api.call<{ id: string }>('POST', '/api/inventory/warehouses', orgId, { code: 'LADEN', name: 'Laden' });
    const names = ['Ladekabel USB-C 2 m geflochten', 'Panzerglas iPhone 15 Pro Max', 'Silikonhülle Galaxy S24', 'Netzteil 20 W', 'Powerbank 10000 mAh', 'Kopfhörer In-Ear Bluetooth'];
    for (const [i, name] of names.entries()) {
      const ean = randomEan();
      eans.push(ean);
      const p = await api.call<{ id: string }>('POST', '/api/catalog/products', orgId, { brand: 'E2E', name: `${name} ${RUN}`, category: 'Accessory', ean });
      await api.call('POST', '/api/inventory/adjustments', orgId, { warehouseId: warehouse.id, productId: p.id, delta: 5, reason: 'E2E', type: 'Receipt' }, 200);
      await api.call('PUT', `/api/pos/prices/${p.id}`, orgId, { priceMinor: 990 + i * 1000 });
    }
    registerId = (await api.call<{ id: string }>('POST', '/api/pos/registers', orgId, { name: `Theke Audit ${RUN}`, warehouseId: warehouse.id })).id;
    await api.call('POST', `/api/pos/registers/${registerId}/sessions`, orgId, { openingCashMinor: 10000 });
    await api.dispose();
  });

  test('screenshots of every POS route at four sizes', async ({ page }) => {
    test.setTimeout(900_000);
    mkdirSync(OUT, { recursive: true });
    await page.addInitScript(() => {
      window.print = () => undefined;
    });
    const report: string[] = [];
    await login(page, shop.email, PASSWORD);

    // One sale so receipts and sessions have content.
    await page.goto(`/kasse/${registerId}`);
    await page.getByTestId('pos-code').fill(eans[0]);
    await page.keyboard.press('Enter');
    await expect(page.getByTestId('pos-total')).toHaveText('9,90 €');
    await page.getByTestId('pos-pay').click();
    await page.getByTestId('pos-method-Card').click();
    await page.getByTestId('pos-validate').click();
    await expect(page.getByTestId('pos-sale-done')).toBeVisible();
    await page.getByTestId('pos-new-sale').click();

    const api = await Api.login(shop.email, PASSWORD);
    const sales = await api.call<{ items: { id: string }[] }>('GET', `/api/pos/sales?registerId=${registerId}`, orgId);
    const session = await api.call<{ id: string }>('GET', `/api/pos/registers/${registerId}/sessions/current`, orgId);
    await api.dispose();
    const receiptId = sales.items[0].id;

    const routes: [string, string][] = [
      ['registers', '/kasse'],
      ['till', `/kasse/${registerId}`],
      ['receipts', `/kasse/${registerId}/belege`],
      ['receipt-detail', `/kasse/belege/${receiptId}`],
      ['sessions', `/kasse/${registerId}/sitzungen`],
      ['session-detail', `/kasse/sitzungen/${session.id}`],
      ['reports', `/kasse/berichte`],
      ['daily-report', `/kasse/berichte/tag`],
      ['quotations', `/kasse/${registerId}/angebote`],
      ['quotation-new', `/kasse/${registerId}/angebote/neu`],
      ['invoices', `/kasse/${registerId}/rechnungen`],
      ['ankauf', `/kasse/${registerId}/ankauf`],
      ['prices', `/kasse/${registerId}/preise`],
      ['printers', `/kasse/${registerId}/drucker`],
    ];

    for (const vp of VIEWPORTS) {
      await page.setViewportSize({ width: vp.width, height: vp.height });
      for (const [name, url] of routes) {
        await page.goto(url);
        await page.waitForLoadState('networkidle');
        await shot(page, `${vp.name}-${name}`, report);
      }
      // Till states: cart with lines, payment, menu and dialogs.
      await page.goto(`/kasse/${registerId}`);
      await page.waitForLoadState('networkidle');
      for (const ean of eans.slice(0, 3)) {
        await page.getByTestId('pos-code').fill(ean);
        await page.keyboard.press('Enter');
      }
      await page.waitForTimeout(500);
      await shot(page, `${vp.name}-till-lines`, report);
      const cartButton = page.getByTestId('pos-pane-cart');
      const phone = await cartButton.isVisible();
      if (phone) {
        await cartButton.click();
        await shot(page, `${vp.name}-till-cart`, report);
      }
      await page.getByTestId('pos-customer').click();
      await page.waitForTimeout(300);
      await shot(page, `${vp.name}-dialog-customer`, report);
      await page.keyboard.press('Escape');
      if (phone) await page.getByTestId('pos-pane-products').click();
      await page.getByTestId('pos-switch-cashier').click();
      await page.waitForTimeout(300);
      await shot(page, `${vp.name}-dialog-pin`, report);
      await page.keyboard.press('Escape');
      await page.getByTestId('pos-menu').click();
      await shot(page, `${vp.name}-menu`, report);
      await page.keyboard.press('Escape');
      await page.getByTestId(phone ? 'pos-bar-pay' : 'pos-pay').click();
      await expect(page.getByTestId('pos-payment')).toBeVisible();
      await shot(page, `${vp.name}-payment`, report);
      await page.getByTestId('pos-pay-back').click();
      if (phone) await page.getByTestId('pos-pane-cart').click();
      await page.getByTestId('pos-discard').click();
      await page.getByTestId('pos-discard-confirm').click();
    }
    writeFileSync(resolve(OUT, 'overflow.txt'), report.join('\n'));
  });
});

async function shot(page: Page, name: string, report: string[]): Promise<void> {
  const o = await page.evaluate(() => {
    const d = document.documentElement;
    const wide = [...document.querySelectorAll<HTMLElement>('body *')]
      .filter((e) => e.getBoundingClientRect().right > window.innerWidth + 1 && e.offsetParent !== null)
      .slice(0, 5)
      .map((e) => `${e.tagName.toLowerCase()}.${String(e.className).slice(0, 60)}[${e.getAttribute('data-testid') ?? ''}]`);
    const small = [...document.querySelectorAll<HTMLElement>('.hn-pos button, .hn-pos a, .hn-pos input, .hn-pos select')]
      .filter((e) => {
        const r = e.getBoundingClientRect();
        return r.width > 0 && r.height > 0 && (r.height < 43.5 || r.width < 43.5) && r.width > 2 && getComputedStyle(e).visibility !== 'hidden';
      })
      .slice(0, 8)
      .map((e) => `${e.tagName.toLowerCase()}[${e.getAttribute('data-testid') ?? (e.textContent ?? '').trim().slice(0, 20)}] ${Math.round(e.getBoundingClientRect().width)}x${Math.round(e.getBoundingClientRect().height)}`);
    return { sw: d.scrollWidth, cw: d.clientWidth, wide, small };
  });
  report.push(`${name}: scroll ${o.sw}/${o.cw}${o.sw > o.cw ? ' OVERFLOW' : ''}\n  wide: ${o.wide.join(' | ')}\n  small: ${o.small.join(' | ')}`);
  await page.screenshot({ path: resolve(OUT, `${name}.png`) });
}
