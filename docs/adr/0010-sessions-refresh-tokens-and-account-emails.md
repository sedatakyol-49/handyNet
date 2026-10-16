# ADR-0010: Sessions with rotating refresh tokens; password reset and email verification

- Status: Accepted
- Date: 2026-10-17

## Context

Phase 1 issued only a 60-minute JWT. There was no way to end a session (logout, stolen device, password change), no
password reset and no proof that a user controls their email address. Notifications already turns outbox events into
queued emails, but Notifications depends on Identity, so Identity cannot call it.

## Decision

1. **Refresh tokens are opaque, not JWTs.** 256 random bits (base64url, 43 chars), stored only as SHA-256 hex in
   `identity.refresh_tokens`. A fast hash is enough because the input has full entropy.
2. **Rotation on every use, with reuse detection per family.** Login starts a *family* (= one sign-in on one
   device). `POST /api/auth/refresh` marks the presented token used, links it to its successor (`replaced_by_id`) and
   returns a new pair. Presenting an already used token means a copy exists: the whole family is revoked
   (`revocation_reason = Reuse`), so thief and victim must sign in again. Other sessions of the user stay valid.
   Each rotation starts a fresh lifetime (`Jwt:RefreshTokenDays`, default 14, sliding).
3. **Concurrency is decided by the database.** The token row's `xmin` is the concurrency token, so two parallel
   refreshes with the same token cannot both rotate it; the loser gets `auth.refresh_token_invalid` (not treated as
   theft). `replaced_by_id` is unique and check constraints keep `used_at`/`replaced_by_id` and
   `revoked_at`/`revocation_reason` consistent. The web app should serialize refreshes (one in-flight refresh shared
   by all callers and tabs).
4. **Revocation.** Logout revokes the family of the presented token (idempotent; unknown tokens give 204). Logout-all,
   password change and password reset revoke every live token of the user in the same transaction as the change.
   Rows are never deleted (no DELETE grant): they are the history reuse detection needs.
5. **Access tokens stay at 60 minutes for now.** The web app does not refresh yet; a shorter lifetime would log users
   out every 15 minutes. Target is 15 minutes once the frontend refreshes (config only, `Jwt:AccessTokenMinutes`).
   Until then a revoked session's *access* token keeps working until it expires — revocation is immediate only for
   refresh tokens.
6. **Account tokens** (`identity.account_tokens`, purpose `EmailVerification` | `PasswordReset`): same opaque
   format, stored hashed, single use (`used_at`, `xmin` guard against a double submit), short-lived
   (`Identity:PasswordResetTokenMinutes` 60, `Identity:EmailVerificationTokenHours` 48). Issuing a new one revokes the
   user's older unused ones of the same purpose. Unknown/used/superseded → `auth.token_invalid`, expired →
   `auth.token_expired`.
7. **No plaintext token in the outbox.** Identity raises `EmailVerificationRequested` / `PasswordResetRequested`
   (contracts) carrying only user id and language. The Notifications handler calls
   `IUserAccounts.Issue…TokenAsync`, which stages the hashed token row in the shared unit of work; the dispatcher
   commits token and email together. Re-delivery of the event is detected via the email's `source_id` before a new
   token is issued. The plaintext exists only in the email; its body is replaced with a placeholder once delivered
   (or finally failed) — `notifications.email_messages` keeps no usable link.
8. **No account enumeration.** `POST /api/auth/password-reset/request` answers `202` with no body for any
   syntactically valid address — existing, unknown, inactive or within the 1-minute per-user cooldown. Per-IP rate
   limits: `RateLimits:AccountEmailsPerHour` (reset request, verification resend), `RateLimits:RefreshPerMinute`
   (refresh, logout), `RateLimits:AuthPerMinute` (login, registration, reset confirm, verify). Residual risk: the
   existing-account path writes one row, so timing differs by a few milliseconds.
9. **Unverified users can sign in and use the app.** Registration sends a verification email; `emailVerified` is
   exposed in the token response and `GET /api/me`. Gating features on it (e.g. before payouts) is a later product
   decision. A successful password reset also verifies the address (the link proved control of the mailbox). The
   bootstrap platform admin is treated as verified. Users created before this change start unverified and can use
   "resend".
10. **Links** are `{Notifications:PublicBaseUrl}/email-bestaetigen#token=…` and `…/passwort-zuruecksetzen#token=…`.
    The token is in the fragment, so it never reaches server/proxy logs or `Referer` headers.
11. **Emails** in German (default) and English, chosen by an optional `language` (`de`|`en`) on registration, reset
    request and resend. There is no stored per-user language yet.

## Consequences

- The web app must store the refresh token, call `/api/auth/refresh` before/after access-token expiry (single
  in-flight call), and call `/api/auth/logout` on sign-out. It needs two public routes for the links.
- `identity.refresh_tokens` grows with every refresh; a retention job that archives/purges long-expired rows needs a
  dedicated, audited path (the app role has no DELETE).
- Error kind `Unauthorized` (HTTP 401) was added to the shared `Error` model for invalid refresh tokens.
