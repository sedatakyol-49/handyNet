import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { API_BASE_URL } from '../api/api-config';
import { EmailLanguage, Me, RegistrationRequest, TokenResponse } from '../api/models';
import { SessionStore } from './session.store';
import { StorageService } from '../storage.service';

/** Last /api/me of this browser profile, used only while the API is unreachable (see loadMe). */
const ME_CACHE_KEY = 'hn.me';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE_URL);
  private readonly session = inject(SessionStore);
  private readonly router = inject(Router);
  private readonly storage = inject(StorageService);
  private mePromise: Promise<Me | null> | null = null;

  async login(email: string, password: string): Promise<void> {
    const token = await firstValueFrom(
      this.http.post<TokenResponse>(`${this.base}/api/auth/login`, { email, password }),
    );
    this.session.setToken(token);
    this.mePromise = null;
    await this.loadMe(true);
  }

  register(request: RegistrationRequest): Promise<{ userId: string; organizationId: string }> {
    return firstValueFrom(
      this.http.post<{ userId: string; organizationId: string }>(`${this.base}/api/registration`, request),
    );
  }

  /** Loads /api/me once per session (or again when forced, e.g. after verification changes). */
  loadMe(force = false): Promise<Me | null> {
    if (!this.session.isAuthenticated()) return Promise.resolve(null);
    if (!force && this.session.me()) return Promise.resolve(this.session.me());
    if (!force && this.mePromise) return this.mePromise;
    this.mePromise = firstValueFrom(this.http.get<Me>(`${this.base}/api/me`))
      .then((me) => {
        this.session.setMe(me);
        this.storage.setJson(ME_CACHE_KEY, me);
        return me;
      })
      .catch((error: unknown) => this.cachedMe(error))
      .finally(() => (this.mePromise = null));
    return this.mePromise;
  }

  /**
   * The API did not answer (no connection, dead gateway): keep working with the last known /api/me of the same user, so
   * a reload while offline does not throw a signed-in cashier out (the till's offline mode depends on it). The API
   * checks every permission again as soon as it is reachable; an answer from the API (401, 403, …) never uses this.
   */
  private cachedMe(error: unknown): Me | null {
    const status = error instanceof HttpErrorResponse ? error.status : -1;
    if (status !== 0 && status !== 502 && status !== 503 && status !== 504) return null;
    const cached = this.storage.getJson<Me>(ME_CACHE_KEY);
    const userId = this.session.userId();
    if (!cached || !Array.isArray(cached.memberships) || !userId || cached.userId !== userId) return null;
    this.session.setMe(cached);
    return cached;
  }

  /**
   * Ends this session: clears it locally right away and revokes the refresh token family on the server
   * (best effort — an unreachable API must not keep the user signed in).
   */
  logout(): void {
    const refreshToken = this.session.refreshToken();
    this.session.clear();
    this.storage.set(ME_CACHE_KEY, null);
    if (refreshToken) {
      this.http.post(`${this.base}/api/auth/logout`, { refreshToken }).subscribe({ error: () => undefined });
    }
    void this.router.navigate(['/login']);
  }

  /** Revokes every session of the user (all devices, including this one) and signs out here. */
  async logoutEverywhere(): Promise<void> {
    await firstValueFrom(this.http.post<void>(`${this.base}/api/auth/logout-all`, {}));
    this.session.clear();
    await this.router.navigate(['/login'], { queryParams: { signedOut: 'all' } });
  }

  /** Changes the password; the API ends all other sessions and returns a new pair for this one. */
  async changePassword(currentPassword: string, newPassword: string): Promise<void> {
    const token = await firstValueFrom(
      this.http.post<TokenResponse>(`${this.base}/api/auth/change-password`, { currentPassword, newPassword }),
    );
    this.session.setToken(token);
  }

  /** Always 202 for a valid address, whether or not an account exists (no enumeration). */
  requestPasswordReset(email: string, language: EmailLanguage): Promise<unknown> {
    return firstValueFrom(this.http.post(`${this.base}/api/auth/password-reset/request`, { email, language }));
  }

  confirmPasswordReset(token: string, newPassword: string): Promise<unknown> {
    return firstValueFrom(this.http.post(`${this.base}/api/auth/password-reset/confirm`, { token, newPassword }));
  }

  verifyEmail(token: string): Promise<unknown> {
    return firstValueFrom(this.http.post(`${this.base}/api/auth/verify-email`, { token }));
  }

  resendVerification(language: EmailLanguage): Promise<unknown> {
    return firstValueFrom(this.http.post(`${this.base}/api/auth/verify-email/resend`, { language }));
  }
}
