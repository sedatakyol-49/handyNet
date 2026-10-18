import { APIRequestContext, Page, expect, request, test } from '@playwright/test';
import { ADMIN_EMAIL, ADMIN_PASSWORD, API_URL, Api, PASSWORD, RUN, company, hasAdmin, login, priceListCsv, randomEan } from './support/handynet';

/**
 * Platform moderation: the admin blocks a single offer, a selection and all offers of a seller in the
 * "Marktplatz-Moderation" tab; the shop no longer finds them, unblocking restores them, and the seller sees the block.
 */
test.describe('marketplace moderation', () => {
  test.skip(!hasAdmin, 'Set HN_ADMIN_EMAIL and HN_ADMIN_PASSWORD to run e2e tests against the API.');

  const supplier = { ...company('supplier'), name: `E2E Moderation Lieferant ${RUN}`, email: `mod-supplier-${RUN}@e2e.handynet.test` };
  const shop = { ...company('shop'), name: `E2E Moderation Shop ${RUN}`, email: `mod-shop-${RUN}@e2e.handynet.test` };
  const tag = `M${RUN}`;
  const items = [
    { sku: `MOD-A-${RUN}`, ean: randomEan(), name: `Galaxy S23 Display ${tag}`, price: '120,00', stock: 5 },
    { sku: `MOD-B-${RUN}`, ean: randomEan(), name: `Galaxy S23 Akku ${tag}`, price: '25,00', stock: 5 },
  ];
  let supplierOrg = '';
  let shopOrg = '';

  /** Supplier SKUs the shop currently finds on the marketplace for this run's products. */
  async function visibleSkus(): Promise<string[]> {
    const ctx: APIRequestContext = await request.newContext({ baseURL: API_URL });
    try {
      const auth = await ctx.post('/api/auth/login', { data: { email: shop.email, password: PASSWORD } });
      const token = (await auth.json()).accessToken;
      const res = await ctx.get('/api/marketplace/offers', {
        headers: { Authorization: `Bearer ${token}`, 'X-Organization-Id': shopOrg },
        params: { search: tag, pageSize: 50 },
      });
      expect(res.status(), await res.text()).toBe(200);
      return ((await res.json()).items as { supplierSku: string }[]).map((i) => i.supplierSku).sort();
    } finally {
      await ctx.dispose();
    }
  }

  async function openModeration(page: Page): Promise<void> {
    await login(page, ADMIN_EMAIL, ADMIN_PASSWORD);
    await page.goto('/app/administration/marktplatz');
    await expect(page.getByTestId('admin-tabs').getByRole('link', { name: 'Marktplatz-Moderation' })).toHaveAttribute('aria-current', 'page');
    await page.getByTestId('mod-filter-search').fill(tag);
    await page.getByTestId('mod-filter-apply').click();
    await expect(page.getByTestId('mod-offers-table').locator('tbody tr[data-offer]')).toHaveCount(2);
  }

  async function confirm(page: Page, reason: string): Promise<void> {
    const dialog = page.getByTestId('mod-dialog');
    await expect(dialog).toBeVisible();
    if (reason) await dialog.getByTestId('mod-reason').fill(reason);
    await dialog.getByTestId('mod-confirm').click();
    await expect(dialog).toBeHidden();
  }

  const offerRow = (page: Page, sku: string) => page.getByTestId('mod-offers-table').locator(`tr[data-sku="${sku}"]`);

  test.beforeAll(async () => {
    supplierOrg = await Api.register(supplier);
    shopOrg = await Api.register(shop);
    const admin = await Api.login(ADMIN_EMAIL, ADMIN_PASSWORD);
    await admin.verifyWithModules(supplierOrg, ['ProductImport', 'Inventory', 'B2BSales']);
    await admin.verifyWithModules(shopOrg, ['B2BPurchasing']);
    await admin.dispose();
    const s = await Api.login(supplier.email, PASSWORD);
    await s.importAndCommit(supplierOrg, priceListCsv(items));
    await s.dispose();
    expect(await visibleSkus()).toEqual(items.map((i) => i.sku).sort());
  });

  test('admin blocks one offer with a reason; the shop no longer sees it; unblocking restores it', async ({ page }) => {
    await openModeration(page);
    const row = offerRow(page, items[0].sku);
    await row.getByTestId('mod-offer-block').click();
    await expect(page.getByTestId('mod-dialog-text')).toContainText(items[0].name);
    await confirm(page, 'Markenrecht ungeklärt');
    await expect(row).toHaveAttribute('data-blocked', 'true');
    await expect(row.getByTestId('mod-offer-reason')).toHaveText('Markenrecht ungeklärt');

    expect(await visibleSkus()).toEqual([items[1].sku]);

    // The shop's marketplace UI does not show it either.
    const shopPage = await page.context().browser()!.newPage();
    await login(shopPage, shop.email, PASSWORD);
    await shopPage.goto('/app/marktplatz');
    await shopPage.getByTestId('marketplace-search').fill(tag);
    const results = shopPage.getByTestId('marketplace-results');
    await expect(results.getByRole('row').filter({ hasText: items[1].name })).toHaveCount(1);
    await expect(results.getByRole('row').filter({ hasText: items[0].name })).toHaveCount(0);

    // The seller sees the block and cannot edit the offer.
    const sellerPage = await page.context().browser()!.newPage();
    await login(sellerPage, supplier.email, PASSWORD);
    await sellerPage.goto('/app/angebote');
    const own = sellerPage.getByTestId('offers-table').getByRole('row').filter({ hasText: items[0].sku });
    await expect(own.getByTestId('offer-blocked')).toContainText('Von der Plattform gesperrt');
    await expect(own.getByTestId('offer-blocked-reason')).toHaveText('Markenrecht ungeklärt');
    await expect(own.getByTestId('offer-edit')).toBeDisabled();
    await sellerPage.close();

    await row.getByTestId('mod-offer-unblock').click();
    await confirm(page, '');
    await expect(row).toHaveAttribute('data-blocked', 'false');
    expect(await visibleSkus()).toEqual(items.map((i) => i.sku).sort());
    await shopPage.reload();
    await shopPage.getByTestId('marketplace-search').fill(tag);
    await expect(shopPage.getByTestId('marketplace-results').getByRole('row').filter({ hasText: items[0].name })).toHaveCount(1);
    await shopPage.close();
  });

  test('admin blocks a selection in bulk and releases it again', async ({ page }) => {
    await openModeration(page);
    await page.getByTestId('mod-select-page').check();
    await expect(page.getByTestId('mod-selected-count')).toHaveText('Ausgewählt: 2');
    await page.getByTestId('mod-bulk-block').click();
    await confirm(page, 'Sammelprüfung');
    await expect(page.getByTestId('mod-selected-count')).toHaveText('Ausgewählt: 0');
    for (const i of items) await expect(offerRow(page, i.sku)).toHaveAttribute('data-blocked', 'true');
    expect(await visibleSkus()).toEqual([]);

    await page.getByTestId('mod-select-page').check();
    await page.getByTestId('mod-bulk-unblock').click();
    await confirm(page, '');
    for (const i of items) await expect(offerRow(page, i.sku)).toHaveAttribute('data-blocked', 'false');
    expect(await visibleSkus()).toEqual(items.map((i) => i.sku).sort());
  });

  test('admin blocks all offers of the seller; the seller sees why; lifting restores them', async ({ page }) => {
    await openModeration(page);
    await page.getByTestId('mod-seller-search').fill(supplier.name);
    await page.getByTestId('mod-seller-search').press('Enter');
    const seller = page.getByTestId('mod-sellers-table').locator(`tr[data-seller="${supplier.name}"]`);
    await expect(seller.getByTestId('mod-seller-offers')).toHaveText('2');
    await seller.getByTestId('mod-seller-block').click();
    await confirm(page, 'Lieferant gesperrt: Prüfung');
    await expect(seller).toHaveAttribute('data-blocked', 'true');
    await expect(seller.getByTestId('mod-seller-reason')).toHaveText('Lieferant gesperrt: Prüfung');
    expect(await visibleSkus()).toEqual([]);

    const sellerPage = await page.context().browser()!.newPage();
    await login(sellerPage, supplier.email, PASSWORD);
    await sellerPage.goto('/app/angebote');
    await expect(sellerPage.getByTestId('offers-seller-blocked')).toContainText('Lieferant gesperrt: Prüfung');
    await sellerPage.close();

    await seller.getByTestId('mod-seller-unblock').click();
    await confirm(page, 'Geklärt');
    await expect(seller).toHaveAttribute('data-blocked', 'false');
    expect(await visibleSkus()).toEqual(items.map((i) => i.sku).sort());
  });
});
