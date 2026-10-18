import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { PosApi } from '../../core/api/handynet-api';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';
import { ReceiptViewComponent } from './receipt-view';

/**
 * Public, read-only receipt (QR link on the receipt screen, `/beleg/:token`): no login, no navigation, only the
 * receipt as issued without the customer block. Unknown and expired links show the same message.
 */
@Component({
  selector: 'hn-public-receipt-page',
  imports: [ReceiptViewComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-h-dvh bg-slate-100 px-4 py-6' },
  template: `
    <main class="mx-auto max-w-md" data-testid="public-receipt">
      @if (receipt.error()) {
        <div class="rounded-lg bg-white p-6 text-center shadow-sm" role="alert" data-testid="public-receipt-missing">
          <h1 class="text-lg font-bold">{{ 'pos.ereceipt.publicMissingTitle' | t }}</h1>
          <p class="mt-2 text-sm text-slate-600">{{ 'pos.ereceipt.publicMissing' | t }}</p>
        </div>
      } @else if (receipt.value(); as r) {
        @if (r.cancelled) {
          <p class="mb-3 rounded-md bg-amber-50 px-3 py-2 text-sm font-semibold text-amber-900 ring-1 ring-amber-200" data-testid="public-receipt-cancelled">
            {{ 'pos.ereceipt.publicCancelled' | t }}
          </p>
        }
        <hn-receipt-view [doc]="r.document" />
        <p class="mt-3 text-center text-xs text-slate-500">{{ 'pos.ereceipt.validUntil' | t: { date: (r.expiresAt | hnDate: 'date') } }}</p>
      } @else {
        <p class="py-10 text-center text-sm text-slate-500">{{ 'common.loading' | t }}</p>
      }
    </main>
  `,
})
export class PublicReceiptPage {
  private readonly api = inject(PosApi);
  readonly token = input.required<string>();
  protected readonly receipt = createQuery(() => this.token(), (token) => this.api.publicReceipt(token));
}
