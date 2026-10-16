import { expect, test } from '@playwright/test';
import { ADMIN_EMAIL, ADMIN_PASSWORD, Api, PASSWORD, RUN, company, hasAdmin, login, logout } from './support/handynet';

/**
 * Commission invoices (ADR-0009) against the real API:
 * 1. A freshly verified seller (B2BSales) opens "Rechnungen" from the navigation and from Finanzen: the list loads from
 *    `GET /api/invoices` and is empty (a new seller has no completed month with commission); an unknown invoice id
 *    shows "Rechnung nicht gefunden." (404 `invoicing.not_found`). A buyer-only company does not see the entry.
 * 2. The platform admin opens Administration → Provisionsrechnungen: the previous month is proposed, the current month
 *    is refused client-side, the idempotency is explained; issuing the previous month (confirmation dialog) shows the
 *    run result, and running it again issues nothing new (idempotent; the monthly job may already have issued it).
 *    Note: this issues real, immutable documents for the previous month in the target environment (exactly what the
 *    monthly job does there anyway).
 */
test.describe.serial('commission invoices', () => {
  test.skip(!hasAdmin, 'Set HN_ADMIN_EMAIL and HN_ADMIN_PASSWORD to run e2e tests against the API.');

  const seller = { ...company('supplier'), name: `E2E Rechnung Verkäufer ${RUN}`, email: `inv-seller-${RUN}@e2e.handynet.test` };
  const buyer = { ...company('shop'), name: `E2E Rechnung Käufer ${RUN}`, email: `inv-buyer-${RUN}@e2e.handynet.test` };

  test.beforeAll(async () => {
    const sellerOrg = await Api.register(seller);
    const buyerOrg = await Api.register(buyer);
    const admin = await Api.login(ADMIN_EMAIL, ADMIN_PASSWORD);
    await admin.verifyWithModules(sellerOrg, ['B2BSales']);
    await admin.verifyWithModules(buyerOrg, ['B2BPurchasing']);
    await admin.dispose();
  });

  test('seller opens the invoice list; buyers do not see it', async ({ page }) => {
    await login(page, seller.email, PASSWORD);
    const nav = page.getByRole('navigation', { name: 'Hauptnavigation' });
    const invoicesResponse = page.waitForResponse((r) => r.url().includes('/api/invoices') && r.request().method() === 'GET');
    await nav.getByRole('link', { name: 'Rechnungen', exact: true }).click();
    await expect(page).toHaveURL(/\/app\/finanzen\/rechnungen$/);
    expect((await invoicesResponse).status()).toBe(200);

    await expect(page.getByRole('heading', { name: 'Provisionsrechnungen', level: 1 })).toBeVisible();
    const table = page.getByTestId('invoices-table');
    await expect(table.getByRole('columnheader', { name: 'Abrechnungsmonat' })).toBeVisible();
    await expect(table.getByRole('columnheader', { name: 'Brutto' })).toBeVisible();
    await expect(table).toContainText('Noch keine Provisionsrechnungen');
    // Only "Rechnungen" is the active entry, not "Finanzen" as well.
    await expect(nav.getByRole('link', { name: 'Rechnungen', exact: true })).toHaveAttribute('aria-current', 'page');
    await expect(nav.getByRole('link', { name: 'Finanzen', exact: true })).not.toHaveAttribute('aria-current', 'page');

    // Unknown / foreign invoice ids: the API answers 404 invoicing.not_found, shown translated.
    await page.goto('/app/finanzen/rechnungen/00000000-0000-0000-0000-000000000001');
    await expect(page.getByText('Rechnung nicht gefunden.')).toBeVisible();
    await page.getByTestId('invoice-back').click();
    await expect(page).toHaveURL(/\/app\/finanzen\/rechnungen$/);

    // Also reachable from Finanzen.
    await nav.getByRole('link', { name: 'Finanzen', exact: true }).click();
    await expect(page).toHaveURL(/\/app\/finanzen$/);
    await page.getByTestId('finance-invoices').click();
    await expect(page).toHaveURL(/\/app\/finanzen\/rechnungen$/);
    await logout(page);

    await login(page, buyer.email, PASSWORD);
    await expect(page.getByRole('navigation', { name: 'Hauptnavigation' }).getByRole('link', { name: 'Marktplatz' })).toBeVisible();
    await expect(page.getByRole('navigation', { name: 'Hauptnavigation' }).getByRole('link', { name: 'Rechnungen', exact: true })).toHaveCount(0);
  });

  test('admin issues the previous month and re-runs it idempotently', async ({ page }) => {
    await login(page, ADMIN_EMAIL, ADMIN_PASSWORD);
    await page.goto('/app/administration');
    await page.getByTestId('admin-tabs').getByRole('link', { name: 'Provisionsrechnungen' }).click();
    await expect(page).toHaveURL(/\/app\/administration\/rechnungen$/);
    await expect(page.getByTestId('invoice-idempotent-hint')).toContainText('Erneutes Ausführen ist unbedenklich');

    const month = page.getByTestId('invoice-issue-month');
    const previous = await month.inputValue();
    expect(previous).toMatch(/^\d{4}-\d{2}$/);
    await expect(page.getByTestId('admin-invoices-table')).toBeVisible();

    // The current month is not over yet → refused before calling the API.
    const [y, m] = previous.split('-').map(Number);
    const current = m === 12 ? `${y + 1}-01` : `${y}-${String(m + 1).padStart(2, '0')}`;
    await month.fill(current);
    await page.getByTestId('invoice-issue-submit').click();
    await expect(page.getByText('Dieser Monat ist noch nicht abgeschlossen')).toBeVisible();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    // First run for the previous month.
    await month.fill(previous);
    await page.getByTestId('invoice-issue-submit').click();
    const dialog = page.getByRole('dialog', { name: 'Rechnungen ausstellen?' });
    await expect(dialog).toBeVisible();
    await expect(dialog.getByTestId('invoice-issue-confirm-text')).toContainText('endgültig');
    const first = page.waitForResponse((r) => r.url().endsWith('/api/admin/invoices/issue'));
    await dialog.getByTestId('invoice-issue-confirm').click();
    expect((await first).status()).toBe(200);
    const result = page.getByTestId('invoice-issue-result');
    await expect(result).toBeVisible();
    await expect(dialog).toBeHidden();
    const sellersChecked = await result.getByTestId('invoice-stat-sellers').locator('dd').innerText();

    // Second run: nothing new is issued, existing documents are only counted.
    await page.getByTestId('invoice-issue-submit').click();
    const second = page.waitForResponse((r) => r.url().endsWith('/api/admin/invoices/issue'));
    await dialog.getByTestId('invoice-issue-confirm').click();
    const body = await (await second).json();
    expect(body.invoicesIssued).toBe(0);
    expect(body.creditNotesIssued).toBe(0);
    await expect(result.getByTestId('invoice-stat-invoices').locator('dd')).toHaveText('0');
    await expect(result.getByTestId('invoice-stat-corrections').locator('dd')).toHaveText('0');
    await expect(result.getByTestId('invoice-stat-sellers').locator('dd')).toHaveText(sellersChecked);
    await expect(result.getByTestId('invoice-stat-existing').locator('dd')).toHaveText(String(body.alreadyIssued));
    if (body.failures.length === 0) {
      await expect(result.getByTestId('invoice-issue-outcome')).toContainText(body.alreadyIssued > 0 ? 'Nichts Neues' : 'Keine Verkäufer');
      // The month listing (GET /api/admin/invoices) shows every document of the run.
      const rows = page.getByTestId('admin-invoices-table').locator('tbody tr[data-invoice-number]');
      await expect(rows).toHaveCount(body.documents.length);
    } else {
      await expect(result.getByTestId('invoice-issue-outcome')).toContainText('fehlgeschlagen');
      await expect(page.getByTestId('invoice-issue-failures').locator('tbody tr')).toHaveCount(body.failures.length);
    }
  });
});
