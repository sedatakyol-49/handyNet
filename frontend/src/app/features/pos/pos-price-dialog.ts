import { ChangeDetectionStrategy, Component, effect, inject, input, output, signal, untracked } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { PosApi } from '../../core/api/handynet-api';
import { PosPrice } from '../../core/api/models';
import { I18nService } from '../../core/i18n/i18n.service';
import { ToastService } from '../../core/ui-state/toast.service';
import { UI } from '../../shared/ui';
import { posErrorText } from './pos-error';
import { minorToPlain, parseAmount } from './pos.logic';

export interface PriceTarget {
  productId: string;
  brand: string;
  name: string;
}

/**
 * Maintains the organization's own POS price of one product (`pos.manage`): shows the till price and where it comes
 * from (own POS price, own marketplace offer net + 19 % VAT, none), sets or clears the POS price.
 */
@Component({
  selector: 'hn-pos-price-dialog',
  imports: [...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-dialog [title]="'pos.price.title' | t" [open]="!!product()" (closed)="close()">
      @if (product(); as p) {
        <p class="mb-3 text-sm font-semibold" data-testid="pos-price-product">{{ p.brand }} {{ p.name }}</p>
        @if (loadError()) {
          <hn-banner class="mb-3" [error]="loadError()" />
        }
        @if (current(); as c) {
          <dl class="mb-4 grid grid-cols-[1fr_auto] gap-x-4 gap-y-1 text-sm" data-testid="pos-price-current">
            <dt>{{ 'pos.price.tillPrice' | t }}</dt>
            <dd class="text-end font-semibold tabular-nums">{{ c.priceMinor === null ? ('pos.sell.noPrice' | t) : (c.priceMinor | money) }}</dd>
            <dt>{{ 'pos.price.source' | t }}</dt>
            <dd class="text-end">{{ 'pos.price.sources.' + c.priceSource | t }}</dd>
            @if (c.offerNetPriceMinor !== null) {
              <dt>{{ 'pos.price.offerNet' | t }}</dt>
              <dd class="text-end tabular-nums">{{ c.offerNetPriceMinor | money }}</dd>
            }
            @if (c.updatedAt) {
              <dt>{{ 'pos.price.updatedAt' | t }}</dt>
              <dd class="text-end text-xs">{{ c.updatedAt | hnDate }}</dd>
            }
          </dl>
        }
        <form (submit)="$event.preventDefault(); save()" novalidate>
          <hn-field [label]="'pos.price.posPrice' | t" for="pos-price-input" [hint]="'pos.price.hint' | t" [errors]="error()">
            <input hnInput id="pos-price-input" name="price" inputmode="decimal" autocomplete="off" class="h-12 text-end text-lg tabular-nums" placeholder="0,00"
              [value]="input()" (input)="input.set($any($event.target).value); error.set(null)" [attr.aria-invalid]="!!error()"
              [attr.aria-describedby]="error() ? 'pos-price-input-error' : 'pos-price-input-hint'" data-testid="pos-price-input" />
          </hn-field>
          <div class="mt-4 flex flex-wrap justify-end gap-2">
            @if (current()?.posPriceMinor !== null && current()?.posPriceMinor !== undefined) {
              <button hnButton variant="ghost" type="button" class="me-auto min-h-11" [disabled]="busy()" (click)="clear()" data-testid="pos-price-clear">{{ 'pos.price.clear' | t }}</button>
            }
            <button hnButton type="button" class="min-h-11" (click)="close()">{{ 'common.cancel' | t }}</button>
            <button hnButton variant="primary" type="submit" class="min-h-11" [loading]="busy()" [disabled]="busy()" data-testid="pos-price-save">{{ 'common.save' | t }}</button>
          </div>
        </form>
      }
    </hn-dialog>
  `,
})
export class PosPriceDialogComponent {
  private readonly api = inject(PosApi);
  private readonly i18n = inject(I18nService);
  private readonly toast = inject(ToastService);

  /** Product to edit; null = closed. */
  readonly product = input<PriceTarget | null>(null);
  readonly registerId = input<string | null>(null);
  readonly closed = output<void>();
  readonly saved = output<PosPrice>();

  protected readonly current = signal<PosPrice | null>(null);
  protected readonly loadError = signal<unknown>(null);
  protected readonly input = signal('');
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);

  constructor() {
    effect(() => {
      const p = this.product();
      untracked(() => {
        this.current.set(null);
        this.loadError.set(null);
        this.error.set(null);
        this.input.set('');
        if (p) void this.load(p.productId);
      });
    });
  }

  private async load(productId: string): Promise<void> {
    try {
      const price = await firstValueFrom(this.api.price(productId, this.registerId()));
      if (this.product()?.productId !== productId) return;
      this.current.set(price);
      this.input.set(price.posPriceMinor !== null ? minorToPlain(price.posPriceMinor) : '');
    } catch (e) {
      this.loadError.set(e);
    }
  }

  protected close(): void {
    if (!this.busy()) this.closed.emit();
  }

  protected save(): Promise<void> {
    const minor = parseAmount(this.input(), null);
    if (minor === null || minor <= 0) {
      this.error.set(this.i18n.t('pos.price.invalid'));
      return Promise.resolve();
    }
    return this.put(minor);
  }

  protected clear(): Promise<void> {
    return this.put(null);
  }

  private async put(priceMinor: number | null): Promise<void> {
    const p = this.product();
    if (!p) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      const saved = await firstValueFrom(this.api.setPrice(p.productId, priceMinor));
      this.toast.success(this.i18n.t(priceMinor === null ? 'pos.price.cleared' : 'pos.price.saved'));
      this.saved.emit(saved);
      this.closed.emit();
    } catch (e) {
      this.error.set(posErrorText(this.i18n, e));
    } finally {
      this.busy.set(false);
    }
  }
}
