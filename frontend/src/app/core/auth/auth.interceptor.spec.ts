import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { authInterceptor, ORGANIZATION_HEADER } from './auth.interceptor';
import { SessionStore } from './session.store';
import { Me } from '../api/models';

const ME: Me = {
  userId: 'u1',
  email: 'owner@example.de',
  displayName: 'Owner',
  isPlatformAdmin: false,
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

describe('authInterceptor', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;
  let session: SessionStore;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
    session = TestBed.inject(SessionStore);
  });

  afterEach(() => httpMock.verify());

  function signIn(): void {
    session.setToken({
      accessToken: 'token-123',
      expiresAt: new Date(Date.now() + 3600_000).toISOString(),
      userId: 'u1',
      email: ME.email,
      displayName: ME.displayName,
      isPlatformAdmin: false,
    });
    session.setMe(ME);
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

  it('clears the session and redirects to login on 401', () => {
    signIn();
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    let failed = false;
    http.get('/api/orders').subscribe({ error: () => (failed = true) });
    httpMock.expectOne('/api/orders').flush({ title: 'Unauthorized' }, { status: 401, statusText: 'Unauthorized' });

    expect(failed).toBe(true);
    expect(session.isAuthenticated()).toBe(false);
    expect(navigate).toHaveBeenCalledWith(['/login'], expect.objectContaining({ queryParams: expect.objectContaining({ expired: 1 }) }));
  });
});
