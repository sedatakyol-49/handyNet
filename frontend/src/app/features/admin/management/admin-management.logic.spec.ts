import { AdminMember, AdminUserDetail, OrganizationProfileInput } from '../../../core/api/models';
import {
  EMPTY_COMPANY_FILTERS,
  EMPTY_USER_FILTERS,
  MAX_REASON_LENGTH,
  availableUserActions,
  canChangeRole,
  companyQuery,
  isLastOwner,
  isTaxNumber,
  isWebsite,
  localDayStart,
  normalizeProfile,
  profileChanged,
  transferCandidates,
  userBadges,
  userQuery,
  validateDateRange,
  validateProfile,
  validateReason,
} from './admin-management.logic';

const profile = (overrides: Partial<OrganizationProfileInput> = {}): OrganizationProfileInput => ({
  name: 'Shop',
  legalName: 'Shop GmbH',
  vatId: null,
  street: 'Weg 1',
  postalCode: '10115',
  city: 'Berlin',
  countryCode: 'DE',
  isWholesaler: false,
  isRetailer: true,
  isRepairProvider: false,
  taxNumber: null,
  contactEmail: null,
  contactPhone: null,
  website: null,
  ...overrides,
});

const member = (userId: string, role: AdminMember['role'], isDisabled = false): AdminMember =>
  ({
    userId,
    role,
    isDisabled,
    email: `${userId}@x.de`,
    displayName: userId,
    joinedAt: '',
    emailVerified: true,
  }) as AdminMember;

const user = (overrides: Partial<AdminUserDetail> = {}): AdminUserDetail =>
  ({
    id: 'u',
    email: 'u@x.de',
    displayName: 'U',
    emailVerified: true,
    isLockedOut: false,
    failedLoginCount: 0,
    isDisabled: false,
    isPlatformAdmin: false,
    activeSessions: 0,
    isSelf: false,
    memberships: [],
    ...overrides,
  }) as AdminUserDetail;

describe('company query', () => {
  it('drops empty filters and turns the inclusive local "to" day into the exclusive start of the next day', () => {
    expect(companyQuery(EMPTY_COMPANY_FILTERS, 1, 25)).toEqual({
      status: undefined,
      search: undefined,
      module: undefined,
      capability: undefined,
      createdFrom: undefined,
      createdTo: undefined,
      page: 1,
      pageSize: 25,
    });
    const q = companyQuery(
      {
        ...EMPTY_COMPANY_FILTERS,
        search: '  Handy ',
        module: 'Pos',
        capability: 'retailer',
        createdFrom: '2026-10-01',
        createdTo: '2026-10-31',
      },
      2,
      25,
    );
    expect(q.search).toBe('Handy');
    expect(q.createdFrom).toBe(new Date(2026, 9, 1).toISOString());
    expect(q.createdTo).toBe(new Date(2026, 10, 1).toISOString());
    expect(q.page).toBe(2);
  });

  it('rejects malformed dates and reversed ranges', () => {
    expect(localDayStart('1.10.2026')).toBeUndefined();
    expect(validateDateRange('2026-10-31', '2026-10-01')).toBe('adminMgmt.companies.rangeInvalid');
    expect(validateDateRange('2026-10-01', '2026-10-01')).toBeNull();
    expect(validateDateRange('', '2026-10-01')).toBeNull();
  });
});

describe('master data validation', () => {
  it('accepts a valid profile and applies the registration rules', () => {
    expect(
      validateProfile(
        profile({
          vatId: 'DE123456789',
          website: 'https://shop.de',
          taxNumber: '21/815/08150',
          contactEmail: 'a@b.de',
          contactPhone: '+49 30 1234',
        }),
      ),
    ).toEqual({});
    const errors = validateProfile(
      profile({ name: ' ', postalCode: '1234', vatId: 'DE12', isRetailer: false }),
    );
    expect(errors.name).toBe('validation.required');
    expect(errors.postalCode).toBe('validation.postalCode');
    expect(errors.vatId).toBe('validation.vatId');
    expect(errors.capabilities).toBe('validation.businessType');
  });

  it('only checks German formats for German addresses', () => {
    expect(
      validateProfile(profile({ countryCode: 'AT', postalCode: '1010', vatId: 'ATU12345678' })),
    ).toEqual({});
    expect(validateProfile(profile({ countryCode: 'Deutschland' })).countryCode).toBe(
      'adminMgmt.validation.country',
    );
  });

  it('checks the optional contact fields', () => {
    const errors = validateProfile(
      profile({
        website: 'www.shop.de',
        taxNumber: '12/34',
        contactEmail: 'kein-at',
        contactPhone: 'abc',
      }),
    );
    expect(errors.website).toBe('adminMgmt.validation.website');
    expect(errors.taxNumber).toBe('adminMgmt.validation.taxNumber');
    expect(errors.contactEmail).toBe('validation.email');
    expect(errors.contactPhone).toBe('adminMgmt.validation.phone');
  });

  it.each([
    ['21/815/08150', true],
    ['2181508150', true],
    ['1121081508150', true],
    ['12345678901234', false],
    ['/2181508150', false],
    ['21-815-08150', false],
  ])('tax number %s → %s', (value, valid) => expect(isTaxNumber(value)).toBe(valid));

  it.each([
    ['https://www.example.de', true],
    ['http://shop.example.de/kontakt', true],
    ['www.example.de', false],
    ['ftp://example.de', false],
    ['https://localhost', false],
  ])('website %s → %s', (value, valid) => expect(isWebsite(value)).toBe(valid));

  it('normalizes and detects changes', () => {
    expect(
      normalizeProfile(profile({ vatId: ' ', countryCode: 'de', website: ' https://a.de ' })),
    ).toMatchObject({ vatId: null, countryCode: 'DE', website: 'https://a.de' });
    expect(profileChanged(profile(), profile({ name: ' Shop ', contactEmail: '' }))).toBe(false);
    expect(profileChanged(profile(), profile({ city: 'Hamburg' }))).toBe(true);
  });
});

