import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE_URL } from '../../../core/api/api-config';
import { DisplayState } from './display-state';

/** `POST /api/pos/registers/{id}/display-link`: the token is only returned here (the API stores its hash). */
export interface DisplayLink {
  token: string;
  /** Web app path, e.g. "/kundendisplay/<token>". */
  path: string;
  expiresAt: string;
}

/** `GET …/display-link`: whether the register has a link (204 → null = none). */
export interface DisplayLinkStatus {
  active: boolean;
  expiresAt: string | null;
}

/** `GET /api/public/pos-display/{token}`. */
export interface PublicDisplay {
  version: number;
  shop: { name: string | null; logoUrl: string | null };
  /** Null until the till pushed a state (e.g. after an API restart): the display shows the idle screen. */
  state: unknown;
}

/** Remote customer display (tablet / other device): link management (`pos.sell`), state push, anonymous long-poll. */
@Injectable({ providedIn: 'root' })
export class DisplayApi {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE_URL);
  private readonly url = `${this.base}/api/pos`;

  /** Creates a new link; earlier links of the register stop working. */
  createLink(registerId: string): Observable<DisplayLink> {
    return this.http.post<DisplayLink>(`${this.url}/registers/${registerId}/display-link`, {});
  }

  linkStatus(registerId: string): Observable<DisplayLinkStatus | null> {
    return this.http.get<DisplayLinkStatus | null>(`${this.url}/registers/${registerId}/display-link`);
  }

  revokeLink(registerId: string): Observable<void> {
    return this.http.delete<void>(`${this.url}/registers/${registerId}/display-link`);
  }

  /** Kept in memory by the API (≤ 16 KiB). */
  pushState(registerId: string, state: DisplayState): Observable<unknown> {
    return this.http.put(`${this.url}/registers/${registerId}/display-state`, state);
  }

  /**
   * Long-poll: with `since` = the version the display already has, the API answers after a change or with 204 (null)
   * after ~20 s. Unknown, expired and revoked tokens are the same 404.
   */
  publicState(token: string, since: number | null): Observable<PublicDisplay | null> {
    const query = since === null ? '' : `?since=${since}`;
    return this.http.get<PublicDisplay | null>(`${this.base}/api/public/pos-display/${encodeURIComponent(token)}${query}`);
  }

  /** Absolute URL for the shop logo (relative to the API like other asset URLs). */
  assetUrl(relative: string | null | undefined): string | null {
    if (!relative) return null;
    return /^https?:\/\//i.test(relative) ? relative : `${this.base}${relative}`;
  }
}
