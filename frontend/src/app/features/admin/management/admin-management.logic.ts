import {
  AdminMember,
  AdminOrganization,
  AdminOrganizationQuery,
  AdminUserAction,
  AdminUserDetail,
  AdminUserQuery,
  MemberRole,
  OrganizationProfileInput,
} from '../../../core/api/models';

/** Same limit as the API (AdminReason.MaxLength). */
export const MAX_REASON_LENGTH = 1000;

export const MEMBER_ROLES: MemberRole[] = ['Owner', 'Manager', 'Staff'];
export const CAPABILITIES = ['wholesaler', 'retailer', 'repair'] as const;
export const ORG_STATUSES = ['PendingVerification', 'Verified', 'Rejected', 'Suspended'] as const;

// ------------------------------------------------------------------------------------------- companies: list

export interface CompanyFilters {
  status: string;
  search: string;
  module: string;
  capability: string;
  /** yyyy-MM-dd (local), inclusive. */
  createdFrom: string;
  /** yyyy-MM-dd (local), inclusive. */
  createdTo: string;
}

export const EMPTY_COMPANY_FILTERS: CompanyFilters = {
  status: '',
  search: '',
  module: '',
  capability: '',
  createdFrom: '',
  createdTo: '',
};

/** Local calendar date (yyyy-MM-dd) plus `days`, as the ISO instant of its start in the browser's time zone. */
export function localDayStart(date: string, days = 0): string | undefined {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(date);
  if (!m) return undefined;
  return new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3]) + days).toISOString();
}

/** API query: the "to" date is inclusive in the form and exclusive (start of the next day) in the API. */
export function companyQuery(
  f: CompanyFilters,
  page: number,
  pageSize: number,
): AdminOrganizationQuery {
  return {
    status: f.status || undefined,
    search: f.search.trim() || undefined,
    module: f.module || undefined,
    capability: f.capability || undefined,
    createdFrom: localDayStart(f.createdFrom),
    createdTo: localDayStart(f.createdTo, 1),
    page,
    pageSize,
  };
}

/** i18n key of a date-range problem, or null. */
export function validateDateRange(from: string, to: string): string | null {
  return from && to && from > to ? 'adminMgmt.companies.rangeInvalid' : null;
}

// ------------------------------------------------------------------------------------------- companies: master data

export type ProfileField = keyof OrganizationProfileInput | 'capabilities';

export function profileOf(o: AdminOrganization): OrganizationProfileInput {
  return {
    name: o.name,
    legalName: o.legalName,
    vatId: o.vatId,
    street: o.street,
    postalCode: o.postalCode,
    city: o.city,
    countryCode: o.countryCode,
    isWholesaler: o.isWholesaler,
    isRetailer: o.isRetailer,
    isRepairProvider: o.isRepairProvider,
    taxNumber: o.taxNumber,
    contactEmail: o.contactEmail,
    contactPhone: o.contactPhone,
    website: o.website,
  };
}

const optional = (v: string | null | undefined): string | null => (v && v.trim() ? v.trim() : null);

/** Trimmed copy; empty optional fields become null, the country code upper case. */
export function normalizeProfile(p: OrganizationProfileInput): OrganizationProfileInput {
  return {
    ...p,
    name: p.name.trim(),
    legalName: p.legalName.trim(),
    vatId: optional(p.vatId),
    street: p.street.trim(),
    postalCode: p.postalCode.trim(),
    city: p.city.trim(),
    countryCode: (p.countryCode || 'DE').trim().toUpperCase(),
    taxNumber: optional(p.taxNumber),
    contactEmail: optional(p.contactEmail),
    contactPhone: optional(p.contactPhone),
    website: optional(p.website),
  };
}

export function profileChanged(a: OrganizationProfileInput, b: OrganizationProfileInput): boolean {
  return JSON.stringify(normalizeProfile(a)) !== JSON.stringify(normalizeProfile(b));
}

/** German Steuernummer: 10–13 digits, separators / or space (mirrors OrganizationContactRules.IsTaxNumber). */
export function isTaxNumber(value: string): boolean {
  const v = value.trim();
  if (v.length > 30 || !/^[\d/ ]+$/.test(v) || !/^\d/.test(v) || !/\d$/.test(v)) return false;
  const digits = v.replace(/\D/g, '').length;
  return digits >= 10 && digits <= 13;
}

/** Absolute http(s) URL with a dotted host (mirrors OrganizationContactRules.IsWebsite). */
export function isWebsite(value: string): boolean {
  const v = value.trim();
  if (/\s/.test(v)) return false;
  try {
    const url = new URL(v);
    return (url.protocol === 'https:' || url.protocol === 'http:') && url.hostname.includes('.');
  } catch {
    return false;
  }
}

/**
 * Client-side checks with the same rules as the registration (server validates again). Returns i18n keys per field.
 */
