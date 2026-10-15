import { randomUUID } from 'node:crypto';
import { APIRequestContext, Page, expect, request } from '@playwright/test';

export const API_URL = process.env['HN_API_URL'] ?? 'http://localhost:5170';
export const ADMIN_EMAIL = process.env['HN_ADMIN_EMAIL'] ?? '';
export const ADMIN_PASSWORD = process.env['HN_ADMIN_PASSWORD'] ?? '';
export const hasAdmin = !!ADMIN_EMAIL && !!ADMIN_PASSWORD;

/** Unique suffix per run so tests never collide with existing data. */
export const RUN = Date.now().toString(36) + Math.floor(Math.random() * 1e4).toString(36);

export const PASSWORD = 'E2e-Passwort-2026!';

/** Random EAN-13 with a valid check digit. */
export function randomEan(): string {
  const digits = '40' + Array.from({ length: 10 }, () => Math.floor(Math.random() * 10)).join('');
  const sum = [...digits].reduce((acc, d, i) => acc + Number(d) * (i % 2 === 0 ? 1 : 3), 0);
  return digits + ((10 - (sum % 10)) % 10);
}

export function priceListCsv(rows: { sku: string; ean: string; name: string; price: string; stock: number }[]): string {
  const header = 'Artikelnummer;EAN;Marke;Bezeichnung;Kategorie;Qualität;Preis;Bestand';
  return [header, ...rows.map((r) => `${r.sku};${r.ean};Apple;${r.name};Display;ServicePack;${r.price};${r.stock}`)].join('\n') + '\n';
}

export interface Company {
  name: string;
  email: string;
  displayName: string;
  wholesaler: boolean;
}

export function company(kind: 'supplier' | 'shop'): Company {
  return {
    name: kind === 'supplier' ? `E2E Großhandel ${RUN}` : `E2E Handyshop ${RUN}`,
    email: `${kind}-${RUN}@e2e.handynet.test`,
    displayName: kind === 'supplier' ? 'Sabine Lieferant' : 'Murat Shop',
    wholesaler: kind === 'supplier',
  };
}

// ---------------------------------------------------------------- UI helpers

