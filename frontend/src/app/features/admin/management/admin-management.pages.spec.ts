import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AdminManagementApi } from '../../../core/api/admin-management-api';
import { AdminOrganizationDetail, AdminUserDetail } from '../../../core/api/models';
import { AdminCompanyDetailPage } from './admin-company-detail.page';
import { AdminUserDetailPage } from './admin-user-detail.page';

const USER: AdminUserDetail = {
  id: 'u1',
  email: 'kasse@shop.de',
  displayName: 'Kassierer',
  emailVerified: true,
  emailVerifiedAt: '2026-10-01T08:00:00Z',
  createdAt: '2026-10-01T08:00:00Z',
  lastLoginAt: '2026-10-20T08:00:00Z',
  failedLoginCount: 0,
  lockedUntil: null,
  isLockedOut: false,
  isDisabled: false,
  disabledAt: null,
  disabledReason: null,
  isPlatformAdmin: false,
  activeSessions: 2,
  verificationEmailRequestedAt: null,
  passwordResetRequestedAt: null,
  memberships: [
    {
      organizationId: 'o1',
      organizationName: 'Handy Shop',
      role: 'Staff',
      organizationStatus: 'Verified',
      joinedAt: '2026-10-01T08:00:00Z',
    },
  ],
  isSelf: false,
};

const COMPANY: AdminOrganizationDetail = {
  organization: {
    id: 'o1',
    name: 'Handy Shop',
    legalName: 'Handy Shop GmbH',
    vatId: null,
    street: 'Weg 1',
    postalCode: '10115',
    city: 'Berlin',
    countryCode: 'DE',
    isWholesaler: false,
    isRetailer: true,
    isRepairProvider: false,
    status: 'Verified',
    statusReason: null,
    createdAt: '2026-10-01T08:00:00Z',
    verifiedAt: '2026-10-02T08:00:00Z',
    modules: ['Pos'],
    taxNumber: null,
    contactEmail: null,
    contactPhone: null,
    website: null,
    memberCount: 2,
    registerCount: 1,
    offerCount: 0,
  },
  version: 42,
  createdBy: { id: 'u0', email: 'chef@shop.de', displayName: 'Chefin' },
  verifiedBy: null,
  statusHistory: [
    {
      from: null,
      to: 'PendingVerification',
      reason: null,
      at: '2026-10-01T08:00:00Z',
      by: { id: 'u0', email: 'chef@shop.de', displayName: 'Chefin' },
    },
    {
      from: 'PendingVerification',
      to: 'Verified',
      reason: null,
      at: '2026-10-02T08:00:00Z',
      by: null,
    },
  ],
  members: [
    {
      userId: 'u0',
      email: 'chef@shop.de',
      displayName: 'Chefin',
      role: 'Owner',
      joinedAt: '2026-10-01T08:00:00Z',
      emailVerified: true,
      isDisabled: false,
    },
    {
      userId: 'u1',
      email: 'kasse@shop.de',
      displayName: 'Kassierer',
      role: 'Staff',
      joinedAt: '2026-10-03T08:00:00Z',
      emailVerified: true,
      isDisabled: false,
    },
  ],
  registers: ['Kasse 1'],
  publishedOfferCount: 0,
};

const settle = async (fixture: { whenStable(): Promise<unknown>; detectChanges(): void }) => {
  await fixture.whenStable();
  fixture.detectChanges();
};

