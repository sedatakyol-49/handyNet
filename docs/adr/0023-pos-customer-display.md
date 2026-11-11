# ADR-0023: Customer display — remote link, in-memory state, public long poll

- Status: Accepted (backend implemented and tested; UI built separately — see "Not done / not verified")
- Date: 2026-11-11
- Extends: ADR-0008 (public receipt link pattern)

## Context

Customers should see the cart, totals and change on a second screen. On the same PC the till can drive a second
browser window locally (no backend). A tablet or other device needs a way to receive the state without logging in.
What is shown is transient: it must not create business records or audit noise, and it must not leak anything.

## Decision

- **Link**: `POST /api/pos/registers/{id}/display-link` (`pos.sell`) returns `{ token, path: "/kundendisplay/<token>",
  expiresAt }` once; 256-bit token, only its SHA-256 stored in `pos.display_links` (RLS, never deleted, only the one-time
  revocation may change a row — trigger). Creating a link revokes the register's earlier ones (advisory lock + partial
  unique index: at most one active link per register). `GET …/display-link` → `{ active, expiresAt }` or 204;
  `DELETE …/display-link` revokes. Lifetime `Pos:DisplayLinkDays` (30).
- **State**: `PUT /api/pos/registers/{id}/display-state` (`pos.sell`), body ≤ 16 KiB (read with a hard limit,
  `pos.display_state_too_large`), validated shape (`pos.display_state_invalid` with `field`): phase idle/cart/payment/done,
  lines (description, quantity, unit price, discount, line gross), totals, payment (due, tendered, change, method),
  loyalty (points, redeem value), message, receipt URL (http(s) or a path, no `javascript:`), currency, locale.
  Unknown fields are dropped on deserialization, so a customer block sent by mistake is never kept or shown. It is kept
  **in memory** of the API instance per register (`DisplayStateStore`, singleton), with a version that only grows (seeded
  with the start time). No database write, no audit entry. Lost on restart: displays show idle until the next push.
- **Public read**: `GET /api/public/pos-display/{token}?since=<version>` (anonymous, `Cache-Control: no-store`,
  rate-limited per IP `RateLimits:PublicDisplayPerMinute`). The token is resolved through the SECURITY DEFINER function
  `pos.public_display(token_hash)`, which returns only register, organization, organization name and expiry of one
  active link. Response: `{ version, shop: { name, logoUrl }, state }` — nothing else (no ids, no customer data). With
  `since` equal to the current version the request waits up to `Pos:DisplayPollSeconds` (20) and answers 204 if nothing
  changed. Unknown, malformed, expired and revoked tokens get the **same 404** (`pos.display_link_not_found`, compared
  byte for byte in the tests), also when the link is revoked while a request waits.

## Consequences

- Works only with one API instance, or with sticky routing of the push and poll endpoints to the same instance
  (operations.md). A shared store (Redis, PostgreSQL LISTEN/NOTIFY) would remove that limit.
- Long polls hold a connection each; the rate limit and the poll timeout bound the load.

## Not done / not verified

- Multi-instance support; WebSockets/SSE; pushing from the server when a sale is booked (the till pushes).
- The link token travels in the URL (QR code): anyone who sees it can watch the display until it is revoked or expires.
  There is no device binding.
- Load behaviour with many displays was not measured.
- The display UI (local BroadcastChannel window and remote page, RTL/dark mode) is frontend work outside this ADR.
