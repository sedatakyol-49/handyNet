import { InjectionToken } from '@angular/core';

/**
 * Base URL of the HandyNet API. Empty string = same origin (`/api/...`), which in development is
 * forwarded to http://localhost:5170 by `proxy.conf.json`. Override in app.config.ts for deployments
 * where the API lives on another origin.
 */
export const API_BASE_URL = new InjectionToken<string>('API_BASE_URL', { providedIn: 'root', factory: () => '' });