describe('admin user detail page', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  async function render(user: AdminUserDetail) {
    const fixture = TestBed.createComponent(AdminUserDetailPage);
    fixture.componentRef.setInput('id', user.id);
    fixture.detectChanges();
    await fixture.whenStable();
    http.expectOne(`/api/admin/users/${user.id}`).flush(user);
    await settle(fixture);
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  it('shows the account and only the actions that fit its state', async () => {
    const { el } = await render(USER);
    expect(el.querySelector('[data-testid="user-sessions"]')?.textContent?.trim()).toBe('2');
    expect(el.querySelector('[data-testid="user-memberships"]')?.textContent).toContain(
      'Handy Shop',
    );
    const actions = [...el.querySelectorAll('[data-testid^="user-action-"]')].map((b) =>
      b.getAttribute('data-testid'),
    );
    expect(actions).toEqual([
      'user-action-disable',
      'user-action-revoke-sessions',
      'user-action-password-reset',
      'user-action-grant-platform-admin',
    ]);
  });

  it('locks the account only with a reason and shows the result', async () => {
    const { fixture, el } = await render(USER);
    (el.querySelector('[data-testid="user-action-disable"]') as HTMLButtonElement).click();
    await settle(fixture);
    const confirm = () =>
      (el.querySelector('[data-testid="user-confirm"]') as HTMLButtonElement).click();

    confirm(); // no reason: refused on the client, no request
    await settle(fixture);
    expect(el.querySelector('#user-reason-error')?.textContent).toContain('Begründung');
    http.expectNone('/api/admin/users/u1/disable');

    const reason = el.querySelector('[data-testid="user-reason"]') as HTMLTextAreaElement;
    reason.value = 'Missbrauchsverdacht';
    reason.dispatchEvent(new Event('input'));
    confirm();
    await settle(fixture);
    const req = http.expectOne('/api/admin/users/u1/disable');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ reason: 'Missbrauchsverdacht' });
    req.flush({
      ...USER,
      isDisabled: true,
      activeSessions: 0,
      disabledReason: 'Missbrauchsverdacht',
      disabledAt: '2026-10-21T08:00:00Z',
    });
    await settle(fixture);
    expect(el.querySelector('[data-testid="user-disabled-reason"]')?.textContent).toContain(
      'Missbrauchsverdacht',
    );
    expect(el.querySelector('[data-testid="user-action-enable"]')).not.toBeNull();
    expect(el.querySelector('[data-testid="user-action-disable"]')).toBeNull();
  });

  it('never offers self-disable or self-revoke', async () => {
    const { el } = await render({ ...USER, isSelf: true, isPlatformAdmin: true });
    expect(el.querySelector('[data-testid="user-action-disable"]')).toBeNull();
    expect(el.querySelector('[data-testid="user-action-revoke-platform-admin"]')).toBeNull();
  });
});

