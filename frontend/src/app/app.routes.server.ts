import { RenderMode, ServerRoute } from '@angular/ssr';

/**
 * Public pages are server-rendered (the public marketplace will join them later). Everything behind
 * login depends on the token in localStorage and is therefore rendered in the browser only.
 */
export const serverRoutes: ServerRoute[] = [
  { path: 'login', renderMode: RenderMode.Server },
  { path: 'registrierung', renderMode: RenderMode.Server },
  { path: 'passwort-vergessen', renderMode: RenderMode.Server },
  // /passwort-zuruecksetzen and /email-bestaetigen read their token from the URL fragment, which only the browser
  // sees, so they render client-side like the app (catch-all below).
  { path: '**', renderMode: RenderMode.Client },
];
