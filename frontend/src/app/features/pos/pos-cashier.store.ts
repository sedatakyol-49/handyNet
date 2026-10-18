import { HttpErrorResponse, HttpInterceptorFn, HttpResponse } from '@angular/common/http';
import { Injectable, PLATFORM_ID, computed, inject, signal } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { catchError, tap, throwError } from 'rxjs';
import { Membership, PosApprovalGrant, PosCashier, PosCashierSignIn } from '../../core/api/models';
import { SessionStore } from '../../core/auth/session.store';

export const POS_CASHIER_HEADER = 'X-Pos-Cashier';
export const POS_APPROVAL_HEADER = 'X-Pos-Approval';

/** A granted manager approval waiting for the next action on its register. */
export interface PosApproval extends PosApprovalGrant {
  registerId: string;
  organizationId: string;
}
const KEY = 'hn.pos.cashier';

interface Stored {
  organizationId: string;
  /** Cashier sign-in token (bearer for X-Pos-Cashier); null while the till is locked. */
  token: string | null;
  cashier: PosCashier | null;
  /** Register whose till is locked (PIN needed to continue); null = not locked. */
  lockedRegisterId: string | null;
}

/**
 * Cashier switching on a shared device (Odoo-style): the device stays signed in as its user, while the till acts as
 * the cashier who entered their PIN. Kept per browser tab (sessionStorage): a reload keeps the cashier or the lock.
 */
@Injectable({ providedIn: 'root' })
export class PosCashierStore {
  private readonly session = inject(SessionStore);
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));
  private readonly state = signal<Stored | null>(this.read());

  /** State of the current organization only (another organization's cashier never applies). */
  private readonly current = computed(() => {
    const s = this.state();
    return s && s.organizationId === this.session.organizationId() ? s : null;
  });

  readonly cashier = computed(() => (this.current()?.token ? this.current()!.cashier : null));
  readonly token = computed(() => this.current()?.token ?? null);
  readonly lockedRegisterId = computed(() => this.current()?.lockedRegisterId ?? null);

  /**
   * Permissions source for POS rights (`posRights`): the switched-in cashier, otherwise the device user's membership.
   * The API enforces the same (per-cashier rights through the X-Pos-Cashier header).
   */
  readonly membership = computed<Pick<Membership, 'permissions' | 'maxDiscountPercent'> | null>(() => {
    const c = this.cashier();
    const base = c ? { permissions: c.permissions, maxDiscountPercent: c.maxDiscountPercent } : this.session.membership();
    const a = this.approval();
    if (!a || !base) return base;
    // An approval adds the approved rights for the next action; the approver (owner/manager) has no discount limit.
    return {
      permissions: [...new Set([...base.permissions, ...a.permissions])],
      maxDiscountPercent: a.permissions.includes('pos.discount') ? 100 : base.maxDiscountPercent,
    };
  });

  // ---- manager approval for the next action (not persisted; single use, short-lived)
  private readonly approvalState = signal<PosApproval | null>(null);
  readonly approval = computed(() => {
    const a = this.approvalState();
    return a && a.organizationId === this.session.organizationId() ? a : null;
  });
  private approvalTimer: ReturnType<typeof setTimeout> | null = null;

  approved(registerId: string, approval: PosApprovalGrant): void {
    const org = this.session.organizationId();
    if (!org) return;
    this.approvalState.set({ ...approval, registerId, organizationId: org });
    if (this.approvalTimer) clearTimeout(this.approvalTimer);
    const ms = Date.parse(approval.expiresAt) - Date.now();
    if (Number.isFinite(ms)) this.approvalTimer = setTimeout(() => this.clearApproval(), Math.max(ms, 0));
  }

  clearApproval(): void {
    if (this.approvalTimer) clearTimeout(this.approvalTimer);
    this.approvalTimer = null;
    this.approvalState.set(null);
  }

  /** Display name of who the till acts as. */
  readonly actingName = computed(() => this.cashier()?.displayName ?? this.session.me()?.displayName ?? null);

  isLocked(registerId: string | null): boolean {
    return !!registerId && this.lockedRegisterId() === registerId;
  }

  /** Cashier active on another register than the one shown: the API refuses its writes (pos.cashier_register_mismatch). */
  boundElsewhere(registerId: string | null): boolean {
    const c = this.cashier();
    return !!c && !!registerId && c.registerId !== registerId;
  }

  signedIn(result: PosCashierSignIn): void {
    this.clearApproval();
    const org = this.session.organizationId();
    if (!org) return;
    this.write({ organizationId: org, token: result.token, cashier: result.cashier, lockedRegisterId: null });
  }

  /** Locks the till of the register: the PIN pad must be used to continue. */
  lock(registerId: string): void {
    this.clearApproval();
    const org = this.session.organizationId();
    if (!org) return;
    this.write({ organizationId: org, token: null, cashier: null, lockedRegisterId: registerId });
  }

  /** The API refused the token (locked elsewhere, idle, expired): lock the register it was bound to. */
  expired(): void {
    const s = this.current();
    if (!s) return;
    this.write({ ...s, token: null, cashier: null, lockedRegisterId: s.cashier?.registerId ?? s.lockedRegisterId });
  }

  /** Leave cashier mode entirely (back to the device user, no lock). */
  clear(): void {
    this.write(null);
  }

  private read(): Stored | null {
    if (!this.browser) return null;
    try {
      const raw = sessionStorage.getItem(KEY);
      return raw ? (JSON.parse(raw) as Stored) : null;
    } catch {
      return null;
    }
  }

  private write(value: Stored | null): void {
    this.state.set(value);
    if (!this.browser) return;
    try {
      if (value) sessionStorage.setItem(KEY, JSON.stringify(value));
      else sessionStorage.removeItem(KEY);
    } catch {
      /* storage unavailable */
    }
  }
}

