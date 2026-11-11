import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { formatMoney, parseMoneyToMinor } from '../../../core/format/money';
import { I18nService } from '../../../core/i18n/i18n.service';
import { ToastService } from '../../../core/ui-state/toast.service';
import { UI } from '../../../shared/ui';
import { posErrorText } from '../pos-error';
import { PosSubnavComponent } from '../pos-subnav';
import { minorToPlain } from '../pos.logic';
import { LoyaltyApi, LoyaltySettings } from './loyalty-api';
import { SettingsError, loyaltySettingsInput, pointsEarned, redeemValueMinor } from './loyalty.logic';

/**
 * Kasse → Treuepunkte (`pos.manage`): customers with a customer record collect points per full euro paid and redeem
 * them as a price reduction at the till. Settings apply to the whole organization.
 */
@Component({
  selector: 'hn-pos-loyalty-settings-page',
  host: { class: 'pos-page block' },
  imports: [FormsModule, PosSubnavComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-pos-subnav [registerId]="registerId()" />
    <div class="mb-4">
      <h1 class="text-2xl font-extrabold tracking-tight">{{ 'pos.loyalty.settings.title' | t }}</h1>
      <p class="mt-1 max-w-3xl text-sm text-pos-muted">{{ 'pos.loyalty.settings.subtitle' | t }}</p>
    </div>
    <hn-banner class="mb-3" [text]="error()" />
    <hn-banner class="mb-3" tone="info" [text]="'pos.loyalty.settings.legal' | t" />
    @if (loaded()) {
      <form class="pos-card grid max-w-2xl gap-4 p-4" (ngSubmit)="save()" novalidate data-testid="loyalty-settings">
        <label class="flex min-h-11 items-start gap-2 text-sm">
          <input type="checkbox" class="hn-checkbox mt-1" name="enabled" [(ngModel)]="form.enabled" data-testid="loyalty-enabled" />
          <span><span class="font-medium">{{ 'pos.loyalty.settings.enabled' | t }}</span><span class="block text-xs text-pos-muted">{{ 'pos.loyalty.settings.enabledHint' | t }}</span></span>
        </label>
        <div class="grid gap-3 sm:grid-cols-3">
          <hn-field [label]="'pos.loyalty.settings.pointsPerEuro' | t" for="loy-ppe" [hint]="'pos.loyalty.settings.pointsPerEuroHint' | t" [errors]="err('pointsPerEuro')">
            <input hnInput id="loy-ppe" name="ppe" inputmode="numeric" autocomplete="off" class="h-11 text-end tabular-nums" [(ngModel)]="form.pointsPerEuro" aria-describedby="loy-ppe-hint" data-testid="loyalty-ppe" />
          </hn-field>
          <hn-field [label]="'pos.loyalty.settings.pointValue' | t" for="loy-value" [hint]="'pos.loyalty.settings.pointValueHint' | t" [errors]="err('pointValue')">
            <input hnInput id="loy-value" name="value" inputmode="decimal" autocomplete="off" class="h-11 text-end tabular-nums" [(ngModel)]="form.pointValue" aria-describedby="loy-value-hint" data-testid="loyalty-value" />
          </hn-field>
          <hn-field [label]="'pos.loyalty.settings.minRedeem' | t" for="loy-min" [hint]="'pos.loyalty.settings.minRedeemHint' | t" [errors]="err('minRedeem')">
            <input hnInput id="loy-min" name="min" inputmode="numeric" autocomplete="off" class="h-11 text-end tabular-nums" [(ngModel)]="form.minRedeemPoints" aria-describedby="loy-min-hint" data-testid="loyalty-min" />
          </hn-field>
        </div>
        @if (example(); as ex) {
          <p class="rounded-[10px] bg-pos-surface px-3 py-2 text-sm" data-testid="loyalty-example">{{ ex }}</p>
        }
        <div class="flex justify-end">
          <button hnButton variant="primary" type="submit" class="min-h-11" [loading]="saving()" [disabled]="saving()" data-testid="loyalty-save">{{ 'common.save' | t }}</button>
        </div>
      </form>
    }
  `,
})
export class PosLoyaltySettingsPage {
  readonly registerId = input.required<string>();
  private readonly api = inject(LoyaltyApi);
  private readonly i18n = inject(I18nService);
  private readonly toast = inject(ToastService);

  protected form = { enabled: false, pointsPerEuro: '1', pointValue: '0,01', minRedeemPoints: '0' };
  protected readonly loaded = signal(false);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly errors = signal<SettingsError[]>([]);
  /** "Bei 100,00 € Einkauf: 100 Punkte; 100 Punkte sind 1,00 € wert." (re-read on every change of the form). */
  protected example(): string | null {
    const r = loyaltySettingsInput(this.form, (s) => parseMoneyToMinor(s));
    if (!r.ok) return null;
    const earned = pointsEarned(10_000, r.value.pointsPerEuro);
    return this.i18n.t('pos.loyalty.settings.example', { points: earned, value: formatMoney(redeemValueMinor(earned, r.value.pointValueMinor)) });
  }

  ngOnInit(): void {
    void firstValueFrom(this.api.settings()).then(
      (s) => this.apply(s),
      (e) => this.error.set(posErrorText(this.i18n, e)),
    ).finally(() => this.loaded.set(true));
  }

  private apply(s: LoyaltySettings): void {
    this.form = { enabled: s.enabled, pointsPerEuro: String(s.pointsPerEuro), pointValue: minorToPlain(s.pointValueMinor), minRedeemPoints: String(s.minRedeemPoints) };
  }

  protected err(key: SettingsError): string | null {
    return this.errors().includes(key) ? this.i18n.t('pos.loyalty.settings.errors.' + key) : null;
  }

  protected async save(): Promise<void> {
    const r = loyaltySettingsInput(this.form, (s) => parseMoneyToMinor(s));
    if (!r.ok) return this.errors.set(r.errors);
    this.errors.set([]);
    this.saving.set(true);
    this.error.set(null);
    try {
      this.apply(await firstValueFrom(this.api.saveSettings(r.value)));
      this.toast.success(this.i18n.t('pos.loyalty.settings.saved'));
    } catch (e) {
      this.error.set(posErrorText(this.i18n, e));
    } finally {
      this.saving.set(false);
    }
  }
}
