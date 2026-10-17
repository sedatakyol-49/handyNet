import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { UI } from '../../shared/ui';
import { AuthLayoutComponent } from './auth-layout';

@Component({
  selector: 'hn-login-page',
  imports: [FormsModule, RouterLink, AuthLayoutComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-auth-layout>
      <div class="hn-card p-6">
        <h1 class="text-lg">{{ 'auth.login.title' | t }}</h1>
        <p class="mt-1 mb-5 text-sm text-slate-500">{{ 'auth.login.subtitle' | t }}</p>

        @if (expired) {
          <hn-banner class="mb-4" tone="info" [text]="'auth.login.sessionExpired' | t" />
        }
        @if (registered) {
          <hn-banner class="mb-4" tone="success" [text]="'auth.login.registered' | t" />
        }
        @if (passwordReset) {
          <hn-banner class="mb-4" tone="success" [text]="'auth.login.passwordReset' | t" />
        }
        @if (signedOutEverywhere) {
          <hn-banner class="mb-4" tone="info" [text]="'auth.login.signedOutEverywhere' | t" />
        }
        <hn-banner class="mb-4" [error]="error()" />

        <form class="space-y-4" (ngSubmit)="submit()" #f="ngForm" novalidate>
          <hn-field [label]="'auth.email' | t" for="login-email" [required]="true">
            <input hnInput id="login-email" name="email" type="email" autocomplete="username" required [(ngModel)]="email" />
          </hn-field>
          <hn-field [label]="'auth.password' | t" for="login-password" [required]="true">
            <input hnInput id="login-password" name="password" type="password" autocomplete="current-password" required [(ngModel)]="password" />
          </hn-field>
          <p class="-mt-2 text-end text-sm">
            <a routerLink="/passwort-vergessen" class="hn-link" data-testid="forgot-password">{{ 'auth.login.forgotPassword' | t }}</a>
          </p>
          <button hnButton variant="primary" type="submit" class="w-full" [loading]="busy()" [disabled]="busy() || !email || !password">
            {{ 'auth.login.submit' | t }}
          </button>
        </form>
      </div>
      <p class="mt-4 text-center text-sm text-slate-600">
        {{ 'auth.login.noAccount' | t }}
        <a routerLink="/registrierung" class="hn-link">{{ 'auth.login.registerLink' | t }}</a>
      </p>
    </hn-auth-layout>
  `,
})
export class LoginPage {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected email = '';
  protected password = '';
  protected readonly busy = signal(false);
  protected readonly error = signal<unknown>(null);
  protected readonly expired = this.route.snapshot.queryParamMap.has('expired');
  protected readonly registered = this.route.snapshot.queryParamMap.has('registered');
  protected readonly passwordReset = this.route.snapshot.queryParamMap.has('reset');
  protected readonly signedOutEverywhere = this.route.snapshot.queryParamMap.get('signedOut') === 'all';

  protected async submit(): Promise<void> {
    if (!this.email || !this.password || this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      await this.auth.login(this.email.trim(), this.password);
      const returnUrl = this.route.snapshot.queryParamMap.get('returnUrl');
      await this.router.navigateByUrl(returnUrl && returnUrl.startsWith('/app') ? returnUrl : '/app');
    } catch (e) {
      this.error.set(e);
    } finally {
      this.busy.set(false);
    }
  }
}
