import { chromium, request } from '@playwright/test';

/**
 * Global setup: warms the freshly started e2e stack before the first test. A cold API answers its first requests very
 * slowly while the browser and the SSR server compete for the CPU (a first login took > 10 s), which tripped the
 * 10 s expectations of whichever test ran first. Runs only against the e2e stack's database.
 */
export default async function warmUp(): Promise<void> {
  const apiUrl = process.env['HN_API_URL'] ?? 'http://localhost:5270';
  const baseUrl = process.env['HN_BASE_URL'] ?? 'http://localhost:4270';
  const email = process.env['HN_ADMIN_EMAIL'];
  const password = process.env['HN_ADMIN_PASSWORD'];
  if (!email || !password) return;

  const api = await request.newContext({ baseURL: apiUrl, timeout: 120_000 });
  try {
    let token = '';
    for (let i = 0; i < 3; i++) {
      const res = await api.post('/api/auth/login', { data: { email, password } });
      if (res.ok()) token = (await res.json()).accessToken;
    }
    const auth = { Authorization: `Bearer ${token}` };
    for (const path of ['/api/me', '/api/admin/organizations', '/api/admin/users', '/api/catalog/categories', '/api/admin/modules']) {
      await api.get(path, { headers: auth });
    }
  } finally {
    await api.dispose();
  }

  const browser = await chromium.launch();
  try {
    const page = await browser.newPage({ baseURL: baseUrl });
    await page.goto('/login');
    await page.locator('#login-email').fill(email);
    await page.locator('#login-password').fill(password);
    await page.getByRole('button', { name: 'Anmelden' }).click();
    await page.waitForURL(/\/app\//, { timeout: 120_000 });
    await page.goto('/app/administration');
    await page.waitForLoadState('networkidle');
  } finally {
    await browser.close();
  }
}
