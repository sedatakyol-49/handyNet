import { inject, PLATFORM_ID } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { CanActivateFn, Router } from '@angular/router';
import { ModuleName, Permission } from '../api/models';
import { AuthService } from './auth.service';
import { SessionStore } from './session.store';

/** Authenticated area. On the server (SSR) the token is unknown, so these routes render client-side only. */
export const authGuard: CanActivateFn = async (_route, state) => {
  const router = inject(Router);
  if (!isPlatformBrowser(inject(PLATFORM_ID))) return false;
  const session = inject(SessionStore);
  if (!session.isAuthenticated()) return router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
  const me = await inject(AuthService).loadMe();
  if (!me) return router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
  return true;
};

/** Anonymous pages (login/registration) redirect signed-in users into the app. */
export const guestGuard: CanActivateFn = () => {
  const session = inject(SessionStore);
  return session.isAuthenticated() ? inject(Router).createUrlTree(['/app']) : true;
};

/** Requires a verified organization with the given module and/or permission. */
export function accessGuard(module?: ModuleName, permission?: Permission): CanActivateFn {
  return () => {
    const session = inject(SessionStore);
    return session.allows(module, permission) ? true : inject(Router).createUrlTree(['/app/uebersicht']);
  };
}

export const platformAdminGuard: CanActivateFn = () => {
  const session = inject(SessionStore);
  return session.isPlatformAdmin() ? true : inject(Router).createUrlTree(['/app/uebersicht']);
};