describe('admin company detail page', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  async function render() {
    const fixture = TestBed.createComponent(AdminCompanyDetailPage);
    fixture.componentRef.setInput('id', 'o1');
    fixture.detectChanges();
    await fixture.whenStable();
    http.expectOne('/api/admin/organizations/o1').flush(COMPANY);
    http.expectOne('/api/admin/modules').flush(['Pos', 'Inventory']);
    await settle(fixture);
    fixture.detectChanges();
    await settle(fixture);
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  it('shows master data, history, registers and protects the only owner', async () => {
    const { el } = await render();
    expect((el.querySelector('[data-testid="cf-name"]') as HTMLInputElement).value).toBe(
      'Handy Shop',
    );
    expect(el.querySelector('[data-testid="company-registers"]')?.textContent).toContain('Kasse 1');
    expect(el.querySelectorAll('[data-testid="company-history"] li').length).toBe(2);
    expect(el.querySelector('[data-testid="company-scope-note"]')).not.toBeNull();
    const ownerRow = el.querySelector('tr[data-member="chef@shop.de"]')!;
    expect(
      (ownerRow.querySelector('[data-testid="member-remove-open"]') as HTMLButtonElement).disabled,
    ).toBe(true);
    expect(ownerRow.textContent).toContain('Einziger Inhaber');
  });

  it('validates and saves master data with the loaded version', async () => {
    const { fixture, el } = await render();
    const save = el.querySelector('[data-testid="cf-save"]') as HTMLButtonElement;
    expect(save.disabled).toBe(true); // nothing changed yet

    const zip = el.querySelector('[data-testid="cf-zip"]') as HTMLInputElement;
    zip.value = '123';
    zip.dispatchEvent(new Event('input'));
    await settle(fixture);
    (el.querySelector('[data-testid="company-form"]') as HTMLFormElement).dispatchEvent(
      new Event('submit'),
    );
    await settle(fixture);
    expect(el.querySelector('#cf-zip-error')?.textContent).toContain('fünf Ziffern');
    http.expectNone('/api/admin/organizations/o1');

    zip.value = '20095';
    zip.dispatchEvent(new Event('input'));
    const city = el.querySelector('[data-testid="cf-city"]') as HTMLInputElement;
    city.value = 'Hamburg';
    city.dispatchEvent(new Event('input'));
    await settle(fixture);
    (el.querySelector('[data-testid="company-form"]') as HTMLFormElement).dispatchEvent(
      new Event('submit'),
    );
    await settle(fixture);
    const req = http.expectOne(
      (r) => r.method === 'PUT' && r.url === '/api/admin/organizations/o1',
    );
    expect(req.request.body.version).toBe(42);
    expect(req.request.body.organization).toMatchObject({
      postalCode: '20095',
      city: 'Hamburg',
      vatId: null,
      countryCode: 'DE',
    });
    req.flush({
      ...COMPANY,
      version: 43,
      organization: { ...COMPANY.organization, postalCode: '20095', city: 'Hamburg' },
    });
    await settle(fixture);
    expect((el.querySelector('[data-testid="cf-city"]') as HTMLInputElement).value).toBe('Hamburg');
  });

  it('changes a member role after confirmation', async () => {
    const { fixture, el } = await render();
    const row = el.querySelector('tr[data-member="kasse@shop.de"]')!;
    const select = row.querySelector('[data-testid="member-role-select"]') as HTMLSelectElement;
    select.value = 'Manager';
    select.dispatchEvent(new Event('change'));
    await settle(fixture);
    (row.querySelector('[data-testid="member-role-apply"]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(el.querySelector('[data-testid="company-dialog-text"]')?.textContent).toContain(
      'Manager',
    );
    (el.querySelector('[data-testid="company-confirm"]') as HTMLButtonElement).click();
    await settle(fixture);
    const req = http.expectOne('/api/admin/organizations/o1/members/u1/role');
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ role: 'Manager', reason: null });
    req.flush({
      ...COMPANY,
      members: COMPANY.members.map((m) =>
        m.userId === 'u1' ? { ...m, role: 'Manager' as const } : m,
      ),
    });
    await settle(fixture);
  });
});

describe('admin management API', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('sends filters as query parameters and email actions with their language', async () => {
    const api = TestBed.inject(AdminManagementApi);
    const list = firstValueFrom(
      api.userList({
        search: 'a@b.de',
        disabled: true,
        noOrganization: false,
        page: 1,
        pageSize: 25,
      }),
    );
    const req = http.expectOne((r) => r.url === '/api/admin/users');
    expect(req.request.params.get('disabled')).toBe('true');
    expect(req.request.params.get('noOrganization')).toBe('false');
    expect(req.request.params.has('locked')).toBe(false);
    req.flush({ items: [], page: 1, pageSize: 25, totalCount: 0 });
    await list;

    const reset = firstValueFrom(api.userAction('u1', 'password-reset', null, 'en'));
    const post = http.expectOne('/api/admin/users/u1/password-reset');
    expect(post.request.body).toEqual({ reason: null, language: 'en' });
    post.flush(USER);
    await reset;

    const companies = firstValueFrom(
      api.companies({
        module: 'Pos',
        capability: 'retailer',
        createdFrom: '2026-10-01T00:00:00.000Z',
      }),
    );
    const get = http.expectOne((r) => r.url === '/api/admin/organizations');
    expect(get.request.params.get('module')).toBe('Pos');
    expect(get.request.params.get('createdFrom')).toBe('2026-10-01T00:00:00.000Z');
    get.flush({ items: [], page: 1, pageSize: 25, totalCount: 0 });
    await companies;
  });
});
