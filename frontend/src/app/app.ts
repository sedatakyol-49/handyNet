import { ChangeDetectionStrategy, Component, effect, inject, untracked } from '@angular/core';
import { Router, RouterOutlet } from '@angular/router';
import { AuthService } from './core/auth/auth.service';
import { SessionStore } from './core/auth/session.store';
import { ToastHostComponent } from './shared/ui';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, ToastHostComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<router-outlet /><hn-toast-host />`,
})
export class App {
  private readonly session = inject(SessionStore);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  constructor() {
    // App-wide (back office and the standalone POS app): signed out in another tab → leave the app;
    // a different user signed in elsewhere (/api/me was dropped) → reload /api/me so requests carry an organization.
    effect(() => {
      if (this.session.signedOutElsewhere() > 0) {
        untracked(() => {
          if (/^\/(app|kasse)(\/|$|\?)/.test(this.router.url)) void this.router.navigate(['/login']);
        });
      }
    });
    effect(() => {
      if (this.session.isAuthenticated() && !this.session.me()) void untracked(() => this.auth.loadMe());
    });
  }
}