describe('members', () => {
  const owner = member('o', 'Owner');
  const staff = member('s', 'Staff');
  const disabled = member('d', 'Manager', true);
  const team = [owner, staff, disabled];

  it('protects the last owner', () => {
    expect(isLastOwner(team, owner)).toBe(true);
    expect(canChangeRole(team, owner, 'Manager')).toBe(false);
    expect(canChangeRole(team, owner, 'Owner')).toBe(false); // unchanged
    expect(canChangeRole(team, staff, 'Manager')).toBe(true);
    const twoOwners = [owner, member('o2', 'Owner')];
    expect(isLastOwner(twoOwners, owner)).toBe(false);
    expect(canChangeRole(twoOwners, owner, 'Staff')).toBe(true);
  });

  it('offers active non-sole-owner members as new owner', () => {
    expect(transferCandidates(team).map((m) => m.userId)).toEqual(['s']);
  });
});

describe('reasons', () => {
  it('requires a reason for destructive actions and limits the length', () => {
    expect(validateReason('  ', true)).toBe('adminMgmt.reasonRequired');
    expect(validateReason('', false)).toBeNull();
    expect(validateReason('x'.repeat(MAX_REASON_LENGTH + 1), false)).toBe(
      'adminMgmt.reasonTooLong',
    );
    expect(validateReason('Betrug', true)).toBeNull();
  });
});

describe('users', () => {
  it('builds tri-state filters', () => {
    expect(
      userQuery(
        { ...EMPTY_USER_FILTERS, search: ' a@b ', disabled: 'true', emailVerified: 'false' },
        1,
        25,
      ),
    ).toEqual({
      search: 'a@b',
      emailVerified: false,
      locked: undefined,
      disabled: true,
      platformAdmin: undefined,
      noOrganization: undefined,
      page: 1,
      pageSize: 25,
    });
  });

  it('shows status badges, most important first', () => {
    expect(
      userBadges({
        isDisabled: true,
        isLockedOut: true,
        isPlatformAdmin: true,
        emailVerified: false,
      }).map((b) => b.key),
    ).toEqual(['disabled', 'locked', 'admin', 'unverified']);
    expect(
      userBadges({
        isDisabled: false,
        isLockedOut: false,
        isPlatformAdmin: false,
        emailVerified: true,
      }),
    ).toEqual([{ key: 'active', tone: 'success' }]);
  });

  it('offers only actions that fit the account state', () => {
    expect(availableUserActions(user())).toEqual([
      'disable',
      'password-reset',
      'grant-platform-admin',
    ]);
    expect(availableUserActions(user({ isDisabled: true, emailVerified: false }))).toEqual([
      'enable',
      'mark-email-verified',
    ]);
    expect(
      availableUserActions(user({ isLockedOut: true, activeSessions: 2, emailVerified: false })),
    ).toEqual([
      'disable',
      'clear-lockout',
      'revoke-sessions',
      'password-reset',
      'resend-verification',
      'mark-email-verified',
      'grant-platform-admin',
    ]);
    // The own account: no self-disable, no self-revoke.
    expect(
      availableUserActions(user({ isSelf: true, isPlatformAdmin: true, activeSessions: 1 })),
    ).toEqual(['revoke-sessions', 'password-reset']);
    expect(availableUserActions(user({ isPlatformAdmin: true }))).toContain(
      'revoke-platform-admin',
    );
  });
});
