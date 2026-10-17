import { TestBed } from '@angular/core/testing';
import { HttpClient, HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { authInterceptor, ORGANIZATION_HEADER } from './auth.interceptor';
import { SessionStore, TOKEN_KEY } from './session.store';
import { REFRESH_LOCK, RefreshLock } from './token-refresher';
import { Me, TokenResponse } from '../api/models';

const ME: Me = {
  userId: 'u1',
  email: 'owner@example.de',
  displayName: 'Owner',
  isPlatformAdmin: false,
  emailVerified: true,
  memberships: [
    {
      organizationId: 'org-1',
      organizationName: 'Org One',
      role: 'Owner',
      status: 'Verified',
      modules: ['B2BPurchasing'],
      permissions: ['marketplace.browse'],
      isWholesaler: false,
      isRetailer: true,
      isRepairProvider: false,
    },
    {
      organizationId: 'org-2',
      organizationName: 'Org Two',
      role: 'Staff',
      status: 'PendingVerification',
      modules: [],
      permissions: [],
      isWholesaler: true,
      isRetailer: false,
      isRepairProvider: false,
    },
  ],
};

function tokens(access: string, refresh: string, accessValidMs = 3600_000): TokenResponse {
  return {
    accessToken: access,
    expiresAt: new Date(Date.now() + accessValidMs).toISOString(),
    refreshToken: refresh,
    refreshExpiresAt: new Date(Date.now() + 14 * 86400_000).toISOString(),
    userId: 'u1',
    email: ME.email,
    displayName: ME.displayName,
    isPlatformAdmin: false,
    emailVerified: true,
  };
}

const unauthorized = { status: 401, statusText: 'Unauthorized' };
/** Lets the promise-based refresh (lock → HTTP) reach the testing backend. */
const settle = () => new Promise<void>((resolve) => setTimeout(resolve, 0));

describe('authInterceptor', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;
  let session: SessionStore;
  let router: Router;
  /** Runs before the locked refresh task (simulates what another tab did while this one waited for the lock). */
  let beforeLockedTask: (() => void) | null;

  beforeEach(() => {
    localStorage.clear();
    beforeLockedTask = null;
    const lock: RefreshLock = {
      run: async (task) => {
        await Promise.resolve();
        beforeLockedTask?.();
        return task();
      },
    };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        { provide: REFRESH_LOCK, useValue: lock },
      ],
    });
    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
    session = TestBed.inject(SessionStore);
    router = TestBed.inject(Router);
  });

  afterEach(() => httpMock.verify());

  function signIn(response = tokens('token-123', 'refresh-1')): void {
    session.setToken(response);
    session.setMe(ME);
  }

  function expectRefresh(refreshToken: string): TestRequest {
    const req = httpMock.expectOne('/api/auth/refresh');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ refreshToken });
    expect(req.request.headers.has('Authorization')).toBe(false);
    return req;
  }

  it('sends no auth headers when signed out', () => {
    http.get('/api/marketplace/offers').subscribe();
    const req = httpMock.expectOne('/api/marketplace/offers');
    expect(req.request.headers.has('Authorization')).toBe(false);
    expect(req.request.headers.has(ORGANIZATION_HEADER)).toBe(false);
    req.flush({});
  });

  it('adds bearer token and the selected organization to tenant-scoped calls', () => {
    signIn();
    http.get('/api/marketplace/offers').subscribe();
    const req = httpMock.expectOne('/api/marketplace/offers');
    expect(req.request.headers.get('Authorization')).toBe('Bearer token-123');
    expect(req.request.headers.get(ORGANIZATION_HEADER)).toBe('org-1');
    req.flush({});
  });

  it('follows the organization switcher', () => {
    signIn();
    session.selectOrganization('org-2');
    http.get('/api/orders').subscribe();
    const req = httpMock.expectOne('/api/orders');
    expect(req.request.headers.get(ORGANIZATION_HEADER)).toBe('org-2');
    req.flush({});
  });

  it('does not send the organization header to /api/me and login', () => {
    signIn();
    http.get('/api/me').subscribe();
    const me = httpMock.expectOne('/api/me');
    expect(me.request.headers.get('Authorization')).toBe('Bearer token-123');
    expect(me.request.headers.has(ORGANIZATION_HEADER)).toBe(false);
    me.flush(ME);
  });

  it('leaves non-API requests untouched', () => {
    signIn();
    http.get('https://fonts.example.com/inter.css').subscribe();
    const req = httpMock.expectOne('https://fonts.example.com/inter.css');
    expect(req.request.headers.has('Authorization')).toBe(false);
    req.flush('');
  });

  it('refreshes on 401 and retries the request once with the new token', async () => {
    signIn();
    let result: unknown;
    http.get('/api/orders').subscribe((r) => (result = r));
    httpMock.expectOne('/api/orders').flush({ title: 'Unauthorized' }, unauthorized);
    await settle();

    expectRefresh('refresh-1').flush(tokens('token-456', 'refresh-2'));
    await settle();

    const retry = httpMock.expectOne('/api/orders');
    expect(retry.request.headers.get('Authorization')).toBe('Bearer token-456');
    expect(retry.request.headers.get(ORGANIZATION_HEADER)).toBe('org-1');
    retry.flush({ items: [] });

    expect(result).toEqual({ items: [] });
    expect(session.token()).toBe('token-456');
    expect(session.refreshToken()).toBe('refresh-2');
    expect(JSON.parse(localStorage.getItem(TOKEN_KEY)!).refreshToken).toBe('refresh-2');
  });

  it('shares one in-flight refresh between concurrent requests', async () => {
    signIn();
    const results: unknown[] = [];
    for (const url of ['/api/orders', '/api/offers', '/api/inventory/stock']) http.get(url).subscribe((r) => results.push(r));
    for (const url of ['/api/orders', '/api/offers', '/api/inventory/stock']) httpMock.expectOne(url).flush(null, unauthorized);
    await settle();

    // Exactly one refresh call although three requests failed.
    expectRefresh('refresh-1').flush(tokens('token-456', 'refresh-2'));
    await settle();

    for (const url of ['/api/orders', '/api/offers', '/api/inventory/stock']) {
      const retry = httpMock.expectOne(url);
      expect(retry.request.headers.get('Authorization')).toBe('Bearer token-456');
      retry.flush({ url });
    }
    expect(results).toHaveLength(3);
  });

  it('retries without a new refresh when the pair was already rotated by an earlier request', async () => {
    signIn();
    http.get('/api/orders').subscribe();
    // Another request rotated the pair while this one was on the wire.
    session.setToken(tokens('token-456', 'refresh-2'));
    httpMock.expectOne('/api/orders').flush(null, unauthorized);
    await settle();

    httpMock.expectNone('/api/auth/refresh');
    httpMock.expectOne('/api/orders').flush({});
  });

  it('refreshes shortly before the access token expires, before sending the request', async () => {
    signIn(tokens('token-old', 'refresh-1', 20_000)); // expires in 20 s (< 60 s leeway)
    http.get('/api/orders').subscribe();
    await settle();

    httpMock.expectNone('/api/orders');
    expectRefresh('refresh-1').flush(tokens('token-new', 'refresh-2'));
    await settle();

    const req = httpMock.expectOne('/api/orders');
    expect(req.request.headers.get('Authorization')).toBe('Bearer token-new');
    req.flush({});
  });

  it('uses the pair another tab rotated while this tab waited for the lock (no second refresh)', async () => {
    signIn();
    beforeLockedTask = () => localStorage.setItem(TOKEN_KEY, JSON.stringify({ ...tokens('token-tab2', 'refresh-tab2'), userId: 'u1' }));
    http.get('/api/orders').subscribe();
    httpMock.expectOne('/api/orders').flush(null, unauthorized);
    await settle();
    await settle();

    httpMock.expectNone('/api/auth/refresh');
    const retry = httpMock.expectOne('/api/orders');
    expect(retry.request.headers.get('Authorization')).toBe('Bearer token-tab2');
    retry.flush({});
    expect(session.refreshToken()).toBe('refresh-tab2');
  });

  it('signs out and reports the original 401 when the refresh token is rejected', async () => {
    signIn();
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    const errors: unknown[] = [];
    http.get('/api/orders').subscribe({ error: (e) => errors.push(e) });
    http.get('/api/offers').subscribe({ error: (e) => errors.push(e) });
    httpMock.expectOne('/api/orders').flush(null, unauthorized);
    httpMock.expectOne('/api/offers').flush(null, unauthorized);
    await settle();

    expectRefresh('refresh-1').flush({ code: 'auth.refresh_token_reused', title: 'Reused' }, unauthorized);
    await settle();

    httpMock.expectNone('/api/orders'); // no retry
    expect(errors).toHaveLength(2);
    expect(errors.every((e) => e instanceof HttpErrorResponse && e.status === 401 && e.url?.endsWith('/api/auth/refresh') === false)).toBe(true);
    expect(session.isAuthenticated()).toBe(false);
    expect(localStorage.getItem(TOKEN_KEY)).toBeNull();
    expect(navigate).toHaveBeenCalledTimes(1);
    expect(navigate).toHaveBeenCalledWith(['/login'], expect.objectContaining({ queryParams: expect.objectContaining({ expired: 1 }) }));
  });

  it('signs out when the retried request is 401 again (no second refresh)', async () => {
    signIn();
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    let failed = false;
    http.get('/api/orders').subscribe({ error: () => (failed = true) });
    httpMock.expectOne('/api/orders').flush(null, unauthorized);
    await settle();
    expectRefresh('refresh-1').flush(tokens('token-456', 'refresh-2'));
    await settle();
    httpMock.expectOne('/api/orders').flush(null, unauthorized);
    await settle();

    httpMock.expectNone('/api/auth/refresh');
    expect(failed).toBe(true);
    expect(session.isAuthenticated()).toBe(false);
    expect(navigate).toHaveBeenCalledWith(['/login'], expect.anything());
  });

  it('keeps the session when the refresh fails transiently (network / 5xx)', async () => {
    signIn();
    let error: unknown;
    http.get('/api/orders').subscribe({ error: (e) => (error = e) });
    httpMock.expectOne('/api/orders').flush(null, unauthorized);
    await settle();
    expectRefresh('refresh-1').flush({ title: 'Down' }, { status: 503, statusText: 'Service Unavailable' });
    await settle();

    expect(error).toBeInstanceOf(HttpErrorResponse);
    expect((error as HttpErrorResponse).status).toBe(503);
    expect(session.isAuthenticated()).toBe(true);
    expect(session.refreshToken()).toBe('refresh-1');
  });

  it('never refreshes for anonymous auth endpoints', async () => {
    signIn();
    let failed = false;
    http.post('/api/auth/login', {}).subscribe({ error: () => (failed = true) });
    httpMock.expectOne('/api/auth/login').flush(null, unauthorized);
    await settle();
    httpMock.expectNone('/api/auth/refresh');
    expect(failed).toBe(true);
    expect(session.isAuthenticated()).toBe(true);
  });

  it('signs out a stored session without refresh token on 401 (pre-refresh sessions)', () => {
    session.setToken({ ...tokens('legacy', 'unused'), refreshToken: null as unknown as string });
    expect(session.refreshToken()).toBeNull();
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    http.get('/api/orders').subscribe({ error: () => undefined });
    httpMock.expectOne('/api/orders').flush(null, unauthorized);

    httpMock.expectNone('/api/auth/refresh');
    expect(session.isAuthenticated()).toBe(false);
    expect(navigate).toHaveBeenCalledWith(['/login'], expect.objectContaining({ queryParams: expect.objectContaining({ expired: 1 }) }));
  });
});