export function validateProfile(
  input: OrganizationProfileInput,
): Partial<Record<ProfileField, string>> {
  const p = normalizeProfile(input);
  const errors: Partial<Record<ProfileField, string>> = {};
  const german = p.countryCode === 'DE';
  if (!p.name || p.name.length > 200) errors.name = 'validation.required';
  if (!p.legalName || p.legalName.length > 300) errors.legalName = 'validation.required';
  if (!p.street || p.street.length > 200) errors.street = 'validation.required';
  if (!p.city || p.city.length > 100) errors.city = 'validation.required';
  if (!/^[A-Z]{2}$/.test(p.countryCode)) errors.countryCode = 'adminMgmt.validation.country';
  if (!p.postalCode) errors.postalCode = 'validation.required';
  else if (german && !/^\d{5}$/.test(p.postalCode)) errors.postalCode = 'validation.postalCode';
  else if (p.postalCode.length > 10) errors.postalCode = 'validation.postalCode';
  if (p.vatId && german && !/^DE\s?\d{9}$/.test(p.vatId)) errors.vatId = 'validation.vatId';
  if (p.taxNumber && !isTaxNumber(p.taxNumber)) errors.taxNumber = 'adminMgmt.validation.taxNumber';
  if (
    p.contactEmail &&
    (p.contactEmail.length > 320 || !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(p.contactEmail))
  )
    errors.contactEmail = 'validation.email';
  if (p.contactPhone && !/^\+?[0-9 ()/-]{6,40}$/.test(p.contactPhone))
    errors.contactPhone = 'adminMgmt.validation.phone';
  if (p.website && (p.website.length > 300 || !isWebsite(p.website)))
    errors.website = 'adminMgmt.validation.website';
  if (!p.isWholesaler && !p.isRetailer && !p.isRepairProvider)
    errors.capabilities = 'validation.businessType';
  return errors;
}

// ------------------------------------------------------------------------------------------- companies: members

export function ownerCount(members: AdminMember[]): number {
  return members.filter((m) => m.role === 'Owner').length;
}

/** The sole owner can neither be demoted nor removed (transfer the ownership first). Mirrors MembershipRules. */
export function isLastOwner(members: AdminMember[], member: AdminMember): boolean {
  return member.role === 'Owner' && ownerCount(members) === 1;
}

export function canChangeRole(
  members: AdminMember[],
  member: AdminMember,
  role: MemberRole,
): boolean {
  return role !== member.role && !(isLastOwner(members, member) && role !== 'Owner');
}

/** Members that can become the only owner (everyone except a sole owner). */
export function transferCandidates(members: AdminMember[]): AdminMember[] {
  return members.filter((m) => !isLastOwner(members, m) && !m.isDisabled);
}

// ------------------------------------------------------------------------------------------- reasons

/** i18n key of a reason problem, or null. Destructive actions require a reason. */
export function validateReason(reason: string, required: boolean): string | null {
  const r = reason.trim();
  if (required && !r) return 'adminMgmt.reasonRequired';
  if (r.length > MAX_REASON_LENGTH) return 'adminMgmt.reasonTooLong';
  return null;
}

export const normalizeReason = (reason: string): string | null => reason.trim() || null;

// ------------------------------------------------------------------------------------------- users

export interface UserFilters {
  search: string;
  /** '' | 'true' | 'false' */
  emailVerified: string;
  locked: string;
  disabled: string;
  platformAdmin: string;
  noOrganization: string;
}

export const EMPTY_USER_FILTERS: UserFilters = {
  search: '',
  emailVerified: '',
  locked: '',
  disabled: '',
  platformAdmin: '',
  noOrganization: '',
};

const triState = (v: string): boolean | undefined =>
  v === 'true' ? true : v === 'false' ? false : undefined;

export function userQuery(f: UserFilters, page: number, pageSize: number): AdminUserQuery {
  return {
    search: f.search.trim() || undefined,
    emailVerified: triState(f.emailVerified),
    locked: triState(f.locked),
    disabled: triState(f.disabled),
    platformAdmin: triState(f.platformAdmin),
    noOrganization: triState(f.noOrganization),
    page,
    pageSize,
  };
}

export type BadgeTone = 'neutral' | 'info' | 'success' | 'warning' | 'danger';

export interface UserBadge {
  key: string;
  tone: BadgeTone;
}

/** Status badges of a user row/detail, most important first. */
export function userBadges(u: {
  isDisabled: boolean;
  isLockedOut: boolean;
  isPlatformAdmin: boolean;
  emailVerified: boolean;
}): UserBadge[] {
  const badges: UserBadge[] = [];
  if (u.isDisabled) badges.push({ key: 'disabled', tone: 'danger' });
  if (u.isLockedOut) badges.push({ key: 'locked', tone: 'warning' });
  if (u.isPlatformAdmin) badges.push({ key: 'admin', tone: 'info' });
  if (!u.emailVerified) badges.push({ key: 'unverified', tone: 'neutral' });
  if (!badges.length) badges.push({ key: 'active', tone: 'success' });
  return badges;
}

/** Actions that need a reason (they are destructive or bypass a check). */
export const REASON_REQUIRED: ReadonlySet<AdminUserAction> = new Set<AdminUserAction>([
  'disable',
  'mark-email-verified',
  'revoke-platform-admin',
]);

/** Actions that ask for confirmation in a dialog (all writes; the destructive ones with a danger button). */
export const DANGER_ACTIONS: ReadonlySet<AdminUserAction> = new Set<AdminUserAction>([
  'disable',
  'revoke-sessions',
  'revoke-platform-admin',
]);

/** Actions offered for the account's current state (the API enforces the same rules). */
export function availableUserActions(u: AdminUserDetail): AdminUserAction[] {
  const actions: AdminUserAction[] = [];
  if (u.isDisabled) actions.push('enable');
  else if (!u.isSelf) actions.push('disable');
  if (u.isLockedOut || u.failedLoginCount > 0) actions.push('clear-lockout');
  if (u.activeSessions > 0) actions.push('revoke-sessions');
  if (!u.isDisabled) actions.push('password-reset');
  if (!u.emailVerified && !u.isDisabled) actions.push('resend-verification');
  if (!u.emailVerified) actions.push('mark-email-verified');
  if (!u.isPlatformAdmin && !u.isDisabled) actions.push('grant-platform-admin');
  if (u.isPlatformAdmin && !u.isSelf) actions.push('revoke-platform-admin');
  return actions;
}
