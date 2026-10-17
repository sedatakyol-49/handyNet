import { HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, catchError, defer, from, of, switchMap, throwError } from 'rxjs';
import { API_BASE_URL } from '../api/api-config';
import { SessionStore } from './session.store';
import { RefreshFailedError, TokenRefresher, expiresSoon } from './token-refresher';

export const ORGANIZATION_HEADER = 'X-Organization-Id';

/** Endpoints that are anonymous or not tenant-scoped. */
const NO_TENANT = [/\/api\/auth\//, /\/api\/registration/, /\/api\/me$/, /\/api\/admin\//];

/** Anonymous auth endpoints: never refreshed or retried, a 401 there says nothing about the session. */
const ANONYMOUS_AUTH = /\/api\/auth\/(login|refresh|logout|password-reset\/request|password-reset\/confirm|verify-email)(\?|$)/;

/**
 * Adds `Authorization: Bearer` and `X-Organization-Id` to HandyNet API calls and keeps the session alive:
 * - shortly before the access token expires, the request first waits for a refresh;
 * - on 401 it refreshes (one refresh shared by all concurrent requests and tabs, see {@link TokenRefresher}) and
 *   retries the request once with the new token;
 * - if the refresh token is rejected (or the retry is 401 again) the session is cleared and the user is sent to the
 *   login page. Network errors / 5xx during a refresh are passed to the caller without ending the session.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const session = inject(SessionStore);
  const router = inject(Router);
  const refresher = inject(TokenRefresher);
  const base = inject(API_BASE_URL);

  const isApi = req.url.startsWith(`${base}/api/`) || req.url.startsWith('/api/');
  if (!isApi) return next(req);

  const tenantScoped = !NO_TENANT.some((r) => r.test(req.url));
  const send = (token: string | null) => next(decorate(req, token, tenantScoped ? session.organizationId() : null));

  // Caller-supplied credentials and anonymous auth endpoints are passed through unchanged.
  if (req.headers.has('Authorization') || ANONYMOUS_AUTH.test(req.url)) return send(session.token());

  const signOut = () => {
    if (!session.isAuthenticated()) return;
    session.clear();
    const returnUrl = router.url.startsWith('/app') ? router.url : undefined;
    void router.navigate(['/login'], { queryParams: { returnUrl, expired: 1 } });
  };

  /** Refreshes; a definitive failure ends the session and surfaces `original` (or the refresh error) to the caller. */
  const refreshed = (original: unknown): Observable<string> =>
    defer(() => from(refresher.refresh())).pipe(
      catchError((error: unknown) => {
        if (error instanceof RefreshFailedError) {
          signOut();
          return throwError(() => original ?? error.response ?? error);
        }
        return throwError(() => error);
      }),
    );

  const proactive = session.refreshToken() !== null && expiresSoon(session.accessExpiresAt());
  const token$: Observable<string | null> = proactive
    ? refreshed(null).pipe(
        // Transient failure: try with the current token; the 401 path below decides.
        catchError((error: unknown) => (session.isAuthenticated() ? of(session.token()) : throwError(() => error))),
      )
    : of(session.token());

  return token$.pipe(
    switchMap((token) =>
      send(token).pipe(
        catchError((error: unknown) => {
          if (!(error instanceof HttpErrorResponse) || error.status !== 401 || !token) return throwError(() => error);
          if (!session.refreshToken()) {
            signOut();
            return throwError(() => error);
          }
          // Someone else (another request or tab) already rotated the pair: retry with it, no new refresh.
          const current = session.token();
          const retryToken$ = current && current !== token ? of(current) : refreshed(error);
          return retryToken$.pipe(
            switchMap((fresh) =>
              send(fresh).pipe(
                catchError((retryError: unknown) => {
                  if (retryError instanceof HttpErrorResponse && retryError.status === 401) signOut();
                  return throwError(() => retryError);
                }),
              ),
            ),
          );
        }),
      ),
    ),
  );
};

function decorate<T>(req: HttpRequest<T>, token: string | null, organizationId: string | null): HttpRequest<T> {
  let headers = req.headers;
  if (token && !headers.has('Authorization')) headers = headers.set('Authorization', `Bearer ${token}`);
  if (token && organizationId && !headers.has(ORGANIZATION_HEADER)) headers = headers.set(ORGANIZATION_HEADER, organizationId);
  return headers === req.headers ? req : req.clone({ headers });
}
