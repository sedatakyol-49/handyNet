import { ChangeDetectionStrategy, Component, Injectable, computed, inject, input, signal } from '@angular/core';
import { CompanyBrandingStore } from '../../../core/company/company-branding.store';
import { UI } from '../../../shared/ui';
import { receiptLines } from '../../company/company.logic';
import { PrinterService, germanT } from '../printing/printer.service';
import { IssuedGiftCard, dateOnly, formatGiftCardCode, maskedGiftCard, voucherLayout } from './gift-card.logic';

/**
 * Codes of gift cards issued in this page session, by receipt id. The API returns a code only in the response of the
 * creating request; it is kept in memory only (never in storage) so the receipt page reached right after a return can
 * still show it once. Cleared when the cashier confirms that the slip was handed over.
 */
@Injectable({ providedIn: 'root' })
export class GiftCardSlipStore {
  private readonly byReceipt = signal<Record<string, IssuedGiftCard[]>>({});

  remember(receiptId: string, cards: readonly IssuedGiftCard[] | null | undefined): void {
    const withCode = (cards ?? []).filter((c) => !!c.code);
    if (withCode.length) this.byReceipt.update((m) => ({ ...m, [receiptId]: withCode }));
  }

  for(receiptId: string): IssuedGiftCard[] {
    return this.byReceipt()[receiptId] ?? [];
  }

  forget(receiptId: string): void {
    this.byReceipt.update((m) => {
      const { [receiptId]: _, ...rest } = m;
      return rest;
    });
  }
}

/**
 * Voucher slip(s) after a sale or a return refunded to a gift card: amount, the full code (shown once — the API does
 * not return it again), validity, and printing through the device's printer profile (thermal) or the print dialog.
 */
@Component({
  selector: 'hn-gift-card-slips',
  imports: [...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    <section class="pos-card border-pos-accent p-4" aria-labelledby="pos-gc-slips-heading" data-testid="pos-gift-card-slips">
      <h2 id="pos-gc-slips-heading" class="text-base font-bold">{{ (cards().length > 1 ? 'pos.giftCards.slip.titleMany' : 'pos.giftCards.slip.title') | t }}</h2>
      <p class="mt-1 rounded-[10px] bg-pos-warn-soft px-3 py-2 text-sm text-pos-ink" role="note">{{ 'pos.giftCards.slip.once' | t }}</p>
      <ul class="mt-3 grid gap-3">
        @for (c of cards(); track c.id) {
          <li class="rounded-xl border border-pos-line bg-pos-surface p-3" data-testid="pos-gift-card-slip">
            <div class="flex flex-wrap items-baseline justify-between gap-2">
              <span class="text-sm text-pos-muted">{{ 'pos.giftCards.slip.amount' | t }}</span>
              <bdi dir="ltr" class="text-2xl font-bold tabular-nums" data-testid="pos-gift-card-amount">{{ c.amountMinor | money }}</bdi>
            </div>
            <p class="mt-2 text-xs text-pos-muted">{{ 'pos.giftCards.slip.code' | t }}</p>
            <p class="font-mono text-xl font-bold tracking-wider break-all" dir="ltr" data-testid="pos-gift-card-code">{{ c.code ? format(c.code) : masked(c.codeLast4) }}</p>
            @if (c.expiresOn) {
              <p class="mt-1 text-sm">{{ 'pos.giftCards.slip.validUntil' | t }}: <span data-testid="pos-gift-card-expires">{{ date(c.expiresOn) }}</span></p>
            }
          </li>
        }
      </ul>
      <div class="mt-3 flex flex-wrap gap-2">
        <button type="button" class="pos-soft pos-soft-accent min-h-11" [disabled]="printing()" (click)="print()" data-testid="pos-gift-card-print">{{ 'pos.giftCards.slip.print' | t }}</button>
      </div>
    </section>
  `,
})
export class GiftCardSlipsComponent {
  readonly cards = input.required<IssuedGiftCard[]>();
  readonly registerId = input.required<string>();
  readonly receiptNumber = input<number | null>(null);

  private readonly printer = inject(PrinterService);
  private readonly branding = inject(CompanyBrandingStore);
  protected readonly printing = signal(false);
  protected readonly format = formatGiftCardCode;
  protected readonly masked = maskedGiftCard;
  protected readonly date = dateOnly;
  private readonly sellerLines = computed(() => {
    const p = this.branding.profile();
    return p ? [...receiptLines(p.receiptHeader), p.name].filter(Boolean) : [];
  });

  protected async print(): Promise<void> {
    this.printing.set(true);
    try {
      for (const card of this.cards()) {
        await this.printer.printLayout(this.registerId(), germanT('pos.giftCards.slip.title'), (width, codepage) =>
          voucherLayout(card, { width, codepage, t: germanT, sellerLines: this.sellerLines(), receiptNumber: this.receiptNumber() }),
        );
      }
    } finally {
      this.printing.set(false);
    }
  }
}
