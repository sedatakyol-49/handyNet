import { expect, test } from '@playwright/test';
import { ADMIN_EMAIL, ADMIN_PASSWORD, Api, PASSWORD, RUN, company, hasAdmin, login } from './support/handynet';

/**
 * Receipt printer profiles of a register (ADR-0018) through the UI against the real API: create, validate, edit, make
 * default, choose per device, test print and delete. The network printer points at a closed local port, so the test print
 * fails like an unreachable printer and must fall back to the browser's print dialog with an understandable message.
 */
test.describe('POS printer profiles', () => {
  test.skip(!hasAdmin, 'HN_ADMIN_EMAIL / HN_ADMIN_PASSWORD (or the API user-secrets) are needed to verify the test company.');
  const shop = { ...company('shop'), name: `E2E Druckershop ${RUN}`, email: `drucker-${RUN}@e2e.handynet.test` };
  let registerId = '';

  test.beforeAll(async () => {
    const orgId = await Api.register(shop);
    const admin = await Api.login(ADMIN_EMAIL, ADMIN_PASSWORD);
    await admin.verifyWithModules(orgId, ['Inventory', 'Pos']);
    await admin.dispose();
    const api = await Api.login(shop.email, PASSWORD);
    registerId = (await api.call<{ id: string }>('POST', '/api/pos/registers', orgId, { name: 'Theke' })).id;
    await api.dispose();
  });

  test('create, edit, default, device choice, test print with browser fallback, delete', async ({ page }) => {
    // Count print dialogs instead of opening them (top window and the hidden print iframe).
    await page.addInitScript(() => {
      const w = window as unknown as { __hnPrints?: number; top: Window & { __hnPrints?: number } };
      window.print = () => {
        try {
          w.top.__hnPrints = (w.top.__hnPrints ?? 0) + 1;
        } catch {
          w.__hnPrints = (w.__hnPrints ?? 0) + 1;
        }
      };
    });
    await login(page, shop.email, PASSWORD);
    await page.goto(`/kasse/${registerId}/drucker`);
    await expect(page.getByRole('heading', { name: 'Belegdrucker' })).toBeVisible();
    await expect(page.getByTestId('pos-tab-printers')).toHaveAttribute('aria-current', 'page');
    await expect(page.getByText('Noch kein Drucker eingerichtet')).toBeVisible();

    // ---- create a network printer (Epson ePOS) — the host is validated first
    await page.getByTestId('pos-printer-new').click();
    const dialog = page.getByTestId('pos-printer-dialog');
    await dialog.getByTestId('pf-name').fill('Theke Epson');
    await dialog.getByTestId('pf-connection').selectOption('EpsonEpos');
    await expect(dialog.getByTestId('pf-help')).toContainText('cgi-bin/epos/service.cgi');
    await dialog.getByTestId('pf-host').fill('http://127.0.0.1/drucker');
    await dialog.getByTestId('pf-save').click();
    await expect(dialog.getByText('Bitte eine IP-Adresse oder einen Hostnamen ohne http:// und Pfad eingeben.')).toBeVisible();
    await dialog.getByTestId('pf-host').fill('127.0.0.1');
    await dialog.getByTestId('pf-port').fill('47999'); // nothing listens here: behaves like a switched-off printer
    await dialog.getByTestId('pf-save').click();
    await expect(dialog).toBeHidden();

    const epson = page.locator('[data-printer="Theke Epson"]');
    await expect(epson).toBeVisible();
    await expect(epson.getByTestId('pos-printer-default')).toBeVisible(); // first profile = register default
    await expect(epson.getByTestId('pos-printer-address')).toHaveText('http://127.0.0.1:47999');
    await expect(epson).toContainText('80 mm');

    // ---- edit: 58 mm paper switches the typical 48 characters to 32
    await epson.getByTestId('pos-printer-edit').click();
    await dialog.getByTestId('pf-paper').selectOption({ label: '58 mm' });
    await expect(dialog.getByTestId('pf-chars')).toHaveValue('32');
    await dialog.getByTestId('pf-save').click();
    await expect(dialog).toBeHidden();
    await expect(epson).toContainText('58 mm');
    await expect(epson).toContainText('32');

    // ---- a second profile (system printer) becomes the default
    await page.getByTestId('pos-printer-new').click();
    await dialog.getByTestId('pf-name').fill('Systemdrucker');
    await dialog.getByTestId('pf-connection').selectOption('Browser');
    await dialog.getByTestId('pf-save').click();
    await expect(dialog).toBeHidden();
    const system = page.locator('[data-printer="Systemdrucker"]');
    await system.getByTestId('pos-printer-make-default').click();
    await expect(system.getByTestId('pos-printer-default')).toBeVisible();
    await expect(epson.getByTestId('pos-printer-default')).toHaveCount(0);
    await expect(page.getByTestId('pos-printer-effective')).toContainText('Systemdrucker');

    // ---- this device uses the Epson profile (stored in the browser only)
    await page.getByTestId('pos-printer-choice').selectOption({ label: 'Theke Epson' });
    await expect(page.getByTestId('pos-printer-effective')).toContainText('Theke Epson');

    // ---- test print on the unreachable network printer: clear error, then the browser print dialog
    await epson.getByTestId('pos-printer-test').click();
    const status = epson.getByTestId('pos-printer-status');
    await expect(status).toContainText('Drucker nicht erreichbar.', { timeout: 30_000 });
    await expect(status).toContainText('Der Testdruck wurde stattdessen im Druckdialog des Browsers geöffnet.');
    await expect(page.locator('iframe[data-hn-print-frame]')).toHaveCount(1);
    await expect.poll(() => page.evaluate(() => (window as unknown as { __hnPrints?: number }).__hnPrints ?? 0)).toBeGreaterThanOrEqual(1);

    // ---- test print on the system printer opens the print dialog directly
    await system.getByTestId('pos-printer-test').click();
    await expect(system.getByTestId('pos-printer-status')).toContainText('im Druckdialog geöffnet');
    await expect.poll(() => page.evaluate(() => (window as unknown as { __hnPrints?: number }).__hnPrints ?? 0)).toBeGreaterThanOrEqual(2);

    // ---- profiles are stored on the server, the device choice in the browser
    await page.reload();
    await expect(page.locator('[data-printer]')).toHaveCount(2);
    await expect(page.getByTestId('pos-printer-effective')).toContainText('Theke Epson');

    // ---- delete the profile this device uses: the device falls back to the register default
    await page.locator('[data-printer="Theke Epson"]').getByTestId('pos-printer-delete').click();
    await page.getByTestId('pos-printer-delete-confirm').click();
    await expect(page.locator('[data-printer="Theke Epson"]')).toHaveCount(0);
    await expect(page.getByTestId('pos-printer-effective')).toContainText('Systemdrucker');
  });
});
