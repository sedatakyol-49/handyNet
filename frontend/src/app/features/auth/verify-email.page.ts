import { ChangeDetectionStrategy, Component, DOCUMENT, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiProblem, toApiProblem } from '../../core/api/api-problem';
import { consumeFragmentToken } from '../../core/auth/account-links';
import { AuthService } from '../../core/auth/auth.service';
import { SessionStore } from '../../core/auth/session.store';
import { I18nService } from '../../core/i18n/i18n.service';
import { describeProblem } from '../../core/ui-state/toast.service';
import { UI } from '../../shared/ui';
import { EmailVerificationBannerComponent } from '../shell/email-verification-banner';
import { AuthLayoutComponent } from './auth-layout';

type VerifyState = 'checking' | 'verified' | 'missing' | 'invalid' | 'expired' | 'error';

/**
 * Target of the verification email link `/email-bestaetigen#token=…`. Works signed in or not (the link may be opened
 * on another device). The token is removed from the address bar before it is sent to the API.
 */
@Component({
  selector: 'hn-verify-email-page',
  imports: [RouterLink, AuthLayoutComponent, EmailVerificationBannerComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-auth-layout>
      <div class="hn-card p-6" aria-live="polite">
        <h1 class="text-lg">{{ 'auth.verify.title' | t }}</h1>
        <div class="mt-4 space-y-4" [attr.data-testid]="'verify-' + state()">
          @switch (state()) {
            @case ('checking') {
              <p class="flex items-center gap-2 text-sm text-slate-600" role="status">
                <span class="size-3.5 animate-spin rounded-full border-2 border-current border-r-transparent" aria-hidden="true"></span>
                {{ 'auth.verify.checking' | t }}
              </p>
            }
            @case ('verified') {
              <hn-banner tone="success" [text]="'auth.verify.success' | t" />
              @if (session.isAuthenticated()) {
                <a hnButton variant="primary" class="w-full" routerLink="/app">{{ 'auth.verify.continue' | t }}</a>
              } @else {
                <a hnButton variant="primary" class="w-full" routerLink="/login">{{ 'auth.login.submit' | t }}</a>
              }
            }
            @default {
              <hn-banner [tone]="state() === 'expired' ? 'warning' : 'danger'" [text]="message()" />
              @if (session.isAuthenticated()) {
                <hn-email-verification-banner [inline]="true" />
              } @else if (state() !== 'error') {
                <p class="text-sm text-slate-600">{{ 'auth.verify.loginToResend' | t }}</p>
                <a hnButton class="w-full" routerLink="/login">{{ 'auth.login.submit' | t }}</a>
              }
            }
          }
        </div>
      </div>
    </hn-auth-layout>
  `,
})
export class VerifyEmailPage {
  protected readonly session = inject(SessionStore);
  private readonly auth = inject(AuthService);
  private readonly i18n = inject(I18nService);

  protected readonly state = signal<VerifyState>('checking');
  private readonly problem = signal<ApiProblem | null>(null);
  protected readonly message = computed(() => {
    const problem = this.problem();
    return problem ? describeProblem(this.i18n, problem) : this.i18n.t('auth.linkIncomplete');
  });

  constructor() {
    const token = consumeFragmentToken(inject(DOCUMENT).defaultView);
    if (!token) {
      this.state.set('missing');
      // A signed-in user can ask for a new link right here.
      void this.auth.loadMe();
      return;
    }
    void this.verify(token);
  }

  private async verify(token: string): Promise<void> {
    try {
      await this.auth.verifyEmail(token);
      this.state.set('verified');
      // Refresh /api/me so the "please verify" banner disappears in the app.
      if (this.session.isAuthenticated()) void this.auth.loadMe(true);
    } catch (e) {
      const problem = toApiProblem(e);
      this.state.set(problem.code === 'auth.token_expired' ? 'expired' : problem.code === 'auth.token_invalid' ? 'invalid' : 'error');
      this.problem.set(problem);
      if (this.session.isAuthenticated()) void this.auth.loadMe();
    }
  }
}