/** POS endpoints that must not carry the cashier token (PIN entry and PIN maintenance act as the device user). */
const WITHOUT_CASHIER = /\/api\/pos\/(registers\/[^/]+\/cashier$|me\/pin$|pins$|cashiers\/[^/]+\/pin$)/;

/** Writes an approval can allow (the API uses it once): sale, return, storno, cash movement, closing. */
export const APPROVABLE_WRITE = /\/api\/pos\/(sales$|sales\/[^/]+\/(returns|storno)$|sessions\/[^/]+\/(cash-movements|close)$)/;

/**
 * Adds `X-Pos-Cashier` to POS API calls while a cashier is signed in (a refused token locks the till), and the pending
 * manager approval (`X-Pos-Approval`) to the next approvable write; a successful write used it up.
 */
export const posCashierInterceptor: HttpInterceptorFn = (req, next) => {
  const store = inject(PosCashierStore);
  const token = store.token();
  const path = req.url.split('?')[0];
  if (!path.includes('/api/pos/')) return next(req);
  const headers: Record<string, string> = {};
  if (token && !WITHOUT_CASHIER.test(path) && !req.headers.has(POS_CASHIER_HEADER)) headers[POS_CASHIER_HEADER] = token;
  const approval = req.method === 'POST' && APPROVABLE_WRITE.test(path) ? store.approval() : null;
  if (approval) headers[POS_APPROVAL_HEADER] = approval.token;
  if (!Object.keys(headers).length) return next(req);
  return next(req.clone({ setHeaders: headers })).pipe(
    tap((event) => {
      if (approval && event instanceof HttpResponse && event.ok) store.clearApproval();
    }),
    catchError((e: unknown) => {
      const code = e instanceof HttpErrorResponse && e.status === 403 ? (e.error as { code?: string } | null)?.code : null;
      if (code === 'pos.cashier_invalid' && headers[POS_CASHIER_HEADER]) store.expired();
      if (code === 'pos.approval_invalid' && approval) store.clearApproval();
      return throwError(() => e);
    }),
  );
};
