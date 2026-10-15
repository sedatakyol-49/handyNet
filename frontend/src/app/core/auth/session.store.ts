import { Injectable, computed, inject, signal } from '@angular/core';
import { StorageService } from '../storage.service';
import { Me, Membership, ModuleName, Permission, TokenResponse } from '../api/models';

const TOKEN_KEY = 'hn.auth';
const ORG_KEY = 'hn.org';

export interface StoredToken {
  accessToken: string;
  expiresAt: string;
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

  readonly token = computed(() => this.tokenState()?.accessToken ?? null);
  readonly isAuthenticated = computed(() => this.token() !== null);
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
    const value: StoredToken = { accessToken: response.accessToken, expiresAt: response.expiresAt };
    this.tokenState.set(value);
    this.storage.setJson(TOKEN_KEY, value);
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
    const stored = this.storage.getJson<StoredToken>(TOKEN_KEY);
    if (!stored?.accessToken) return null;
    if (stored.expiresAt && Date.parse(stored.expiresAt) <= Date.now()) {
      this.storage.set(TOKEN_KEY, null);
      return null;
    }
    return stored;
  }
}
