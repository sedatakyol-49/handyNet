import { defineConfig, devices } from '@playwright/test';

/**
 * End-to-end tests run against the real HandyNet API.
 *   - API:      HN_API_URL      (default http://localhost:5170, must be running)
 *   - Frontend: HN_BASE_URL     (default http://localhost:4200; `npm start` is launched if nothing listens there)
 *   - Admin:    HN_ADMIN_EMAIL / HN_ADMIN_PASSWORD (platform admin of the target environment; never commit them)
 */
export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 120_000,
  expect: { timeout: 10_000 },
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: process.env['HN_BASE_URL'] ?? 'http://localhost:4200',
    locale: 'de-DE',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: {
    command: 'npm start',
    url: process.env['HN_BASE_URL'] ?? 'http://localhost:4200',
    reuseExistingServer: true,
    timeout: 180_000,
  },
});
