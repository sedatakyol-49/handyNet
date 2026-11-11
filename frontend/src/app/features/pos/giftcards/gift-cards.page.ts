import { ChangeDetectionStrategy, Component, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { I18nService } from '../../../core/i18n/i18n.service';
import { ToastService } from '../../../core/ui-state/toast.service';
import { UI } from '../../../shared/ui';
import { posErrorText } from '../pos-error';
import { PosSubnavComponent } from '../pos-subnav';
import { GiftCardSlipsComponent } from './gift-card-slips';
import { GiftCard, GiftCardApi, GiftCardStatus } from './gift-card-api';
import { IssuedGiftCard, dateOnly, maskedGiftCard } from './gift-card.logic';

const PAGE_SIZE = 50;

/**
 * Kasse → Gutscheine (`pos.manage`): every gift card of the organization with balance and status, the history of a card
 * (issue, redemptions, restores, void, reissue), voiding a card and reissuing it under a new code (same balance; the old
 * code stops working). The new code is shown once, like at the sale.
 */
@Component({
  selector: 'hn-pos-gift-cards-page',
  host: { class: 'pos-page block' },
  imports: [FormsModule, RouterLink, PosSubnavComponent, GiftCardSlipsComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-pos-subnav [registerId]="registerId()" />
    <div class="mb-4">
      <h1 class="text-2xl font-extrabold tracking-tight">{{ 'pos.giftCards.title' | t }}</h1>
      <p class="mt-1 max-w-3xl text-sm text-pos-muted">{{ 'pos.giftCards.subtitle' | t }}</p>
    </div>
    <div class="mb-3 flex flex-wrap items-end gap-2" role="search">
      <hn-field class="min-w-0 flex-1 basis-56" [label]="'pos.giftCards.search' | t" for="gc-search">
        <input hnInput id="gc-search" type="search" class="h-11" autocomplete="off" [placeholder]="'pos.giftCards.searchPlaceholder' | t" [value]="search()" (input)="search.set($any($event.target).value)" data-testid="gc-search" />
      </hn-field>
      <hn-field [label]="'pos.giftCards.status' | t" for="gc-status">
        <select hnSelect id="gc-status" class="min-h-11" [value]="status()" (change)="status.set($any($event.target).value)" data-testid="gc-status">
          <option value="">{{ 'common.all' | t }}</option>
          <option value="Active">{{ 'Active' | tEnum: 'giftCardStatus' }}</option>
          <option value="Voided">{{ 'Voided' | tEnum: 'giftCardStatus' }}</option>
        </select>
      </hn-field>
    </div>
    <hn-banner class="mb-3" [text]="error()" />

    @if (loaded() && !cards().length) {
      <div class="pos-card"><hn-empty-state [title]="'pos.giftCards.emptyTitle' | t" [text]="'pos.giftCards.emptyText' | t" /></div>
    } @else {
      <ul class="pos-card divide-y divide-pos-line" data-testid="gc-list">
        @for (c of cards(); track c.id) {
          <li class="flex flex-wrap items-center gap-x-4 gap-y-2 px-4 py-3" [attr.data-card]="c.codeLast4" data-testid="gc-row">
            <div class="min-w-0 flex-1">
              <p class="font-mono font-bold" dir="ltr">{{ masked(c.codeLast4) }}</p>
              <p class="text-xs text-pos-muted">
                {{ 'pos.giftCards.issued' | t: { date: (c.issuedAt | hnDate: 'date') } }}
                @if (c.issuedReceiptNumber) {
                  · {{ 'pos.loyalty.history.receipt' | t: { number: c.issuedReceiptNumber } }}
                }
                @if (c.expiresOn) {
                  · {{ 'pos.giftCards.slip.validUntil' | t }} {{ date(c.expiresOn) }}
                }
                @if (c.customerName) {
                  · {{ c.customerName }}
                }
              </p>
            </div>
            <span class="pos-pill" [class]="statusClass(c)" data-testid="gc-state">{{ statusText(c) }}</span>
            <div class="text-end">
              <p class="text-lg font-bold tabular-nums" data-testid="gc-balance">{{ c.balanceMinor | money }}</p>
              <p class="text-xs text-pos-muted">{{ 'pos.giftCards.of' | t: { amount: (c.initialMinor | money) } }}</p>
            </div>
            <button type="button" class="pos-soft" (click)="openDetail(c)" data-testid="gc-open">{{ 'pos.giftCards.details' | t }}</button>
          </li>
        }
      </ul>
      @if (cards().length < total()) {
        <button hnButton type="button" class="mt-3 min-h-11" (click)="load(page + 1)">{{ 'pos.sell.loadMore' | t: { shown: cards().length, total: total() } }}</button>
      }
    }

    <hn-dialog [title]="'pos.giftCards.detailTitle' | t: { code: masked(detail()?.codeLast4) }" [open]="!!detail()" [wide]="true" testId="gc-detail" (closed)="closeDetail()">
      @if (detail(); as c) {
        @if (reissued(); as r) {
          <hn-gift-card-slips class="mb-3" [cards]="r" [registerId]="registerId()" />
        }
        <dl class="grid grid-cols-2 gap-2 text-sm sm:grid-cols-4">
          <div class="rounded-[10px] bg-pos-surface px-3 py-2"><dt class="text-xs text-pos-muted">{{ 'pos.giftCards.balance' | t }}</dt><dd class="font-semibold tabular-nums" data-testid="gc-detail-balance">{{ c.balanceMinor | money }}</dd></div>
          <div class="rounded-[10px] bg-pos-surface px-3 py-2"><dt class="text-xs text-pos-muted">{{ 'pos.giftCards.initial' | t }}</dt><dd class="font-semibold tabular-nums">{{ c.initialMinor | money }}</dd></div>
          <div class="rounded-[10px] bg-pos-surface px-3 py-2"><dt class="text-xs text-pos-muted">{{ 'pos.giftCards.status' | t }}</dt><dd class="font-semibold">{{ statusText(c) }}</dd></div>
          <div class="rounded-[10px] bg-pos-surface px-3 py-2"><dt class="text-xs text-pos-muted">{{ 'pos.giftCards.slip.validUntil' | t }}</dt><dd class="font-semibold">{{ date(c.expiresOn) || '–' }}</dd></div>
        </dl>
        <h3 class="mt-4 mb-1 text-sm font-bold">{{ 'pos.giftCards.history' | t }}</h3>
        <ul class="divide-y divide-slate-100 text-sm" data-testid="gc-transactions">
          @for (tx of c.transactions ?? []; track tx.id) {
            <li class="flex flex-wrap items-baseline justify-between gap-x-3 py-1.5" data-testid="gc-tx">
              <span class="min-w-0">
                <span class="font-medium">{{ tx.type | tEnum: 'giftCardTransactionType' }}</span>
                @if (tx.receiptId && tx.receiptNumber) {
                  · <a class="underline" [routerLink]="['/kasse/belege', tx.receiptId]">{{ 'pos.loyalty.history.receipt' | t: { number: tx.receiptNumber } }}</a>
                }
                @if (tx.reason) {
                  <span class="block text-xs text-slate-500">{{ tx.reason }}</span>
                }
                <span class="block text-xs text-slate-500">{{ tx.createdAt | hnDate }}{{ tx.createdByName ? ' · ' + tx.createdByName : '' }}</span>
              </span>
              <span class="text-end tabular-nums">
                <bdi dir="ltr" class="font-semibold">{{ tx.amountMinor | signedMoney }}</bdi>
                <span class="block text-xs text-slate-500">= {{ tx.balanceAfterMinor | money }}</span>
              </span>
            </li>
          }
        </ul>
        @if (c.status === 'Active') {
          <form class="mt-4 grid gap-2 border-t border-slate-100 pt-3" (submit)="$event.preventDefault()" novalidate>
            <hn-field [label]="'pos.giftCards.reason' | t" for="gc-reason" [required]="true" [hint]="'pos.giftCards.reasonHint' | t" [errors]="reasonError()">
              <input hnInput id="gc-reason" name="reason" autocomplete="off" maxlength="500" class="h-11" [(ngModel)]="reason" aria-describedby="gc-reason-hint" data-testid="gc-reason" />
            </hn-field>
            <hn-banner [text]="actionError()" />
            <div class="flex flex-wrap justify-end gap-2">
              <button hnButton type="button" class="min-h-11" [loading]="busy() === 'reissue'" [disabled]="!!busy()" (click)="act('reissue')" data-testid="gc-reissue">{{ 'pos.giftCards.reissue' | t }}</button>
              <button hnButton variant="danger" type="button" class="min-h-11" [loading]="busy() === 'void'" [disabled]="!!busy()" (click)="act('void')" data-testid="gc-void">{{ 'pos.giftCards.void' | t }}</button>
            </div>
          </form>
        }
      }
    </hn-dialog>
  `,
})
export class PosGiftCardsPage {
  readonly registerId = input.required<string>();
  private readonly api = inject(GiftCardApi);
  private readonly i18n = inject(I18nService);
  private readonly toast = inject(ToastService);

  protected readonly masked = maskedGiftCard;
  protected readonly date = dateOnly;
  protected readonly search = signal('');
  protected readonly status = signal<'' | GiftCardStatus>('');
  protected readonly cards = signal<GiftCard[]>([]);
  protected readonly total = signal(0);
  protected readonly loaded = signal(false);
  protected readonly error = signal<string | null>(null);
  protected page = 1;
  protected readonly detail = signal<GiftCard | null>(null);
  protected readonly reissued = signal<IssuedGiftCard[] | null>(null);
  protected reason = '';
  protected readonly reasonError = signal<string | null>(null);
  protected readonly actionError = signal<string | null>(null);
  protected readonly busy = signal<'void' | 'reissue' | null>(null);

  constructor() {
    effect((onCleanup) => {
      this.search();
      this.status();
      const t = setTimeout(() => untracked(() => void this.load(1)), 250);
      onCleanup(() => clearTimeout(t));
    });
  }

  protected statusText(c: GiftCard): string {
    if (c.status === 'Voided') return this.i18n.enum('giftCardStatus', 'Voided');
    if (c.expired) return this.i18n.t('pos.giftCards.expired');
    return c.balanceMinor ? this.i18n.enum('giftCardStatus', 'Active') : this.i18n.t('pos.giftCards.used');
  }

  protected statusClass(c: GiftCard): string {
    return c.status === 'Active' && !c.expired && c.balanceMinor ? 'bg-pos-ok-soft text-pos-ok-text' : 'bg-pos-key text-pos-muted';
  }

  async load(page: number): Promise<void> {
    try {
      const r = await firstValueFrom(this.api.list({ search: this.search().trim() || null, status: this.status() || null, page, pageSize: PAGE_SIZE }));
      this.cards.update((list) => (page === 1 ? r.items : [...list, ...r.items]));
      this.total.set(r.totalCount);
      this.page = page;
      this.error.set(null);
    } catch (e) {
      this.error.set(posErrorText(this.i18n, e));
    } finally {
      this.loaded.set(true);
    }
  }

  protected async openDetail(c: GiftCard): Promise<void> {
    this.reason = '';
    this.reasonError.set(null);
    this.actionError.set(null);
    this.reissued.set(null);
    this.detail.set(c);
    try {
      this.detail.set(await firstValueFrom(this.api.get(c.id)));
    } catch (e) {
      this.actionError.set(posErrorText(this.i18n, e));
    }
  }

  protected closeDetail(): void {
    if (this.busy()) return;
    this.detail.set(null);
    this.reissued.set(null); // the new code is not kept
  }

  protected async act(kind: 'void' | 'reissue'): Promise<void> {
    const c = this.detail();
    const reason = this.reason.trim();
    this.reasonError.set(reason ? null : this.i18n.t('pos.giftCards.reasonRequired'));
    if (!c || !reason) return;
    this.busy.set(kind);
    this.actionError.set(null);
    try {
      if (kind === 'void') {
        await firstValueFrom(this.api.void(c.id, reason));
        this.toast.success(this.i18n.t('pos.giftCards.voided', { code: maskedGiftCard(c.codeLast4) }));
      } else {
        const r = await firstValueFrom(this.api.reissue(c.id, reason));
        this.reissued.set([{ id: r.card.id, code: r.code, codeLast4: r.card.codeLast4, amountMinor: r.card.balanceMinor, expiresOn: r.card.expiresOn }]);
        this.toast.success(this.i18n.t('pos.giftCards.reissued'));
      }
      this.reason = '';
      this.detail.set(await firstValueFrom(this.api.get(c.id)));
      await this.load(1);
    } catch (e) {
      this.actionError.set(posErrorText(this.i18n, e));
    } finally {
      this.busy.set(null);
    }
  }
}
