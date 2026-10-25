import { Page, expect, test } from '@playwright/test';
import { ADMIN_EMAIL, ADMIN_PASSWORD, Api, PASSWORD, RUN, company, drawSignature, hasAdmin, login, logout, publishAnkaufList, randomImei } from './support/handynet';

/**
 * Ankauf with seller capture, signature and till payout (ADR-0015):
 * 1. MRZ paste (German specimen passport, as a keyboard-wedge scanner would type it) fills the seller form; the seller
 *    signs price + declaration on screen; the cash is taken from the open till; the Z-report (interim) shows the Ankauf
 *    section and the Tagesbericht lists the purchase.
 * 2. QR self check-in: the customer opens the link on "their phone" (separate browser context, no login), enters the data
 *    and signs; the employee sees it, takes it over and receives the device.
 */
test.describe.serial('Ankauf capture, signature and till payout', () => {
  test.skip(!hasAdmin, 'Set HN_ADMIN_EMAIL and HN_ADMIN_PASSWORD to run e2e tests against the API.');

  const brand = `E2ECAP${RUN}`;
  const model = 'Capturephone 15';
  const shop = { ...company('shop'), name: `E2E Ankauf Capture ${RUN}`, email: `capture-shop-${RUN}@e2e.handynet.test` };
  const passportMrz = 'P<D<<MUSTERMANN<<ERIKA<<<<<<<<<<<<<<<<<<<<<<\nC01X00T478D<<6408125F2702283<<<<<<<<<<<<<<<4';
  let orgId = '';
  let sessionId = '';
  let registerId = '';

  test.beforeAll(async () => {
    const admin = await Api.login(ADMIN_EMAIL, ADMIN_PASSWORD);
    orgId = await Api.register(shop);
    await admin.verifyWithModules(orgId, ['Ankauf', 'Inventory', 'Pos', 'B2BPurchasing']);
    await admin.dispose();
    const owner = await Api.login(shop.email, PASSWORD);
    const register = await owner.call<{ id: string }>('POST', '/api/pos/registers', orgId, { name: `Ankaufkasse ${RUN}` });
    registerId = register.id;
    sessionId = (await owner.call<{ id: string }>('POST', `/api/pos/registers/${register.id}/sessions`, orgId, { openingCashMinor: 500_00 })).id;
    await owner.dispose();
    await publishAnkaufList(`E2E Capture ${RUN}`, [{ brand, model, storage: '128 GB', price: 300 }]);
  });

  async function quoteAndStart(page: Page): Promise<void> {
    await page.goto('/app/ankauf');
    await page.getByTestId('ak-search').fill(`${brand} ${model}`);
    await page.getByTestId('ak-models').getByRole('button', { name: new RegExp(model) }).click();
    await page.locator('input[data-condition="Gut"]').check();
    await page.getByTestId('ak-submit').click();
    await expect(page.getByTestId('ak-platform-offer')).toHaveText(/^300,00\s€$/);
    await page.getByTestId('ak-start-intake').click();
    await expect(page).toHaveURL(/\/app\/ankauf\/ankaeufe\/neu\//);
  }

  test('MRZ paste, on-screen signature and cash from the till appear in the Z-report and the Tagesbericht', async ({ page }) => {
    await login(page, shop.email, PASSWORD);
    await quoteAndStart(page);

    // A wrong check digit is refused live; the correct MRZ fills name, birth date and the document.
    const mrz = page.getByTestId('in-mrz');
    await mrz.fill(passportMrz.slice(0, -1) + '5');
    await expect(page.getByTestId('in-mrz-status')).toHaveAttribute('data-state', 'error');
    await expect(page.getByTestId('in-mrz-status')).toContainText('Prüfziffer falsch');
    await mrz.fill(passportMrz);
    await expect(page.getByTestId('in-mrz-status')).toHaveAttribute('data-state', 'ok');
    await expect(page.getByTestId('in-last')).toHaveValue('Mustermann');
    await expect(page.getByTestId('in-first')).toHaveValue('Erika');
    await expect(page.getByTestId('in-birth')).toHaveValue('1964-08-12');
    await expect(page.getByTestId('in-idtype')).toHaveValue(/Passport/);
    await expect(page.getByTestId('in-idno')).toHaveValue('C01X00T47');
    await expect(page.getByTestId('in-expiry')).toHaveValue('2027-02-28');
    await expect(page.getByTestId('intake-capture-source')).toContainText('Prüfziffern korrekt');
    await page.getByTestId('in-street').fill('Heidestraße 17');
    await page.getByTestId('in-zip').fill('51147');
    await page.getByTestId('in-city').fill('Köln');
    await page.getByTestId('in-imei').fill(randomImei());
    await page.getByTestId('in-receive').click();

    await expect(page.getByTestId('intake-status')).toHaveText('Angenommen');
    await expect(page.getByTestId('seller-capture')).toHaveText('Ausweis gescannt');
    await expect(page.getByTestId('seller-card')).toContainText('*****0T47');

    await page.getByTestId('in-inspect').click();
    await expect(page.getByTestId('intake-status')).toHaveText('Geprüft');

    // The seller signs 250,00 € in cash.
    await page.getByTestId('in-paid').fill('250');
    await page.locator('input[data-method="Cash"]').check();
    await drawSignature(page, 'intake-signature-pad');
    await page.getByTestId('in-sign').click();
    await expect(page.getByTestId('intake-signature-state')).toHaveAttribute('data-state', 'valid');

    // Cash from the open till (preselected: the only register with an open session).
    await expect(page.getByTestId('in-tills')).toContainText(`Ankaufkasse ${RUN}`);
    await expect(page.getByTestId('in-tills')).toContainText('500,00');
    await page.getByTestId('in-complete').click();
    await expect(page.getByTestId('intake-confirm-register')).toHaveText(`Ankaufkasse ${RUN}`);
    await page.getByTestId('intake-confirm-submit').click();
    await expect(page.getByTestId('intake-status')).toHaveText('Abgeschlossen');
    await expect(page.getByTestId('intake-till')).toBeVisible();

    // HTML Ankaufbeleg with the signature image.
    await page.getByTestId('intake-beleg-view').click();
    await expect(page.getByTestId('beleg-print')).toContainText('Ankaufbeleg');
    await expect(page.getByTestId('beleg-amount')).toHaveText(/^250,00\s€$/);
    await expect(page.getByTestId('beleg-signature')).toBeVisible();

    // Interim Z-report of the till: Ankauf section and the reduced expected cash.
    await page.goto(`/kasse/sitzungen/${sessionId}`);
    await expect(page.getByTestId('z-ankauf-count')).toHaveText('1');
    await expect(page.getByTestId('z-ankauf-total')).toHaveText(/^-250,00\s€$/);
    await expect(page.getByTestId('z-expected')).toHaveText(/^250,00\s€$/);

    // Tagesbericht (back office): the purchase with its payment method and the till payout.
    await page.goto('/app/berichte/tagesbericht');
    await expect(page.getByTestId('dr-ankauf-total')).toHaveText(/^250,00\s€$/);
    await expect(page.getByTestId('dr-devices')).toContainText(model);
    await expect(page.getByTestId('dr-pos-ankauf')).toHaveText(/^250,00\s€$/);
    await expect(page.getByTestId('dr-bought')).toHaveText(/^250,00\s€$/);
    await expect(page.getByTestId('dr-purchases')).toBeVisible();
    await logout(page);
  });

  test('QR self check-in: the customer enters and signs on their phone, the employee takes it over', async ({ page, browser }) => {
    await login(page, shop.email, PASSWORD);
    await quoteAndStart(page);
    await page.getByTestId('in-tab-checkin').click();
    await page.getByTestId('checkin-start').click();
    await expect(page.getByTestId('checkin-open').getByTestId('qr-code')).toBeVisible();
    const url = (await page.getByTestId('checkin-url').textContent())!.trim();
    expect(url).toMatch(/\/checkin\/[A-Za-z0-9_-]{43}$/);

    // The customer's phone: no login, own context.
    const phoneContext = await browser.newContext({ locale: 'de-DE', viewport: { width: 390, height: 844 }, hasTouch: true });
    const phone = await phoneContext.newPage();
    await phone.goto(url);
    await expect(phone.getByTestId('checkin-shop')).toHaveText(shop.name);
    await phone.getByTestId('pc-first').fill('Ayşe');
    await phone.getByTestId('pc-last').fill(`Yilmaz${RUN}`);
    await phone.getByTestId('pc-birth').fill('1990-03-04');
    await phone.getByTestId('pc-street').fill('Lindenweg 3');
    await phone.getByTestId('pc-zip').fill('50667');
    await phone.getByTestId('pc-city').fill('Köln');
    await phone.getByTestId('pc-type').selectOption({ label: 'Personalausweis' });
    await phone.getByTestId('pc-no').fill('T22000129');
    await phone.getByTestId('pc-exp').fill('2031-10-31');
    await phone.getByTestId('pc-submit').click();
    await expect(phone.getByText('Bitte bestätigen Sie den Hinweis.')).toBeVisible();
    await drawSignature(phone, 'pc-signature');
    await phone.getByTestId('pc-consent').check();
    await phone.getByTestId('pc-submit').click();
    await expect(phone.getByTestId('checkin-done')).toBeVisible();
    // Single use: opening the link again does not offer the form.
    await phone.reload();
    await expect(phone.getByTestId('checkin-already')).toBeVisible();
    await phoneContext.close();

    // The counter sees the data (polling), verifies it and takes it over.
    await expect(page.getByTestId('checkin-submitted')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByTestId('checkin-name')).toHaveText(`Ayşe Yilmaz${RUN}`);
    await expect(page.getByTestId('checkin-signature')).toBeVisible();
    await page.getByTestId('checkin-take').click();
    await expect(page.getByTestId('in-last')).toHaveValue(`Yilmaz${RUN}`);
    await expect(page.getByTestId('in-expiry')).toHaveValue('2031-10-31');
    await page.getByTestId('in-imei').fill(randomImei());
    await page.getByTestId('in-receive').click();
    await expect(page.getByTestId('intake-status')).toHaveText('Angenommen');
    await expect(page.getByTestId('seller-capture')).toHaveText('QR-Selbsterfassung');

    // The link is used up for good.
    const anonymous = await browser.newContext();
    const again = await anonymous.newPage();
    await again.goto(url);
    await expect(again.getByTestId('checkin-missing')).toBeVisible();
    await anonymous.close();

    // The phone declaration signature is attached; the purchase still needs a signature at the counter.
    await page.getByTestId('in-inspect').click();
    await expect(page.getByTestId('intake-checkin-signature')).toBeVisible();
    await expect(page.locator('[data-step="sign"]')).toHaveAttribute('data-state', 'current');

    // Inside the POS app (Petrol design): the register's Ankauf tab lists the intakes and opens them in the POS layout.
    await page.goto(`/kasse/${registerId}/ankauf/ankaeufe`);
    await expect(page.getByTestId('pos-tab-ankauf')).toBeVisible();
    await expect(page.getByTestId('pos-topbar')).toBeVisible();
    await page.locator(`[data-intake]`).filter({ hasText: `Yilmaz${RUN}` }).getByRole('link').click();
    await expect(page).toHaveURL(new RegExp(`/kasse/${registerId}/ankauf/ankaeufe/[0-9a-f-]{36}$`));
    await expect(page.getByTestId('intake-sign')).toBeVisible();
    await page.goto('/app/uebersicht');
    await logout(page);
  });
});
