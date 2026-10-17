import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ApiProblem, fieldErrors, toApiProblem } from '../../core/api/api-problem';
import { emailLanguage } from '../../core/auth/account-links';
import { AuthService } from '../../core/auth/auth.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { UI } from '../../shared/ui';
import { AuthLayoutComponent } from './auth-layout';

const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

/**
 * "Passwort vergessen": asks for a reset link. The API answers 202 for every valid address, so the confirmation is
 * worded the same whether or not an account exists (no account enumeration).
 */
@Component({
  selector: 'hn-forgot-password-page',
  imports: [FormsModule, RouterLink, AuthLayoutComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-auth-layout>
      <div class="hn-card p-6">
        <h1 class="text-lg">{{ 'auth.forgot.title' | t }}</h1>
        @if (sentTo(); as address) {
          <div class="mt-4 space-y-3" data-testid="reset-requested">
            <hn-banner tone="success" [heading]="'auth.forgot.sentTitle' | t" [text]="'auth.forgot.sent' | t: { email: address }" />
            <p class="text-sm text-slate-600">{{ 'auth.forgot.sentHint' | t }}</p>
            <button hnButton type="button" class="w-full" (click)="again()">{{ 'auth.forgot.again' | t }}</button>
          </div>
        } @else {
          <p class="mt-1 mb-5 text-sm text-slate-500">{{ 'auth.forgot.subtitle' | t }}</p>
          <!-- Field errors are shown at the field; the banner covers the rest (rate limit, network). -->
          <hn-banner class="mb-4" [error]="emailErrors().length ? null : problem()" />
          <form class="space-y-4" (ngSubmit)="submit()" novalidate>
            <hn-field [label]="'auth.email' | t" for="forgot-email" [required]="true" [errors]="emailErrors()">
              <input
                hnInput
                id="forgot-email"
                name="email"
                type="email"
                autocomplete="username"
                required
                [(ngModel)]="email"
                [attr.aria-invalid]="emailErrors().length > 0 || null"
                [attr.aria-describedby]="emailErrors().length ? 'forgot-email-error' : null"
              />
            </hn-field>
            <button hnButton variant="primary" type="submit" class="w-full" [loading]="busy()" [disabled]="busy()">
              {{ 'auth.forgot.submit' | t }}
            </button>
          </form>
        }
      </div>
      <p class="mt-4 text-center text-sm">
        <a routerLink="/login" class="hn-link">{{ 'auth.backToLogin' | t }}</a>
      </p>
    </hn-auth-layout>
  `,
})
export class ForgotPasswordPage {
  private readonly auth = inject(AuthService);
  private readonly i18n = inject(I18nService);

  protected email = '';
  protected readonly busy = signal(false);
  protected readonly submitted = signal(false);
  protected readonly problem = signal<ApiProblem | null>(null);
  protected readonly sentTo = signal<string | null>(null);

  protected emailErrors(): string[] {
    const messages: string[] = [];
    if (this.submitted()) {
      const v = this.email.trim();
      if (!v) messages.push(this.i18n.t('validation.required'));
      else if (!EMAIL.test(v)) messages.push(this.i18n.t('validation.email'));
    }
    return [...messages, ...fieldErrors(this.problem(), 'email')];
  }

  protected async submit(): Promise<void> {
    this.submitted.set(true);
    this.problem.set(null);
    const email = this.email.trim();
    if (!email || !EMAIL.test(email) || this.busy()) return;
    this.busy.set(true);
    try {
      await this.auth.requestPasswordReset(email, emailLanguage(this.i18n.lang()));
      this.sentTo.set(email);
    } catch (e) {
      this.problem.set(toApiProblem(e));
    } finally {
      this.busy.set(false);
    }
  }

  protected again(): void {
    this.sentTo.set(null);
    this.submitted.set(false);
  }
}
