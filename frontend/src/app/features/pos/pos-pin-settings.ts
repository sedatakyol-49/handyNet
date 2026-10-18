import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { PosApi } from '../../core/api/handynet-api';
import { PosPinStatus } from '../../core/api/models';
import { I18nService } from '../../core/i18n/i18n.service';
import { ToastService } from '../../core/ui-state/toast.service';
import { UI } from '../../shared/ui';
import { posErrorText } from './pos-error';

/** Client-side check before the API (same rule: 4–6 digits; trivial PINs are refused by the API). */
export function pinFormatError(pin: string, confirm: string): 'format' | 'mismatch' | null {
  if (!/^[0-9]{4,6}$/.test(pin)) return 'format';
  return pin === confirm ? null : 'mismatch';
}

/** "Konto & Sicherheit": the member's own POS PIN for cashier switching and approvals (needs the account password). */
@Component({
  selector: 'hn-pos-pin-settings',
  imports: [...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    <section class="hn-card p-4" aria-labelledby="account-pos-pin" data-testid="account-pos-pin">
      <h2 id="account-pos-pin" class="text-base">{{ 'pos.pinSettings.title' | t }}</h2>
      <p class="mt-1 mb-3 text-sm text-slate-500">{{ 'pos.pinSettings.text' | t }}</p>
      <p class="mb-3 text-sm" data-testid="account-pos-pin-status">
        @if (status()?.lockedUntil) {
          <span class="font-semibold text-red-700">{{ 'pos.pinSettings.locked' | t }}</span>
        } @else {
          {{ (status()?.hasPin ? 'pos.pinSettings.isSet' : 'pos.pinSettings.notSet') | t }}
        }
      </p>
      <form class="space-y-3" (submit)="$event.preventDefault(); save(false)" novalidate>
        <hn-field [label]="'pos.pinSettings.pin' | t" for="own-pin" [hint]="'team.pin.hint' | t">
          <input hnInput id="own-pin" name="pin" type="password" inputmode="numeric" maxlength="6" autocomplete="off" class="w-40 tracking-widest"
            [value]="pin()" (input)="pin.set($any($event.target).value)" aria-describedby="own-pin-hint" data-testid="account-pin" />
        </hn-field>
        <hn-field [label]="'pos.pinSettings.confirm' | t" for="own-pin-confirm">
          <input hnInput id="own-pin-confirm" name="confirm" type="password" inputmode="numeric" maxlength="6" autocomplete="off" class="w-40 tracking-widest"
            [value]="confirm()" (input)="confirm.set($any($event.target).value)" data-testid="account-pin-confirm" />
        </hn-field>
        <hn-field [label]="'account.password.current' | t" for="own-pin-password" [errors]="error()">
          <input hnInput id="own-pin-password" name="currentPassword" type="password" autocomplete="current-password" [value]="password()"
            (input)="password.set($any($event.target).value)" data-testid="account-pin-password" />
        </hn-field>
        <div class="flex flex-wrap justify-end gap-2">
          @if (status()?.hasPin) {
            <button hnButton variant="ghost" type="button" class="me-auto" [disabled]="busy()" (click)="save(true)" data-testid="account-pin-clear">{{ 'team.pin.clear' | t }}</button>
          }
          <button hnButton variant="primary" type="submit" [loading]="busy()" [disabled]="busy()" data-testid="account-pin-save">{{ 'common.save' | t }}</button>
        </div>
      </form>
    </section>
  `,
})
export class PosPinSettingsComponent {
  private readonly api = inject(PosApi);
  private readonly i18n = inject(I18nService);
  private readonly toast = inject(ToastService);

  protected readonly status = signal<PosPinStatus | null>(null);
  protected readonly pin = signal('');
  protected readonly confirm = signal('');
  protected readonly password = signal('');
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);

  constructor() {
    void firstValueFrom(this.api.myPin()).then((s) => this.status.set(s), () => undefined);
  }

  protected async save(clear: boolean): Promise<void> {
    this.error.set(null);
    if (!clear) {
      const invalid = pinFormatError(this.pin(), this.confirm());
      if (invalid) {
        this.error.set(this.i18n.t(invalid === 'format' ? 'team.pin.invalid' : 'pos.pinSettings.mismatch'));
        return;
      }
    }
    if (!this.password()) {
      this.error.set(this.i18n.t('pos.pinSettings.passwordRequired'));
      return;
    }
    this.busy.set(true);
    try {
      this.status.set(await firstValueFrom(this.api.setMyPin(clear ? null : this.pin(), this.password())));
      this.pin.set('');
      this.confirm.set('');
      this.password.set('');
      this.toast.success(this.i18n.t(clear ? 'pos.pinSettings.cleared' : 'pos.pinSettings.saved'));
    } catch (e) {
      this.error.set(posErrorText(this.i18n, e));
    } finally {
      this.busy.set(false);
    }
  }
}
