import { ChangeDetectionStrategy, Component, DOCUMENT, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ApiProblem, toApiProblem } from '../../core/api/api-problem';
import { consumeFragmentToken } from '../../core/auth/account-links';
import { AuthService } from '../../core/auth/auth.service';
import { NewPasswordInput, newPasswordErrors } from '../../core/auth/password-policy';
import { SessionStore } from '../../core/auth/session.store';
import { I18nService } from '../../core/i18n/i18n.service';
import { describeProblem } from '../../core/ui-state/toast.service';
import { UI } from '../../shared/ui';
import { AuthLayoutComponent } from './auth-layout';

type LinkState = 'form' | 'missing' | 'invalid' | 'expired';

/**
 * Target of the reset email link `/passwort-zuruecksetzen#token=…`. The token is taken from the fragment and removed
 * from the address bar at once; it lives only in this component (a reload shows "link incomplete").
 */
@Component({
  selector: 'hn-reset-password-page',
  imports: [FormsModule, RouterLink, AuthLayoutComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-auth-layout>
      <div class="hn-card p-6">
        <h1 class="text-lg">{{ 'auth.reset.title' | t }}</h1>
        @switch (state()) {
          @case ('form') {
            <p class="mt-1 mb-5 text-sm text-slate-500">{{ 'auth.reset.subtitle' | t }}</p>
            <hn-banner class="mb-4" [text]="bannerText()" />
            <form class="space-y-4" (ngSubmit)="submit()" novalidate>
              <hn-field [label]="'auth.reset.newPassword' | t" for="reset-password" [required]="true" [hint]="'auth.register.passwordHint' | t" [errors]="errorFor('newPassword')">
                <input hnInput id="reset-password" name="newPassword" type="password" autocomplete="new-password" required [(ngModel)]="form.newPassword"
                  [attr.aria-invalid]="!!errorFor('newPassword') || null"
                  [attr.aria-describedby]="errorFor('newPassword') ? 'reset-password-error' : 'reset-password-hint'" />
              </hn-field>
              <hn-field [label]="'auth.reset.confirmPassword' | t" for="reset-confirm" [required]="true" [errors]="errorFor('confirmPassword')">
                <input hnInput id="reset-confirm" name="confirmPassword" type="password" autocomplete="new-password" required [(ngModel)]="form.confirmPassword"
                  [attr.aria-invalid]="!!errorFor('confirmPassword') || null"
                  [attr.aria-describedby]="errorFor('confirmPassword') ? 'reset-confirm-error' : null" />
              </hn-field>
              <button hnButton variant="primary" type="submit" class="w-full" [loading]="busy()" [disabled]="busy()" data-testid="reset-submit">
                {{ 'auth.reset.submit' | t }}
              </button>
            </form>
          }
          @default {
            <div class="mt-4 space-y-4" data-testid="reset-link-problem">
              <hn-banner [tone]="state() === 'expired' ? 'warning' : 'danger'" [text]="linkProblem() | t" />
              <a hnButton variant="primary" class="w-full" routerLink="/passwort-vergessen">{{ 'auth.reset.requestNew' | t }}</a>
            </div>
          }
        }
      </div>
      <p class="mt-4 text-center text-sm">
        <a routerLink="/login" class="hn-link">{{ 'auth.backToLogin' | t }}</a>
      </p>
    </hn-auth-layout>
  `,
})
export class ResetPasswordPage {
  private readonly auth = inject(AuthService);
  private readonly session = inject(SessionStore);
  private readonly router = inject(Router);
  private readonly i18n = inject(I18nService);

  private readonly token = consumeFragmentToken(inject(DOCUMENT).defaultView);
  protected readonly state = signal<LinkState>(this.token ? 'form' : 'missing');
  protected readonly busy = signal(false);
  protected readonly submitted = signal(false);
  protected readonly problem = signal<ApiProblem | null>(null);
  protected form: Required<Omit<NewPasswordInput, 'currentPassword'>> = { newPassword: '', confirmPassword: '' };

  protected errorFor(field: 'newPassword' | 'confirmPassword'): string | null {
    if (this.submitted()) {
      const key = newPasswordErrors(this.form)[field];
      if (key) return this.i18n.t(key);
    }
    const problem = this.problem();
    if (field === 'newPassword' && problem?.code === 'password.weak') return describeProblem(this.i18n, problem);
    return null;
  }

  protected bannerText(): string | null {
    const problem = this.problem();
    return problem && problem.code !== 'password.weak' ? describeProblem(this.i18n, problem) : null;
  }

  protected linkProblem(): string {
    switch (this.state()) {
      case 'expired':
        return 'errors.codes.auth.token_expired';
      case 'invalid':
        return 'errors.codes.auth.token_invalid';
      default:
        return 'auth.linkIncomplete';
    }
  }

  protected async submit(): Promise<void> {
    this.submitted.set(true);
    this.problem.set(null);
    if (!this.token || Object.keys(newPasswordErrors(this.form)).length > 0 || this.busy()) return;
    this.busy.set(true);
    try {
      await this.auth.confirmPasswordReset(this.token, this.form.newPassword);
      // The API ended every session of the user, including one that may be open in this browser.
      this.session.clear();
      await this.router.navigate(['/login'], { queryParams: { reset: 1 } });
    } catch (e) {
      const problem = toApiProblem(e);
      if (problem.code === 'auth.token_invalid') this.state.set('invalid');
      else if (problem.code === 'auth.token_expired') this.state.set('expired');
      else this.problem.set(problem);
    } finally {
      this.busy.set(false);
    }
  }
}
