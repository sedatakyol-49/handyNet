import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiProblem, toApiProblem } from '../../core/api/api-problem';
import { AuthService } from '../../core/auth/auth.service';
import { NewPasswordInput, newPasswordErrors } from '../../core/auth/password-policy';
import { SessionStore } from '../../core/auth/session.store';
import { I18nService } from '../../core/i18n/i18n.service';
import { ToastService, describeProblem } from '../../core/ui-state/toast.service';
import { UI } from '../../shared/ui';
import { EmailVerificationBannerComponent } from '../shell/email-verification-banner';

type PasswordField = keyof NewPasswordInput;

/** Konto & Sicherheit: account data, change password, sign out everywhere. */
@Component({
  selector: 'hn-account-page',
  imports: [FormsModule, EmailVerificationBannerComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-page-header [title]="'account.title' | t" [description]="'account.subtitle' | t" />

    <div class="grid max-w-5xl gap-4 lg:grid-cols-2">
      <section class="hn-card p-4 lg:col-span-2" aria-labelledby="account-profile">
        <h2 id="account-profile" class="text-base">{{ 'account.profile' | t }}</h2>
        @if (session.me(); as me) {
          <dl class="mt-3 grid gap-x-6 gap-y-2 text-sm sm:grid-cols-[max-content_1fr]">
            <dt class="text-slate-500">{{ 'common.name' | t }}</dt>
            <dd class="font-medium">{{ me.displayName }}</dd>
            <dt class="text-slate-500">{{ 'auth.email' | t }}</dt>
            <dd class="flex flex-wrap items-center gap-2">
              <span class="font-medium">{{ me.email }}</span>
              @if (me.emailVerified) {
                <span class="rounded bg-emerald-50 px-1.5 py-0.5 text-xs font-medium text-emerald-800 ring-1 ring-emerald-200" data-testid="email-verified">
                  {{ 'account.verified' | t }}
                </span>
              } @else {
                <span class="rounded bg-amber-50 px-1.5 py-0.5 text-xs font-medium text-amber-900 ring-1 ring-amber-200" data-testid="email-unverified">
                  {{ 'account.unverified' | t }}
                </span>
              }
            </dd>
          </dl>
          <hn-email-verification-banner class="mt-3" [inline]="true" />
        }
      </section>

      <section class="hn-card p-4" aria-labelledby="account-password">
        <h2 id="account-password" class="text-base">{{ 'account.password.title' | t }}</h2>
        <p class="mt-1 mb-4 text-sm text-slate-500">{{ 'account.password.hint' | t }}</p>
        <hn-banner class="mb-3" [text]="bannerText()" />
        <form class="space-y-4" (ngSubmit)="changePassword()" novalidate data-testid="change-password-form">
          <!-- Lets password managers attach the change to the right account. -->
          <input type="email" name="username" autocomplete="username" [value]="session.me()?.email ?? ''" class="hidden" aria-hidden="true" tabindex="-1" readonly />
          <hn-field [label]="'account.password.current' | t" for="pw-current" [required]="true" [errors]="errorFor('currentPassword')">
            <input hnInput id="pw-current" name="currentPassword" type="password" autocomplete="current-password" required [(ngModel)]="form.currentPassword"
              [attr.aria-invalid]="!!errorFor('currentPassword') || null"
              [attr.aria-describedby]="errorFor('currentPassword') ? 'pw-current-error' : null" />
          </hn-field>
          <hn-field [label]="'account.password.new' | t" for="pw-new" [required]="true" [hint]="'auth.register.passwordHint' | t" [errors]="errorFor('newPassword')">
            <input hnInput id="pw-new" name="newPassword" type="password" autocomplete="new-password" required [(ngModel)]="form.newPassword"
              [attr.aria-invalid]="!!errorFor('newPassword') || null"
              [attr.aria-describedby]="errorFor('newPassword') ? 'pw-new-error' : 'pw-new-hint'" />
          </hn-field>
          <hn-field [label]="'account.password.confirm' | t" for="pw-confirm" [required]="true" [errors]="errorFor('confirmPassword')">
            <input hnInput id="pw-confirm" name="confirmPassword" type="password" autocomplete="new-password" required [(ngModel)]="form.confirmPassword"
              [attr.aria-invalid]="!!errorFor('confirmPassword') || null"
              [attr.aria-describedby]="errorFor('confirmPassword') ? 'pw-confirm-error' : null" />
          </hn-field>
          <div class="flex justify-end">
            <button hnButton variant="primary" type="submit" [loading]="savingPassword()" [disabled]="savingPassword()" data-testid="change-password-submit">
              {{ 'account.password.submit' | t }}
            </button>
          </div>
        </form>
      </section>

      <section class="hn-card p-4" aria-labelledby="account-sessions">
        <h2 id="account-sessions" class="text-base">{{ 'account.sessions.title' | t }}</h2>
        <p class="mt-1 mb-4 text-sm text-slate-600">{{ 'account.sessions.text' | t }}</p>
        <hn-banner class="mb-3" [error]="logoutError()" />
        <button hnButton variant="danger" type="button" (click)="confirmOpen.set(true)" data-testid="logout-all">
          {{ 'account.sessions.signOutAll' | t }}
        </button>
      </section>
    </div>

    <hn-dialog [open]="confirmOpen()" [title]="'account.sessions.confirmTitle' | t" (closed)="confirmOpen.set(false)">
      <p class="text-sm text-slate-700">{{ 'account.sessions.confirmText' | t }}</p>
      <div class="mt-4 flex justify-end gap-2">
        <button hnButton type="button" (click)="confirmOpen.set(false)">{{ 'common.cancel' | t }}</button>
        <button hnButton variant="danger" type="button" [loading]="signingOut()" [disabled]="signingOut()" (click)="logoutEverywhere()" data-testid="logout-all-confirm">
          {{ 'account.sessions.signOutAll' | t }}
        </button>
      </div>
    </hn-dialog>
  `,
})
export class AccountPage {
  protected readonly session = inject(SessionStore);
  private readonly auth = inject(AuthService);
  private readonly i18n = inject(I18nService);
  private readonly toast = inject(ToastService);

  protected form: Required<NewPasswordInput> = { currentPassword: '', newPassword: '', confirmPassword: '' };
  protected readonly submitted = signal(false);
  protected readonly savingPassword = signal(false);
  protected readonly passwordProblem = signal<ApiProblem | null>(null);

  protected readonly confirmOpen = signal(false);
  protected readonly signingOut = signal(false);
  protected readonly logoutError = signal<unknown>(null);

  protected errorFor(field: PasswordField): string | null {
    if (this.submitted()) {
      const key = newPasswordErrors(this.form)[field];
      if (key) return this.i18n.t(key);
    }
    const problem = this.passwordProblem();
    if (field === 'currentPassword' && problem?.code === 'auth.current_password_invalid') return describeProblem(this.i18n, problem);
    if (field === 'newPassword' && problem?.code === 'password.weak') return describeProblem(this.i18n, problem);
    return null;
  }

  protected bannerText(): string | null {
    const problem = this.passwordProblem();
    if (!problem || problem.code === 'auth.current_password_invalid' || problem.code === 'password.weak') return null;
    return describeProblem(this.i18n, problem);
  }

  protected async changePassword(): Promise<void> {
    this.submitted.set(true);
    this.passwordProblem.set(null);
    if (Object.keys(newPasswordErrors(this.form)).length > 0 || this.savingPassword()) return;
    this.savingPassword.set(true);
    try {
      await this.auth.changePassword(this.form.currentPassword, this.form.newPassword);
      this.form = { currentPassword: '', newPassword: '', confirmPassword: '' };
      this.submitted.set(false);
      this.toast.success(this.i18n.t('account.password.changed'));
    } catch (e) {
      this.passwordProblem.set(toApiProblem(e));
    } finally {
      this.savingPassword.set(false);
    }
  }

  protected async logoutEverywhere(): Promise<void> {
    if (this.signingOut()) return;
    this.signingOut.set(true);
    this.logoutError.set(null);
    try {
      await this.auth.logoutEverywhere();
    } catch (e) {
      this.confirmOpen.set(false);
      this.logoutError.set(e);
    } finally {
      this.signingOut.set(false);
    }
  }
}