export async function login(page: Page, email: string, password: string): Promise<void> {
  await page.goto('/login');
  await page.locator('#login-email').fill(email);
  await page.locator('#login-password').fill(password);
  await page.getByRole('button', { name: 'Anmelden' }).click();
  await expect(page).toHaveURL(/\/app\//);
}

export async function logout(page: Page): Promise<void> {
  await page.getByTestId('user-menu').click();
  await page.getByTestId('logout').click();
  await expect(page).toHaveURL(/\/login/);
}

export async function registerViaUi(page: Page, c: Company): Promise<void> {
  await page.goto('/registrierung');
  await page.locator('#reg-name').fill(c.name);
  await page.locator('#reg-legal').fill(`${c.name} GmbH`);
  await page.locator('#reg-vat').fill('DE' + String(Math.floor(1e8 + Math.random() * 9e8)));
  await page.locator('#reg-street').fill('Teststraße 1');
  await page.locator('#reg-zip').fill('10115');
  await page.locator('#reg-city').fill('Berlin');
  await page.getByLabel(c.wholesaler ? 'Großhandel' : 'Reparaturbetrieb').check();
  if (!c.wholesaler) await page.getByLabel('Einzelhandel').check();
  await page.locator('#reg-display').fill(c.displayName);
  await page.locator('#reg-email').fill(c.email);
  await page.locator('#reg-password').fill(PASSWORD);
  await page.getByTestId('register-submit').click();
  await expect(page).toHaveURL(/\/login\?registered=1/);
}

// ---------------------------------------------------------------- API helpers (test data setup)

export class Api {
  private constructor(
    private readonly ctx: APIRequestContext,
    private readonly token: string,
  ) {}

  static async login(email: string, password: string): Promise<Api> {
    const ctx = await request.newContext({ baseURL: API_URL });
    const res = await ctx.post('/api/auth/login', { data: { email, password } });
    expect(res.status(), await res.text()).toBe(200);
    return new Api(ctx, (await res.json()).accessToken);
  }

  static async register(c: Company): Promise<string> {
    const ctx = await request.newContext({ baseURL: API_URL });
    const res = await ctx.post('/api/registration', {
      data: {
        email: c.email,
        displayName: c.displayName,
        password: PASSWORD,
        organization: {
          name: c.name,
          legalName: `${c.name} GmbH`,
          street: 'Teststraße 1',
          postalCode: '10115',
          city: 'Berlin',
          countryCode: 'DE',
          isWholesaler: c.wholesaler,
          isRetailer: !c.wholesaler,
          isRepairProvider: !c.wholesaler,
        },
      },
    });
    expect(res.status(), await res.text()).toBe(201);
    return (await res.json()).organizationId;
  }

  private headers(org?: string): Record<string, string> {
    return { Authorization: `Bearer ${this.token}`, ...(org ? { 'X-Organization-Id': org } : {}) };
  }

  async verifyWithModules(organizationId: string, modules: string[]): Promise<void> {
    const v = await this.ctx.post(`/api/admin/organizations/${organizationId}/verify`, { headers: this.headers() });
    expect(v.status(), await v.text()).toBe(200);
    const m = await this.ctx.put(`/api/admin/organizations/${organizationId}/modules`, { headers: this.headers(), data: { modules } });
    expect(m.status(), await m.text()).toBe(200);
  }

  async importAndCommit(organizationId: string, csv: string): Promise<void> {
    const up = await this.ctx.post('/api/imports', {
      headers: this.headers(organizationId),
      multipart: { file: { name: 'preisliste.csv', mimeType: 'text/csv', buffer: Buffer.from(csv, 'utf8') } },
    });
    expect(up.status(), await up.text()).toBe(201);
    const batch = await up.json();
    expect(batch.summary.validRows).toBeGreaterThan(0);
    const c = await this.ctx.post(`/api/imports/${batch.id}/commit`, { headers: this.headers(organizationId) });
    expect(c.status(), await c.text()).toBe(200);
  }

  /** Changes the price (decimal euros) of the supplier's offer with the given SKU. */
  async setOfferPrice(organizationId: string, sku: string, price: number): Promise<void> {
    const list = await this.ctx.get('/api/offers', { headers: this.headers(organizationId), params: { search: sku } });
    expect(list.status(), await list.text()).toBe(200);
    const offer = (await list.json()).items.find((o: { supplierSku: string }) => o.supplierSku === sku);
    expect(offer, `offer ${sku}`).toBeTruthy();
    const res = await this.ctx.put(`/api/offers/${offer.id}`, {
      headers: this.headers(organizationId),
      data: { price, minOrderQuantity: offer.minOrderQuantity, deliveryDays: offer.deliveryDays, condition: offer.condition },
    });
    expect(res.status(), await res.text()).toBe(200);
  }

  /** Buyer searches the marketplace (`search`: product name or supplier SKU) and orders `quantity` of the offer with that SKU. */
  async placeOrder(organizationId: string, search: string, sku: string, quantity: number): Promise<{ id: string; number: string; grossTotalMinor: number }> {
    const list = await this.ctx.get('/api/marketplace/offers', { headers: this.headers(organizationId), params: { search } });
    expect(list.status(), await list.text()).toBe(200);
    const offer = (await list.json()).items.find((o: { supplierSku: string }) => o.supplierSku === sku);
    expect(offer, `marketplace offer ${sku}`).toBeTruthy();
    const res = await this.ctx.post('/api/orders', {
      headers: { ...this.headers(organizationId), 'Idempotency-Key': randomUUID() },
      data: { lines: [{ offerId: offer.offerId, quantity, expectedUnitPriceMinor: offer.priceMinor }] },
    });
    expect(res.status(), await res.text()).toBe(201);
    return res.json();
  }

  async confirmOrder(organizationId: string, orderId: string): Promise<void> {
    const res = await this.ctx.post(`/api/orders/${orderId}/confirm`, { headers: this.headers(organizationId) });
    expect(res.status(), await res.text()).toBe(200);
  }

  /** Seller marks a confirmed order as shipped without booking a shipment (Confirmed → Shipped). */
  async shipOrder(organizationId: string, orderId: string): Promise<void> {
    const res = await this.ctx.post(`/api/orders/${orderId}/ship`, { headers: this.headers(organizationId) });
    expect(res.status(), await res.text()).toBe(200);
  }

  /** Buyer confirms receipt (Shipped → Delivered); starts the inspection window. */
  async confirmDelivery(organizationId: string, orderId: string): Promise<void> {
    const res = await this.ctx.post(`/api/orders/${orderId}/confirm-delivery`, { headers: this.headers(organizationId), data: {} });
    expect(res.status(), await res.text()).toBe(200);
  }

  async dispose(): Promise<void> {
    await this.ctx.dispose();
  }
}
