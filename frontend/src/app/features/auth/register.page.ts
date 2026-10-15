import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { AbstractControl, FormBuilder, ReactiveFormsModule, ValidationErrors, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ApiProblem, fieldErrors, toApiProblem } from '../../core/api/api-problem';
import { AuthService } from '../../core/auth/auth.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { UI } from '../../shared/ui';
import { AuthLayoutComponent } from './auth-layout';

/** Mirrors the server password policy: min. 10 characters, letters plus a digit or symbol. */
export function passwordPolicy(control: AbstractControl): ValidationErrors | null {
  const v = String(control.value ?? '');
  if (!v) return null;
  return v.length >= 10 && /\p{L}/u.test(v) && /[^\p{L}]/u.test(v) ? null : { passwordPolicy: true };
}

function atLeastOneType(group: AbstractControl): ValidationErrors | null {
  const v = group.value as { isWholesaler: boolean; isRetailer: boolean; isRepairProvider: boolean };
  return v.isWholesaler || v.isRetailer || v.isRepairProvider ? null : { businessType: true };
}

@Component({
  selector: 'hn-register-page',
  imports: [ReactiveFormsModule, RouterLink, AuthLayoutComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './register.page.html',
})
export class RegisterPage {
  private readonly fb = inject(FormBuilder).nonNullable;
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly i18n = inject(I18nService);

  protected readonly busy = signal(false);
  protected readonly submitted = signal(false);
  protected readonly problem = signal<ApiProblem | null>(null);

  protected readonly form = this.fb.group({
    email: ['', [Validators.required, Validators.email, Validators.maxLength(320)]],
    displayName: ['', [Validators.required, Validators.maxLength(200)]],
    password: ['', [Validators.required, passwordPolicy]],
    organization: this.fb.group(
      {
        name: ['', [Validators.required, Validators.maxLength(200)]],
        legalName: ['', [Validators.required, Validators.maxLength(300)]],
        vatId: ['', [Validators.pattern(/^DE\s?\d{9}$/)]],
        street: ['', [Validators.required, Validators.maxLength(200)]],
        postalCode: ['', [Validators.required, Validators.pattern(/^\d{5}$/)]],
        city: ['', [Validators.required, Validators.maxLength(100)]],
        countryCode: ['DE'],
        isWholesaler: [false],
        isRetailer: [false],
        isRepairProvider: [false],
      },
      { validators: atLeastOneType },
    ),
  });

  /** Client-side message first, then server-side 422 messages for the same field. */
  protected errorsFor(path: string): string[] {
    const control = this.form.get(path);
    const messages: string[] = [];
    if (control && control.invalid && (control.touched || this.submitted())) {
      const e = control.errors ?? {};
      if (e['required']) messages.push(this.i18n.t('validation.required'));
      else if (e['email']) messages.push(this.i18n.t('validation.email'));
      else if (e['passwordPolicy']) messages.push(this.i18n.t('validation.passwordPolicy'));
      else if (e['pattern'] && path.endsWith('postalCode')) messages.push(this.i18n.t('validation.postalCode'));
      else if (e['pattern'] && path.endsWith('vatId')) messages.push(this.i18n.t('validation.vatId'));
      else if (e['maxlength']) messages.push(this.i18n.t('validation.maxLength', { max: e['maxlength'].requiredLength }));
    }
    return [...messages, ...fieldErrors(this.problem(), path)];
  }

  protected invalid(path: string): boolean {
    return this.errorsFor(path).length > 0;
  }

  protected typeError(): string | null {
    const group = this.form.controls.organization;
    if (group.hasError('businessType') && this.submitted()) return this.i18n.t('validation.businessType');
    const server = fieldErrors(this.problem(), 'organization');
    return server.length ? server.join(' ') : null;
  }

  protected async submit(): Promise<void> {
    this.submitted.set(true);
    this.form.markAllAsTouched();
    if (this.form.invalid || this.busy()) return;
    this.busy.set(true);
    this.problem.set(null);
    const v = this.form.getRawValue();
    try {
      await this.auth.register({
        email: v.email.trim(),
        displayName: v.displayName.trim(),
        password: v.password,
        organization: { ...v.organization, vatId: v.organization.vatId.trim() || null },
      });
      await this.router.navigate(['/login'], { queryParams: { registered: 1 } });
    } catch (e) {
      this.problem.set(toApiProblem(e));
    } finally {
      this.busy.set(false);
    }
  }
}
