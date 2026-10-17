import { HttpBackend, HttpClient, HttpErrorResponse } from '@angular/common/http';
import { DOCUMENT, Injectable, InjectionToken, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { API_BASE_URL } from '../api/api-config';
import { TokenResponse } from '../api/models';
import { StorageService } from '../storage.service';
import { SessionStore } from './session.store';

/** Refresh this long before the access token expires (clock skew + request time). */
export const REFRESH_LEEWAY_MS = 60_000;

const LOCK_NAME = 'hn.auth.refresh';
const LEASE_KEY = 'hn.auth.refresh-lease';
const LEASE_MS = 10_000;

/** The refresh token was rejected (401 invalid / expired / reused): the session is over. */
export class RefreshFailedError extends Error {
  constructor(readonly response?: HttpErrorResponse) {
    super('Session refresh failed');
    this.name = 'RefreshFailedError';
  }
}

export function expiresSoon(expiresAtMs: number | null, now = Date.now(), leeway = REFRESH_LEEWAY_MS): boolean {
  return expiresAtMs !== null && expiresAtMs - now <= leeway;
}

/** Mutual exclusion across the tabs of this browser profile. */
export interface RefreshLock {
  run<T>(task: () => Promise<T>): Promise<T>;
}

export const REFRESH_LOCK = new InjectionToken<RefreshLock>('hn.refreshLock', {
  providedIn: 'root',
  factory: () => browserRefreshLock(inject(DOCUMENT).defaultView, inject(StorageService).isBrowser),
});

/**
 * Web Locks API where available (all current browsers). Fallback: a short lease in localStorage (write, wait, read
 * back; expires after {@link LEASE_MS} so a crashed tab cannot block the others). During SSR there is nothing to lock.
 */
export function browserRefreshLock(win: (Window & typeof globalThis) | null, isBrowser: boolean): RefreshLock {
  const locks = isBrowser ? (win?.navigator as Navigator | undefined)?.locks : undefined;
  if (locks?.request) {
    return {
      run<T>(task: () => Promise<T>): Promise<T> {
        return locks.request(LOCK_NAME, { mode: 'exclusive' }, task) as Promise<T>;
      },
    };
  }
  if (!isBrowser || !win) return { run: (task) => task() };
  return { run: (task) => withLease(win, task) };
}

async function withLease<T>(win: Window, task: () => Promise<T>): Promise<T> {
  const id = Math.random().toString(36).slice(2);
  const read = (): { id: string; until: number } | null => {
    try {
      return JSON.parse(win.localStorage.getItem(LEASE_KEY) ?? 'null');
    } catch {
      return null;
    }
  };
  const sleep = (ms: number) => new Promise<void>((r) => win.setTimeout(r, ms));
  const giveUp = Date.now() + LEASE_MS;
  let owned = false;
  try {
    while (Date.now() < giveUp) {
      const lease = read();
      if (!lease || lease.until < Date.now()) {
        win.localStorage.setItem(LEASE_KEY, JSON.stringify({ id, until: Date.now() + LEASE_MS }));
        await sleep(25);
        if (read()?.id === id) {
          owned = true;
          break;
        }
      }
      await sleep(50 + Math.random() * 50);
    }
  } catch {
    /* storage blocked: run without the cross-tab lease */
  }
  try {
    return await task();
  } finally {
    try {
      if (owned && read()?.id === id) win.localStorage.removeItem(LEASE_KEY);
    } catch {
      /* ignore */
    }
  }
}

/**
 * Exchanges the refresh token for a new pair. Only one refresh runs at a time: concurrent callers in this tab share
 * the in-flight promise, and other tabs wait for the cross-tab lock and then pick up the rotated pair from storage
 * instead of presenting the (now used) refresh token again — which the API would treat as theft and revoke the family.
 */
@Injectable({ providedIn: 'root' })
export class TokenRefresher {
  /** Bypasses the interceptors (the refresh call must never trigger another refresh). */
  private readonly http = new HttpClient(inject(HttpBackend));
  private readonly base = inject(API_BASE_URL);
  private readonly session = inject(SessionStore);
  private readonly lock = inject(REFRESH_LOCK);
  private inFlight: Promise<string> | null = null;

  /** Resolves with a fresh access token; rejects with {@link RefreshFailedError} when the session is over. */
  refresh(): Promise<string> {
    if (this.inFlight) return this.inFlight;
    const seen = this.session.refreshToken();
    const run = this.lock.run(() => this.refreshLocked(seen)).finally(() => {
      if (this.inFlight === run) this.inFlight = null;
    });
    this.inFlight = run;
    return run;
  }

  private async refreshLocked(seen: string | null): Promise<string> {
    const stored = this.session.syncFromStorage();
    if (!stored?.refreshToken) throw new RefreshFailedError();
    // Another tab rotated the pair while we waited for the lock: use its result.
    if (stored.refreshToken !== seen && !expiresSoon(Date.parse(stored.expiresAt))) return stored.accessToken;
    try {
      const response = await firstValueFrom(
        this.http.post<TokenResponse>(`${this.base}/api/auth/refresh`, { refreshToken: stored.refreshToken }),
      );
      this.session.setToken(response);
      return response.accessToken;
    } catch (error) {
      // 401 auth.refresh_token_invalid|expired|reused, or 400/422 for a malformed token: definitive.
      if (error instanceof HttpErrorResponse && [400, 401, 422].includes(error.status)) throw new RefreshFailedError(error);
      throw error; // network, 429, 5xx: transient, keep the session
    }
  }
}
