import { DOCUMENT, DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import { StorageService } from '../storage.service';
import { Me, Membership, ModuleName, Permission, TokenResponse } from '../api/models';

export const TOKEN_KEY = 'hn.auth';
const ORG_KEY = 'hn.org';

/**
 * The persisted session (localStorage `hn.auth`, see README "Sessions"). One entry per browser profile, shared by all
 * tabs: a refresh in one tab rotates the pair for every tab (the `storage` event keeps the others in sync).
 */
export interface StoredToken {
  accessToken: string;
  expiresAt: string;
  /** Absent for sessions stored before refresh tokens existed; those end when the access token expires. */
  refreshToken?: string | null;
  refreshExpiresAt?: string | null;
  userId?: string | null;
}

/** Parses and validates the stored session; returns null when it is missing or can no longer be used. */
export function usableToken(stored: StoredToken | null, now = Date.now()): StoredToken | null {
  if (!stored?.accessToken) return null;
  const accessAlive = !stored.expiresAt || Date.parse(stored.expiresAt) > now;
  const refreshAlive = !!stored.refreshToken && (!stored.refreshExpiresAt || Date.parse(stored.refreshExpiresAt) > now);
  return accessAlive || refreshAlive ? stored : null;
}

/**
 * Holds the authentication state as signals. Has no HttpClient dependency so the HTTP interceptor
 * can read it without creating an injection cycle.
 */
@Injectable({ providedIn: 'root' })
export class SessionStore {
  private readonly storage = inject(StorageService);

  private readonly tokenState = signal<StoredToken | null>(this.loadToken());
  readonly me = signal<Me | null>(null);
  readonly selectedOrganizationId = signal<string | null>(this.storage.get(ORG_KEY));
  /** Incremented when another tab ended the shared session (logout there); the shell then leaves the app. */
  readonly signedOutElsewhere = signal(0);

  readonly token = computed(() => this.tokenState()?.accessToken ?? null);
  readonly refreshToken = computed(() => this.tokenState()?.refreshToken ?? null);
  /** Expiry of the access token in epoch ms (null if unknown). */
  readonly accessExpiresAt = computed(() => {
    const at = this.tokenState()?.expiresAt;
    const ms = at ? Date.parse(at) : NaN;
    return Number.isFinite(ms) ? ms : null;
  });
  readonly isAuthenticated = computed(() => this.tokenState() !== null);
  /** User of the stored session (known before /api/me answered; null for sessions stored by older versions). */
  readonly userId = computed(() => this.tokenState()?.userId ?? null);
  readonly isPlatformAdmin = computed(() => this.me()?.isPlatformAdmin ?? false);
  readonly memberships = computed(() => this.me()?.memberships ?? []);

  /** The membership the user currently acts for (falls back to the first one). */
  readonly membership = computed<Membership | null>(() => {
    const list = this.memberships();
    return list.find((m) => m.organizationId === this.selectedOrganizationId()) ?? list[0] ?? null;
  });
  readonly organizationId = computed(() => this.membership()?.organizationId ?? null);
  readonly isVerified = computed(() => this.membership()?.status === 'Verified');
  private readonly modules = computed(() => new Set(this.membership()?.modules ?? []));
  private readonly permissions = computed(() => new Set(this.membership()?.permissions ?? []));

  constructor() {
    // Other tabs rotate or clear the shared session; follow them so this tab never presents a used refresh token.
    const win = this.storage.isBrowser ? inject(DOCUMENT).defaultView : null;
    const onStorage = (event: StorageEvent) => {
      if (event.key !== TOKEN_KEY && event.key !== null) return;
      const wasSignedIn = this.tokenState() !== null;
      if (!this.syncFromStorage() && wasSignedIn) this.signedOutElsewhere.update((n) => n + 1);
    };
    win?.addEventListener('storage', onStorage);
    inject(DestroyRef).onDestroy(() => win?.removeEventListener('storage', onStorage));
  }

  hasModule(module: ModuleName): boolean {
    return this.isVerified() && this.modules().has(module);
  }

  can(permission: Permission): boolean {
    return this.permissions().has(permission);
  }

  /** Module enabled (and org verified) and, if given, permission granted. */
  allows(module?: ModuleName, permission?: Permission): boolean {
    if (module && !this.hasModule(module)) return false;
    if (permission && !this.can(permission)) return false;
    return true;
  }

  setToken(response: TokenResponse): void {
    const value: StoredToken = {
      accessToken: response.accessToken,
      expiresAt: response.expiresAt,
      refreshToken: response.refreshToken ?? null,
      refreshExpiresAt: response.refreshExpiresAt ?? null,
      userId: response.userId ?? null,
    };
    this.tokenState.set(value);
    this.storage.setJson(TOKEN_KEY, value);
    const me = this.me();
    if (me && me.userId === response.userId && me.emailVerified !== response.emailVerified) {
      this.me.set({ ...me, emailVerified: response.emailVerified });
    }
  }

  /**
   * Re-reads the shared session from storage (another tab may have rotated or cleared it) and returns it. Falls back
   * to the in-memory session when storage is unavailable.
   */
  syncFromStorage(): StoredToken | null {
    if (!this.storage.isBrowser) return this.tokenState();
    const raw = this.storage.get(TOKEN_KEY);
    if (raw === null && !this.storageWorks()) return this.tokenState();
    const next = usableToken(this.storage.getJson<StoredToken>(TOKEN_KEY));
    const current = this.tokenState();
    if (next?.accessToken !== current?.accessToken || next?.refreshToken !== current?.refreshToken) {
      this.tokenState.set(next);
      if (!next || (current?.userId && next.userId !== current.userId)) this.me.set(null);
    }
    return next;
  }

  setMe(me: Me): void {
    this.me.set(me);
    const selected = this.selectedOrganizationId();
    if (!selected || !me.memberships.some((m) => m.organizationId === selected)) {
      this.selectOrganization(me.memberships[0]?.organizationId ?? null);
    }
  }

  selectOrganization(organizationId: string | null): void {
    this.selectedOrganizationId.set(organizationId);
    this.storage.set(ORG_KEY, organizationId);
  }

  clear(): void {
    this.tokenState.set(null);
    this.me.set(null);
    this.storage.set(TOKEN_KEY, null);
  }

  private loadToken(): StoredToken | null {
    const stored = usableToken(this.storage.getJson<StoredToken>(TOKEN_KEY));
    if (!stored && this.storage.get(TOKEN_KEY) !== null) this.storage.set(TOKEN_KEY, null);
    return stored;
  }

  private storageWorks(): boolean {
    const probe = 'hn.probe';
    this.storage.set(probe, '1');
    const ok = this.storage.get(probe) === '1';
    this.storage.set(probe, null);
    return ok;
  }
}
