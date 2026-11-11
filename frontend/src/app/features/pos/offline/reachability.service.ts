import { HttpErrorResponse, HttpInterceptorFn, HttpResponse } from '@angular/common/http';
import { Injectable, PLATFORM_ID, inject, signal } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { tap } from 'rxjs';
import { API_BASE_URL } from '../../../core/api/api-config';

const PROBE_TIMEOUT_MS = 4000;
const PROBE_ONLINE_MS = 30_000;
const PROBE_OFFLINE_MS = 5000;

/** A request failed without an answer from the API (or behind a dead gateway): the till may be offline. */
export function isUnreachable(status: number): boolean {
  return status === 0 || status === 502 || status === 503 || status === 504;
}

/** Is this the answer of the API's readiness check (and not, say, an HTML page of a captive portal or the app shell)? */
export function isHealthyAnswer(status: number, contentType: string | null, body: string): boolean {
  return status === 200 && !/text\/html/i.test(contentType ?? '') && /^\s*(healthy|degraded)\s*$/i.test(body);
}

/**
 * Whether the till reaches the API. Sources: the browser's online/offline events, every API request that fails without
 * an answer (reported by `reachabilityInterceptor`) and a periodic probe of `/health/ready` while the POS is open.
 * A single failed request does not flip the state by itself — the probe decides — so one aborted request never puts
 * the till into offline mode.
 */
@Injectable({ providedIn: 'root' })
export class ReachabilityService {
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));
  private readonly base = inject(API_BASE_URL);
  private timer: ReturnType<typeof setTimeout> | null = null;
  private watchers = 0;
  private probing: Promise<boolean> | null = null;

  readonly online = signal(true);

  constructor() {
    if (!this.browser) return;
    if (navigator.onLine === false) this.online.set(false);
    window.addEventListener('offline', () => this.set(false));
    window.addEventListener('online', () => void this.check());
  }

  /** The POS app keeps the periodic probe running while it is on screen. */
  watch(): () => void {
    if (!this.browser) return () => undefined;
    this.watchers++;
    this.schedule(0);
    return () => {
      this.watchers = Math.max(0, this.watchers - 1);
      if (!this.watchers && this.timer) {
        clearTimeout(this.timer);
        this.timer = null;
      }
    };
  }

  /** An API request got an answer: the API is reachable. */
  reportSuccess(): void {
    if (!this.online()) this.set(true);
  }

  /** An API request failed without an answer: ask the readiness endpoint before believing it. */
  reportFailure(): void {
    if (this.browser) void this.check();
  }

  /** Probes now (single flight) and returns the result. */
  check(): Promise<boolean> {
    if (!this.browser) return Promise.resolve(true);
    this.probing ??= this.probe().then((ok) => {
      this.probing = null;
      this.set(ok);
      return ok;
    });
    return this.probing;
  }

  private set(online: boolean): void {
    if (this.online() !== online) this.online.set(online);
    if (this.watchers) this.schedule(online ? PROBE_ONLINE_MS : PROBE_OFFLINE_MS);
  }

  private schedule(delay: number): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = setTimeout(() => {
      this.timer = null;
      void this.check().then((ok) => {
        if (this.watchers && !this.timer) this.schedule(ok ? PROBE_ONLINE_MS : PROBE_OFFLINE_MS);
      });
    }, delay);
  }

  private async probe(): Promise<boolean> {
    if (navigator.onLine === false) return false;
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), PROBE_TIMEOUT_MS);
    try {
      const res = await fetch(`${this.base}/health/ready`, { cache: 'no-store', credentials: 'omit', signal: controller.signal });
      return isHealthyAnswer(res.status, res.headers.get('content-type'), await res.text());
    } catch {
      return false;
    } finally {
      clearTimeout(timer);
    }
  }
}

/** Tells the ReachabilityService how API requests end (answer = reachable; no answer / dead gateway = check). */
export const reachabilityInterceptor: HttpInterceptorFn = (req, next) => {
  const base = inject(API_BASE_URL);
  if (!(req.url.startsWith(`${base}/api/`) || req.url.startsWith('/api/'))) return next(req);
  const reach = inject(ReachabilityService);
  return next(req).pipe(
    tap({
      next: (event) => {
        if (event instanceof HttpResponse) reach.reportSuccess();
      },
      error: (error: unknown) => {
        const status = error instanceof HttpErrorResponse ? error.status : 0;
        if (isUnreachable(status)) reach.reportFailure();
        else reach.reportSuccess();
      },
    }),
  );
};
