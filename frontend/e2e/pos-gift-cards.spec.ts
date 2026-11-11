import { expect, test } from '@playwright/test';
import { ADMIN_EMAIL, ADMIN_PASSWORD, Api, PASSWORD, RUN, company, hasAdmin, login, randomEan } from './support/handynet';

/**
 * Gift cards against the real API: sold at the till (amount pad, limits checked), the code is shown once on the voucher
 * slip and never again (the receipt read has no code); redeemed partially in a second sale; the balance is right in the
 * lookup and on the "Gutscheine" page; a return refunded to a new gift card shows that card's code once; voiding works.
 */
test.describe('POS gift cards', () => {
  test.skip(!hasAdmin, 'Set HN_ADMIN_EMAIL and HN_ADMIN_PASSWORD to run e2e tests against the API.');

  const shop = { ...company('shop'), name: `E2E Gutscheine ${RUN}`, email: `gutschein-${RUN}@e2e.handynet.test` };
  const product = `Hülle Gutschein ${RUN}`;
  let orgId = '';
  let registerId = '';

  test.beforeAll(async () => {
    orgId = await Api.register(shop);
    const admin = await Api.login(ADMIN_EMAIL, ADMIN_PASSWORD);
    await admin.verifyWithModules(orgId, ['Inventory', 'ProductImport', 'Pos']);
    await admin.dispose();
    const api = await Api.login(shop.email, PASSWORD);
    const warehouse = await api.call<{ id: string }>('POST', '/api/inventory/warehouses', orgId, { code: 'LADEN', name: 'Laden' });
    const p = await api.call<{ id: string }>('POST', '/api/catalog/products', orgId, { brand: 'E2E', name: product, category: 'Accessory', ean: randomEan() });
    await api.call('POST', '/api/inventory/adjustments', orgId, { warehouseId: warehouse.id, productId: p.id, delta: 10, reason: 'E2E', type: 'Receipt' }, 200);
    await api.call('PUT', `/api/pos/prices/${p.id}`, orgId, { priceMinor: 1990 });
    registerId = (await api.call<{ id: string }>('POST', '/api/pos/registers', orgId, { name: `Theke ${RUN}`, warehouseId: warehouse.id })).id;
    await api.call('POST', `/api/pos/registers/${registerId}/sessions`, orgId, { openingCashMinor: 0 });
    await api.dispose();
  });

  test('sold → code shown once → redeemed partly → balance right; refund onto a new card; void', async ({ page }) => {
    await page.addInitScript(() => (window.print = () => undefined));
    await login(page, shop.email, PASSWORD);
    await page.goto(`/kasse/${registerId}`);

    // ---- sell a 25,00 € gift card (limits are checked before the API)
    await page.getByTestId('pos-sell-gift-card').click();
    const sale = page.getByTestId('pos-gift-card-sale');
    await sale.getByTestId('pos-gc-amount').fill('2');
    await sale.getByTestId('pos-gc-add').click();
    await expect(sale).toContainText('Mindestens 5,00 €.');
    await sale.getByTestId('pos-gc-quick').filter({ hasText: '25,00 €' }).click();
    await sale.getByTestId('pos-gc-add').click();
    const giftLine = page.getByTestId('pos-line').filter({ hasText: 'Gutschein' });
    await expect(giftLine.getByTestId('pos-line-total')).toHaveText('25,00 €');
    await expect(giftLine.getByTestId('pos-line-gift-card')).toBeVisible();
    await expect(page.getByTestId('pos-total')).toHaveText('25,00 €');

    // a gift card cannot pay for itself: the gift card method offers nothing to pay
    await page.getByTestId('pos-pay').click();
    await page.getByTestId('pos-method-Cash').click();
    await page.getByTestId('pos-validate').click();
    await expect(page.getByTestId('pos-sale-done')).toBeVisible();

    // ---- the slip shows the code once
    const slip = page.getByTestId('pos-gift-card-slip');
    await expect(slip.getByTestId('pos-gift-card-amount')).toHaveText('25,00 €');
    const code = (await slip.getByTestId('pos-gift-card-code').innerText()).trim();
    expect(code).toMatch(/^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$/);
    await expect(slip.getByTestId('pos-gift-card-expires')).toHaveText(/^31\.12\.\d{4}$/);
    await page.getByTestId('pos-gift-card-print').click(); // thermal or print dialog; must not fail
    await expect(page.getByTestId('receipt')).toContainText('••••-' + code.slice(-4));
    await expect(page.getByTestId('receipt')).not.toContainText(code);

    // the receipt read later carries no code
    const api = await Api.login(shop.email, PASSWORD);
    const sales = await api.call<{ items: { id: string; number: number }[] }>('GET', `/api/pos/sales?registerId=${registerId}`, orgId);
    const first = await api.call<{ issuedGiftCards: { code: string | null }[] }>('GET', `/api/pos/sales/${sales.items.find((s) => s.number === 1)!.id}`, orgId);
    expect(first.issuedGiftCards[0].code).toBeNull();

    // ---- second sale: 19,90 € paid with the gift card (code typed in lower case without dashes)
    await page.getByTestId('pos-new-sale').click();
    await page.getByTestId('pos-product').filter({ hasText: product }).click();
    await page.getByTestId('pos-pay').click();
    await page.getByTestId('pos-method-GiftCard').click();
    const pay = page.getByTestId('pos-gift-card-pay');
    await pay.getByTestId('pos-gcp-code').fill(code.replace(/-/g, '').toLowerCase());
    await expect(pay.getByTestId('pos-gcp-code')).toHaveValue(code);
    await pay.getByTestId('pos-gcp-lookup').click();
    await expect(pay.getByTestId('pos-gcp-balance')).toHaveText('25,00 €');
    await expect(pay.getByTestId('pos-gcp-amount')).toHaveValue('19,90');
    await pay.getByTestId('pos-gcp-use').click();
    await expect(page.getByTestId('pos-pay-gift-card')).toHaveText('••••-' + code.slice(-4));
    await expect(page.getByTestId('pos-change')).toHaveText('0,00 €');
    await page.getByTestId('pos-validate').click();
    await expect(page.getByTestId('pos-sale-done')).toBeVisible();
    await expect(page.getByTestId('receipt').getByTestId('receipt-payments')).toContainText('Gutschein');

    const lookup = await api.call<{ balanceMinor: number; usable: boolean }>('GET', `/api/pos/gift-cards/lookup?code=${encodeURIComponent(code)}`, orgId);
    expect(lookup).toMatchObject({ balanceMinor: 510, usable: true });

    // ---- a wrong code is refused with a clear text
    await page.getByTestId('pos-new-sale').click();
    await page.getByTestId('pos-product').filter({ hasText: product }).click();
    await page.getByTestId('pos-pay').click();
    await page.getByTestId('pos-method-GiftCard').click();
    await pay.getByTestId('pos-gcp-code').fill('AAAA-BBBB-CCCC-DDDD');
    await pay.getByTestId('pos-gcp-lookup').click();
    await expect(pay).toContainText('Unbekannter Gutscheincode.');
    await pay.getByTestId('dialog-close').click();
    await page.getByTestId('pos-pay-back').click();
    await page.getByTestId('pos-discard').click();
    await page.getByTestId('pos-discard-confirm').click();

    // ---- return of the second sale refunded onto a new gift card: its code is shown once on the return receipt
    const second = (await api.call<{ items: { id: string; number: number }[] }>('GET', `/api/pos/sales?registerId=${registerId}`, orgId)).items.find((s) => s.number === 2)!;
    await page.goto(`/kasse/belege/${second.id}`);
    await page.getByTestId('pos-start-return').click();
    await page.getByTestId('pos-return-qty').fill('1');
    await page.getByTestId('pos-refund-GiftCard').check();
    await page.getByTestId('pos-return-reason').fill('Falsches Modell');
    await page.getByTestId('pos-return-submit').click();
    await page.getByTestId('pos-return-confirm').click();
    const refundSlip = page.getByTestId('pos-gift-card-slip');
    await expect(refundSlip.getByTestId('pos-gift-card-amount')).toHaveText('19,90 €');
    const refundCode = (await refundSlip.getByTestId('pos-gift-card-code').innerText()).trim();
    expect(refundCode).not.toBe(code);
    await page.getByTestId('pos-gift-card-slips-done').click();
    await expect(page.getByTestId('pos-gift-card-slips')).toHaveCount(0);
    await page.reload();
    await expect(page.getByTestId('pos-gift-card-slips')).toHaveCount(0); // never shown again

    // ---- management page: balances, history, void
    await page.goto(`/kasse/${registerId}/gutscheine`);
    const row = page.locator(`[data-card="${code.slice(-4)}"]`);
    await expect(row.getByTestId('gc-balance')).toHaveText('5,10 €');
    await row.getByTestId('gc-open').click();
    const detail = page.getByTestId('gc-detail');
    await expect(detail.getByTestId('gc-tx')).toHaveCount(2); // sold, redeemed
    await detail.getByTestId('gc-void').click();
    await expect(detail).toContainText('Bitte einen Grund angeben.');
    await detail.getByTestId('gc-reason').fill('Kunde hat Karte verloren');
    await detail.getByTestId('gc-void').click();
    await expect(detail.getByTestId('gc-tx')).toHaveCount(3);
    await expect(row.getByTestId('gc-state')).toHaveText('Gesperrt');
    const voided = await api.call<{ usable: boolean; status: string }>('GET', `/api/pos/gift-cards/lookup?code=${encodeURIComponent(code)}`, orgId);
    expect(voided).toMatchObject({ usable: false, status: 'Voided' });
    await api.dispose();
  });
});
