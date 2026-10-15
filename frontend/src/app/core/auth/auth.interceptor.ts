import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { API_BASE_URL } from '../api/api-config';
import { SessionStore } from './session.store';

export const ORGANIZATION_HEADER = 'X-Organization-Id';

/** Endpoints that are anonymous or not tenant-scoped. */
const NO_TENANT = [/\/api\/auth\//, /\/api\/registration/, /\/api\/me$/, /\/api\/admin\//];

/**
 * Adds `Authorization: Bearer` and `X-Organization-Id` to HandyNet API calls and logs the user out
 * when the API answers 401 for an authenticated request.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const session = inject(SessionStore);
  const router = inject(Router);
  const base = inject(API_BASE_URL);

  const isApi = req.url.startsWith(`${base}/api/`) || req.url.startsWith('/api/');
  if (!isApi) return next(req);

  const token = session.token();
  let headers = req.headers;
  if (token && !headers.has('Authorization')) headers = headers.set('Authorization', `Bearer ${token}`);
  const org = session.organizationId();
  if (token && org && !headers.has(ORGANIZATION_HEADER) && !NO_TENANT.some((r) => r.test(req.url))) {
    headers = headers.set(ORGANIZATION_HEADER, org);
  }

  return next(req.clone({ headers })).pipe(
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse && error.status === 401 && token) {
        session.clear();
        void router.navigate(['/login'], { queryParams: { returnUrl: router.url, expired: 1 } });
      }
      return throwError(() => error);
    }),
  );
};
