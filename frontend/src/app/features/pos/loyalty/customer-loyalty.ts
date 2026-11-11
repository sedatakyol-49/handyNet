import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { I18nService } from '../../../core/i18n/i18n.service';
import { UI } from '../../../shared/ui';
import { posErrorText } from '../pos-error';
import { CustomerLoyalty, LoyaltyApi, LoyaltyTransaction } from './loyalty-api';
import { LOYALTY_LIMITS, parseAdjustment } from './loyalty.logic';

const PAGE_SIZE = 20;

/**
 * Points of one customer: balance, history (newest first, with the receipt that earned or redeemed them) and, for
 * managers, a manual adjustment with a reason (`POST /api/pos/customers/{id}/loyalty/adjustments`).
 */
@Component({
  selector: 'hn-pos-customer-loyalty',
  imports: [FormsModule, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    <section class="rounded-xl border border-slate-200 p-3" aria-labelledby="pos-cl-heading" data-testid="pos-customer-loyalty">
      <div class="flex flex-wrap items-baseline justify-between gap-2">
        <h3 id="pos-cl-heading" class="text-sm font-bold">{{ 'pos.loyalty.history.title' | t }}</h3>
        @if (data(); as d) {
          <p class="text-sm">{{ 'pos.loyalty.redeem.balance' | t }} <strong class="text-lg tabular-nums" data-testid="pos-cl-points">{{ d.points }}</strong></p>
        }
      </div>
      <hn-banner class="mt-2" [text]="error()" />
      @if (data() && !data()!.enabled) {
        <p class="mt-2 text-xs text-slate-500">{{ 'pos.loyalty.history.disabled' | t }}</p>
      }
      @if (items().length) {
        <ul class="mt-2 max-h-64 divide-y divide-slate-100 overflow-y-auto text-sm" data-testid="pos-cl-list">
          @for (tx of items(); track tx.id) {
            <li class="flex flex-wrap items-baseline justify-between gap-x-3 py-1.5" data-testid="pos-cl-row">
              <span class="min-w-0">
                <span class="font-medium">{{ tx.type | tEnum: 'loyaltyTransactionType' }}</span>
                @if (tx.receiptNumber) {
                  <span class="text-slate-500"> · {{ 'pos.loyalty.history.receipt' | t: { number: tx.receiptNumber } }}</span>
                }
                @if (tx.reason) {
                  <span class="block text-xs text-slate-500">{{ tx.reason }}</span>
                }
                <span class="block text-xs text-slate-500">{{ tx.createdAt | hnDate }}{{ tx.createdByName ? ' · ' + tx.createdByName : '' }}</span>
              </span>
              <span class="text-end tabular-nums">
                <bdi dir="ltr" class="font-semibold" [class.text-red-700]="tx.points < 0">{{ tx.points > 0 ? '+' : '' }}{{ tx.points }}</bdi>
                <span class="block text-xs text-slate-500">= {{ tx.balanceAfter }}</span>
              </span>
            </li>
          }
        </ul>
      } @else if (data()) {
        <p class="mt-2 text-sm text-slate-500" data-testid="pos-cl-empty">{{ 'pos.loyalty.history.empty' | t }}</p>
      }
      @if (items().length < total()) {
        <button hnButton type="button" class="mt-2 min-h-11" [loading]="loading()" (click)="load(page + 1)">{{ 'pos.sell.loadMore' | t: { shown: items().length, total: total() } }}</button>
      }
      @if (canAdjust()) {
        <form class="mt-3 grid gap-2 border-t border-slate-100 pt-3 sm:grid-cols-[8rem_minmax(0,1fr)_auto] sm:items-end" (submit)="$event.preventDefault(); adjust()" novalidate>
          <hn-field [label]="'pos.loyalty.history.adjustPoints' | t" for="pos-cl-adjust" [hint]="'pos.loyalty.history.adjustHint' | t" [errors]="pointsError()">
            <input hnInput id="pos-cl-adjust" name="points" dir="ltr" inputmode="numeric" autocomplete="off" class="h-11 text-end tabular-nums" [(ngModel)]="pointsText"
              aria-describedby="pos-cl-adjust-hint" data-testid="pos-cl-adjust-points" />
          </hn-field>
          <hn-field [label]="'pos.loyalty.history.adjustReason' | t" for="pos-cl-reason" [required]="true" [errors]="reasonError()">
            <input hnInput id="pos-cl-reason" name="reason" autocomplete="off" class="h-11" [maxlength]="maxReason" [(ngModel)]="reason" data-testid="pos-cl-adjust-reason" />
          </hn-field>
          <button hnButton type="submit" class="min-h-11" [loading]="busy()" [disabled]="busy()" data-testid="pos-cl-adjust-save">{{ 'pos.loyalty.history.adjust' | t }}</button>
        </form>
      }
    </section>
  `,
})
export class PosCustomerLoyaltyComponent {
  readonly customerId = input.required<string>();
  readonly canAdjust = input(false);
  /** New balance after an adjustment (the caller updates the ticket's customer). */
  readonly changed = output<number>();

  private readonly api = inject(LoyaltyApi);
  private readonly i18n = inject(I18nService);
  protected readonly maxReason = LOYALTY_LIMITS.reason;
  protected readonly data = signal<CustomerLoyalty | null>(null);
  protected readonly items = signal<LoyaltyTransaction[]>([]);
  protected readonly total = computed(() => this.data()?.transactions.totalCount ?? 0);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected page = 1;
  protected pointsText = '';
  protected reason = '';
  protected readonly pointsError = signal<string | null>(null);
  protected readonly reasonError = signal<string | null>(null);
  protected readonly busy = signal(false);

  constructor() {
    effect(() => {
      const id = this.customerId();
      untracked(() => {
        this.items.set([]);
        void this.load(1, id);
      });
    });
  }

  async load(page: number, id = this.customerId()): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const d = await firstValueFrom(this.api.customer(id, { page, pageSize: PAGE_SIZE }));
      this.data.set(d);
      this.page = page;
      this.items.update((list) => (page === 1 ? d.transactions.items : [...list, ...d.transactions.items.filter((t) => !list.some((x) => x.id === t.id))]));
    } catch (e) {
      this.error.set(posErrorText(this.i18n, e));
    } finally {
      this.loading.set(false);
    }
  }

  protected async adjust(): Promise<void> {
    const points = parseAdjustment(this.pointsText);
    const reason = this.reason.trim();
    this.pointsError.set(points === null ? this.i18n.t('pos.loyalty.history.adjustInvalid') : null);
    this.reasonError.set(reason ? null : this.i18n.t('pos.loyalty.history.reasonRequired'));
    if (points === null || !reason) return;
    this.busy.set(true);
    try {
      const d = await firstValueFrom(this.api.adjust(this.customerId(), points, reason));
      this.data.set(d);
      this.items.set(d.transactions.items);
      this.page = 1;
      this.pointsText = '';
      this.reason = '';
      this.changed.emit(d.points);
    } catch (e) {
      this.error.set(posErrorText(this.i18n, e));
    } finally {
      this.busy.set(false);
    }
  }
}
