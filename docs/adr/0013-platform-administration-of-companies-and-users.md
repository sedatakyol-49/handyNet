# ADR-0013: Platform administration of companies and user accounts

- Status: Accepted
- Date: 2026-10-21

## Context

The platform operator (SEHA) could verify, reject and suspend organizations and switch modules, but could not see a
company's members, contact data or history, nor manage user accounts (a stolen device, a former employee, a locked-out
shop owner, a typo in a name). The product owner asked for it; we agreed on the scope: platform admins see and manage
**company master data and user accounts** (contract and account data, which the operator legitimately holds), but they
do **not** browse tenants' operational data — POS customers, receipts, sales, Ankauf seller ID data, prices.

## Decision

1. **Scope.** `/api/admin/organizations` (Tenancy) and `/api/admin/users` (Identity), platform admin only
   (`RequirePlatformAdmin`), called without `X-Organization-Id`; a request that carries a tenant context is refused with
   `admin.tenant_header` (422), as in the marketplace moderation. Exposed: master data, status and its history, modules,
   members with role and account status, register *names*, and counts (members, registers, offers / published offers).
   Never exposed: password hashes, refresh/account tokens, and any operational data.
2. **Counts of tenant-owned data through a port.** Tenancy defines `IOrganizationFactSource` (contracts); POS and
   Marketplace implement it with id-only `SECURITY DEFINER` functions (`pos.admin_register_names(uuid[])`,
   `marketplace.admin_offer_counts(uuid[])`) that refuse to run for a tenant session and return names/counts only. RLS on
   the tenant tables is unchanged; Tenancy does not depend on POS or Marketplace.
3. **User administration lives in Identity**, which owns the account. Memberships come through a port
   `IUserMembershipSource` (Identity contracts, implemented by Tenancy), including a composable query for the filter
   "users without a company" (one SQL statement in the shared unit of work, ADR-0002).
4. **Account actions** (domain methods on `User`, returning `Result`): disable/enable (disable needs a reason, refuses the
   own account and revokes every refresh token in the same transaction, reason `AccountDisabled`), clear lockout, end all
   sessions (`AdminRevoked`), send a password reset link or a verification link (same cooldown as the user's own requests,
   errors reported to the admin — the admin is trusted, so no enumeration concern), mark the email as verified (reason
   required), change the display name, grant/revoke platform admin (no self-revoke; the last active platform admin stays).
5. **Immediate effect for access tokens.** `OnTokenValidated` (`AccountStatusCheck`) rejects an access token of a
   disabled account, and a token with the `hn_admin` claim whose role was revoked, with 401 — one primary-key lookup per
   authenticated request. The web app then refreshes, which fails for a disabled account and issues a token without the
   admin claim otherwise. (Logout and "end all sessions" still end only refresh tokens; an access token of an *enabled*
   account runs out within `Jwt:AccessTokenMinutes`.)
6. **Invariants in the database, not only in code.**
   - Every organization keeps at least one owner: deferred constraint trigger `owner_required` on `tenancy.memberships`
     (raises `ck_memberships_owner_required`); an advisory lock per organization serialises concurrent demotions. The
     transfer of ownership (new owner up, previous owners down to manager) commits in one transaction.
   - At least one active platform admin remains: deferred trigger `platform_admin_required` on `identity.users`
     (`ck_users_last_platform_admin`), with an advisory lock against two admins demoting each other at the same time.
   - Removing a membership: the app role still has no `DELETE` on memberships; `tenancy.admin_remove_membership` (SECURITY
     DEFINER) refuses tenant sessions and callers that are not an active platform admin, requires a reason and writes the
     audit entries in the same statement. The user account stays.
7. **Status history.** `tenancy.organization_status_changes` (from, to, reason, changed_by, changed_at) is written by the
   `Organization` aggregate on registration, verification, rejection, suspension and reactivation; append-only (no
   UPDATE/DELETE grant). The migration backfills it from `infra.audit_log` (enum values stored as numbers or strings).
   Reactivation (`Suspended → Verified`) is its own action and sends no "verified" email.
8. **Master data.** Name, legal name, address, VAT ID, business types plus the new optional `tax_number`, `contact_email`,
   `contact_phone`, `website`, validated with the registration validator (and extra rules for the new fields) and
   optimistic concurrency (`version` = `xmin` sent back by the form).
9. **Audit and access log.** Every admin write adds an explicit entry to `infra.audit_log` (`action` = `Admin…`,
   `changes` = `{Source: "platform", Reason, Before, After}`; before/after values for master data and roles), next to the
   automatic entity entries. Opening a company or user detail writes an access-log entry (`action` = `AdminRead`, the
   view name, no values). Both are append-only like the rest of the audit log.

## Consequences

- The admin UI gets "Unternehmen" (list with filters → detail) and "Benutzer" (list → detail) tabs; the old
  verification list is the company list (same quick actions).
- Every authenticated request costs one indexed lookup of `identity.users` (acceptable; can be cached for a few seconds
  later if needed).
- Bootstrap still re-promotes `Bootstrap:AdminEmail` at startup (operator recovery path if every other admin was
  removed); revoking the bootstrap admin's role therefore lasts only until the next restart unless the setting is changed.
- Out of scope, still: browsing receipts, customers, sales or Ankauf seller data of a tenant; impersonation ("log in as");
  deleting accounts or companies (GDPR erasure needs its own audited process).
