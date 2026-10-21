import { defineConfig, devices } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';

/**
 * End-to-end tests run against the real SEHA API, on their OWN stack and database so they never touch the
 * development data:
 *   - API:      HN_API_URL  (default http://localhost:5270) — started from backend/ with database `handynet_e2e`
 *   - Frontend: HN_BASE_URL (default http://localhost:4270) — production build in .artifacts/e2e-web, SSR server forwards /api to :5270
 *   - Admin:    HN_ADMIN_EMAIL / HN_ADMIN_PASSWORD; when unset they are read from the API's user-secrets
 *               (Bootstrap:AdminEmail / Bootstrap:AdminPassword), the same account the API bootstraps.
 * The connection strings are taken from the API's user-secrets with the database name replaced by `handynet_e2e`;
 * the database is created and migrated on start. Nothing secret is printed or written to disk.
 * Running against the development servers (:4200 / :5170) is refused unless HN_E2E_ALLOW_DEV_DB=1.
 */
const E2E_DB = 'handynet_e2e';
const apiUrl = process.env['HN_API_URL'] ?? 'http://localhost:5270';
const baseUrl = process.env['HN_BASE_URL'] ?? 'http://localhost:4270';
process.env['HN_API_URL'] = apiUrl;

if (process.env['HN_E2E_ALLOW_DEV_DB'] !== '1' && (/:4200\b/.test(baseUrl) || /:5170\b/.test(apiUrl))) {
  throw new Error('e2e must not run against the development servers (:4200/:5170, database handynet). '
    + 'Use the default e2e stack, or set HN_E2E_ALLOW_DEV_DB=1 if you really mean it.');
}

const repoRoot = resolve(__dirname, '..');
const apiProject = resolve(repoRoot, 'backend/src/HandyNet.Api/HandyNet.Api.csproj');
const apiOut = resolve(repoRoot, '.artifacts/e2e-api');
const webOut = resolve(repoRoot, '.artifacts/e2e-web');

/** Reads the API's user-secrets once (only when we start our own API or need the admin login). */
function userSecrets(): Record<string, string> {
  const text = execFileSync('dotnet', ['user-secrets', 'list', '--project', apiProject], { encoding: 'utf8' });
  const secrets: Record<string, string> = {};
  for (const line of text.split(/\r?\n/)) {
    const i = line.indexOf(' = ');
    if (i > 0) secrets[line.slice(0, i).trim()] = line.slice(i + 3).trim();
  }
  return secrets;
}

function withDatabase(connectionString: string | undefined, database: string): string {
  if (!connectionString) throw new Error('Connection string missing in the API user-secrets.');
  return /Database=[^;]*/i.test(connectionString)
    ? connectionString.replace(/Database=[^;]*/i, `Database=${database}`)
    : `${connectionString};Database=${database}`;
}

const ownStack = /localhost:5270\b/.test(apiUrl);
const secrets = ownStack || !process.env['HN_ADMIN_EMAIL'] ? userSecrets() : {};
process.env['HN_ADMIN_EMAIL'] ??= secrets['Bootstrap:AdminEmail'];
process.env['HN_ADMIN_PASSWORD'] ??= secrets['Bootstrap:AdminPassword'];

const apiServer = ownStack
  ? (() => {
      mkdirSync(apiOut, { recursive: true });
      const dll = resolve(apiOut, 'HandyNet.Api.dll');
      return {
        // Separate output folder: the development API keeps running from its own bin/ without file locks.
        command: `dotnet build "${apiProject}" -c Release -o "${apiOut}" --nologo -v q && dotnet "${dll}" migrate && dotnet "${dll}"`,
        cwd: apiOut,
        url: `${apiUrl}/health/ready`,
        reuseExistingServer: true,
        timeout: 300_000,
        env: {
          ASPNETCORE_ENVIRONMENT: 'Development',
          ASPNETCORE_URLS: apiUrl,
          ConnectionStrings__HandyNet: withDatabase(secrets['ConnectionStrings:HandyNet'], E2E_DB),
          ConnectionStrings__HandyNetMigrations: withDatabase(secrets['ConnectionStrings:HandyNetMigrations'], E2E_DB),
          Payments__ApiBaseUrl: apiUrl,
          Payments__WebBaseUrl: baseUrl,
          Notifications__PublicBaseUrl: baseUrl,
          FileStorage__Local__RootPath: resolve(apiOut, 'App_Data/files'),
          RateLimits__AuthPerMinute: '1000',
          RateLimits__RefreshPerMinute: '1000',
          RateLimits__AccountEmailsPerHour: '1000',
        },
      };
    })()
  : null;

export default defineConfig({
  testDir: './e2e',
  globalSetup: './e2e/support/warm-up.ts',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 120_000,
  expect: { timeout: 10_000 },
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: baseUrl,
    locale: 'de-DE',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: [
    ...(apiServer ? [apiServer] : []),
    /localhost:4270\b/.test(baseUrl)
      ? {
          // Production build served by the SSR server (no second Vite dev server next to `npm start`, which shares its
          // cache and keeps re-optimizing/reloading); /api is forwarded to the e2e API.
          command: `npx ng build --output-path "${webOut}" && node "${resolve(webOut, 'server/server.mjs')}"`,
          url: baseUrl,
          reuseExistingServer: true,
          timeout: 300_000,
          env: { PORT: new URL(baseUrl).port, HN_API_URL: apiUrl },
        }
      : { command: 'npm start', url: baseUrl, reuseExistingServer: true, timeout: 180_000 },
  ],
});
