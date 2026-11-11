import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { I18nService } from '../../../core/i18n/i18n.service';
import { formatMoney, parseMoneyToMinor } from '../../../core/format/money';
import { UI } from '../../../shared/ui';
import { posErrorText } from '../pos-error';
import { PosNumpadComponent } from '../pos-numpad';
import { NumpadKey, minorToPlain, pressNumpadKey, sanitizeNumpadText } from '../pos.logic';
import { GiftCardApi, GiftCardLookup } from './gift-card-api';
import {
  GIFT_CARD_LIMITS,
  GIFT_CARD_QUICK_AMOUNTS,
  dateOnly,
  giftCardAmountError,
  giftCardPaymentAmount,
  maskedGiftCard,
  normalizeGiftCardCode,
  typingGiftCardCode,
} from './gift-card.logic';

/** "Gutschein verkaufen": amount by quick buttons, numpad or keyboard; checked against the API's limits. */
@Component({
  selector: 'hn-pos-gift-card-sale-dialog',
  imports: [PosNumpadComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-dialog [title]="'pos.giftCards.sell.title' | t" [open]="open()" testId="pos-gift-card-sale" (closed)="closed.emit()">
      @if (open()) {
        <form (submit)="$event.preventDefault(); add()" novalidate>
          <p class="mb-3 text-sm text-pos-muted">{{ 'pos.giftCards.sell.hint' | t: { min: min, max: max } }}</p>
          <div class="mb-3 grid grid-cols-3 gap-1.5 sm:grid-cols-5" role="group" [attr.aria-label]="'pos.giftCards.sell.quick' | t">
            @for (v of quick; track v) {
              <button type="button" class="pos-quick tabular-nums" [attr.aria-pressed]="amountMinor() === v" (click)="setAmount(v)" data-testid="pos-gc-quick">{{ v | money }}</button>
            }
          </div>
          <hn-field [label]="'pos.giftCards.sell.amount' | t" for="pos-gc-amount" [required]="true" [errors]="error()">
            <input hnInput id="pos-gc-amount" name="amount" inputmode="decimal" autocomplete="off" class="h-14 text-end text-2xl font-semibold tabular-nums" placeholder="0,00"
              [value]="text()" (input)="onText($any($event.target))" [attr.aria-invalid]="!!error()" data-testid="pos-gc-amount" />
          </hn-field>
          <hn-pos-numpad class="mt-3 block" variant="payment" [label]="'pos.numpad.label' | t" (key)="onKey($event)" (add)="setAmount((amountMinor() ?? 0) + $event)" />
          <div class="mt-4 flex justify-end gap-2">
            <button hnButton type="button" class="min-h-11" (click)="closed.emit()">{{ 'common.cancel' | t }}</button>
            <button hnButton variant="primary" type="submit" class="min-h-11" data-testid="pos-gc-add">{{ 'pos.giftCards.sell.add' | t }}</button>
          </div>
        </form>
      }
    </hn-dialog>
  `,
})
export class PosGiftCardSaleDialogComponent {
  readonly open = input(false);
  readonly closed = output<void>();
  readonly chosen = output<number>();

  private readonly i18n = inject(I18nService);
  protected readonly quick = GIFT_CARD_QUICK_AMOUNTS;
  protected readonly min = formatMoney(GIFT_CARD_LIMITS.minMinor);
  protected readonly max = formatMoney(GIFT_CARD_LIMITS.maxMinor);
  protected readonly text = signal('');
  protected readonly error = signal<string | null>(null);
  protected readonly amountMinor = computed(() => parseMoneyToMinor(this.text().replace(/,$/, '')));
  private fresh = true;

  constructor() {
    effect(() => {
      if (this.open())
        untracked(() => {
          this.text.set('');
          this.error.set(null);
          this.fresh = true;
        });
    });
  }

  protected setAmount(minor: number): void {
    this.text.set(minorToPlain(minor));
    this.fresh = true;
    this.error.set(null);
  }

  protected onText(el: HTMLInputElement): void {
    const clean = sanitizeNumpadText(el.value, 'amount');
    if (clean !== el.value) el.value = clean;
    this.text.set(clean);
    this.fresh = false;
    this.error.set(null);
  }

  protected onKey(key: NumpadKey): void {
    const r = pressNumpadKey({ buffer: this.text(), fresh: this.fresh }, key, 'amount');
    this.fresh = false;
    this.text.set(r.buffer);
    this.error.set(null);
  }

  protected add(): void {
    const minor = this.amountMinor();
    const e = giftCardAmountError(minor);
    if (e) {
      this.error.set(this.i18n.t('pos.giftCards.sell.errors.' + e, { min: this.min, max: this.max }));
      return;
    }
    this.chosen.emit(minor!);
  }
}

/** A gift card chosen for payment: what goes into the payment line. */
export interface GiftCardPaymentChoice {
  code: string;
  last4: string;
  balanceMinor: number;
  amountMinor: number;
}

/**
 * Gift card as a means of payment: scan or type the code → the API shows balance and validity → amount (default: what the
 * card still has, at most what is left to pay and may be paid by gift card).
 */
@Component({
  selector: 'hn-pos-gift-card-pay-dialog',
  imports: [...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-dialog [title]="'pos.giftCards.pay.title' | t" [open]="open()" testId="pos-gift-card-pay" (closed)="closed.emit()">
      @if (open()) {
        <form (submit)="$event.preventDefault(); card() ? use() : lookup()" novalidate>
          <hn-field [label]="'pos.giftCards.pay.code' | t" for="pos-gcp-code" [required]="true" [hint]="'pos.giftCards.pay.codeHint' | t" [errors]="codeError()">
            <input hnInput id="pos-gcp-code" name="code" dir="ltr" autocomplete="off" autocapitalize="characters" spellcheck="false" class="h-12 font-mono text-lg tracking-wider"
              placeholder="XXXX-XXXX-XXXX-XXXX" [value]="code()" (input)="onCode($any($event.target))" aria-describedby="pos-gcp-code-hint" data-testid="pos-gcp-code" />
          </hn-field>
          @if (card(); as c) {
            <dl class="mt-3 grid grid-cols-[1fr_auto] gap-x-4 gap-y-1 rounded-xl border border-pos-line bg-pos-surface p-3 text-sm" data-testid="pos-gcp-card">
              <dt>{{ 'pos.giftCards.pay.card' | t }}</dt>
              <dd class="text-end font-mono" dir="ltr">{{ masked(c.codeLast4) }}</dd>
              <dt>{{ 'pos.giftCards.pay.balance' | t }}</dt>
              <dd class="text-end text-lg font-bold tabular-nums" data-testid="pos-gcp-balance">{{ c.balanceMinor | money }}</dd>
              @if (c.expiresOn) {
                <dt>{{ 'pos.giftCards.slip.validUntil' | t }}</dt>
                <dd class="text-end">{{ date(c.expiresOn) }}</dd>
              }
            </dl>
            @if (!c.usable) {
              <p class="mt-2 rounded-[10px] bg-pos-danger-soft px-3 py-2 text-sm text-pos-ink" role="alert" data-testid="pos-gcp-unusable">
                {{ (c.status === 'Voided' ? 'pos.giftCards.pay.voided' : 'pos.giftCards.pay.expired') | t }}
              </p>
            } @else {
              <hn-field class="mt-3 block" [label]="'pos.giftCards.pay.amount' | t" for="pos-gcp-amount" [required]="true" [hint]="'pos.giftCards.pay.amountHint' | t: { max: (maxAmount() | money) }" [errors]="amountError()">
                <input hnInput id="pos-gcp-amount" name="amount" inputmode="decimal" autocomplete="off" class="h-12 text-end text-xl font-semibold tabular-nums"
                  [value]="amount()" (input)="amount.set($any($event.target).value); amountError.set(null)" aria-describedby="pos-gcp-amount-hint" data-testid="pos-gcp-amount" />
              </hn-field>
            }
          }
          <div class="mt-4 flex justify-end gap-2">
            <button hnButton type="button" class="min-h-11" (click)="closed.emit()">{{ 'common.cancel' | t }}</button>
            @if (card()?.usable) {
              <button hnButton variant="primary" type="submit" class="min-h-11" data-testid="pos-gcp-use">{{ 'pos.giftCards.pay.use' | t }}</button>
            } @else if (!card()) {
              <button hnButton variant="primary" type="submit" class="min-h-11" [loading]="busy()" [disabled]="busy()" data-testid="pos-gcp-lookup">{{ 'pos.giftCards.pay.check' | t }}</button>
            }
          </div>
        </form>
      }
    </hn-dialog>
  `,
})
export class PosGiftCardPayDialogComponent {
  readonly open = input(false);
  /** What may still be paid by gift card (remaining amount, without gift cards sold on this ticket). */
  readonly payableMinor = input(0);
  /** Codes already used as payment lines on this ticket. */
  readonly usedCodes = input<string[]>([]);
  readonly closed = output<void>();
  readonly chosen = output<GiftCardPaymentChoice>();

  private readonly api = inject(GiftCardApi);
  private readonly i18n = inject(I18nService);
  protected readonly masked = maskedGiftCard;
  protected readonly date = dateOnly;
  protected readonly code = signal('');
  protected readonly codeError = signal<string | null>(null);
  protected readonly card = signal<GiftCardLookup | null>(null);
  private normalized = '';
  protected readonly amount = signal('');
  protected readonly amountError = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly maxAmount = computed(() => giftCardPaymentAmount(this.card()?.balanceMinor ?? 0, this.payableMinor()));

  constructor() {
    effect(() => {
      if (this.open())
        untracked(() => {
          this.code.set('');
          this.codeError.set(null);
          this.card.set(null);
          this.amount.set('');
          this.amountError.set(null);
        });
    });
  }

  protected onCode(el: HTMLInputElement): void {
    const next = typingGiftCardCode(el.value);
    if (next !== el.value) el.value = next;
    this.code.set(next);
    this.codeError.set(null);
    this.card.set(null);
  }

  protected async lookup(): Promise<void> {
    const normalized = normalizeGiftCardCode(this.code());
    if (!normalized) return this.codeError.set(this.i18n.t('pos.giftCards.pay.codeInvalid'));
    if (this.usedCodes().includes(normalized)) return this.codeError.set(this.i18n.t('pos.giftCards.pay.alreadyUsed'));
    this.busy.set(true);
    try {
      const c = await firstValueFrom(this.api.lookup(normalized));
      this.normalized = normalized;
      this.card.set(c);
      this.amount.set(minorToPlain(giftCardPaymentAmount(c.balanceMinor, this.payableMinor())));
    } catch (e) {
      this.codeError.set(posErrorText(this.i18n, e));
    } finally {
      this.busy.set(false);
    }
  }

  protected use(): void {
    const c = this.card();
    if (!c?.usable) return;
    const minor = parseMoneyToMinor(this.amount());
    if (minor === null || minor <= 0) return this.amountError.set(this.i18n.t('pos.giftCards.pay.amountInvalid'));
    if (minor > c.balanceMinor) return this.amountError.set(this.i18n.t('pos.giftCards.pay.amountBalance', { balance: formatMoney(c.balanceMinor) }));
    if (minor > this.payableMinor()) return this.amountError.set(this.i18n.t('pos.giftCards.pay.amountTotal', { max: formatMoney(this.payableMinor()) }));
    this.chosen.emit({ code: this.normalized, last4: c.codeLast4, balanceMinor: c.balanceMinor, amountMinor: minor });
  }
}
