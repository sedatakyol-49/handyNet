import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { CashSession } from '../../core/api/models';
import { UI } from '../../shared/ui';
import { countTotal, denominationLabel, fromDenominations } from './cash-count';

/**
 * Z-report (closed session, stored figures) or interim report (open session, live figures). Printable on the receipt
 * layout when `printable` is set (only one print area per page).
 */
@Component({
  selector: 'hn-z-report',
  imports: [...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    @let s = session();
    @let z = s.summary;
    <article class="hn-card p-4 text-sm" [class.hn-print-area]="printable()" aria-labelledby="z-heading" data-testid="z-report">
      <h2 id="z-heading" class="mb-1 text-base">{{ (s.status === 'Closed' ? 'pos.z.title' : 'pos.z.interimTitle') | t }}</h2>
      <p class="mb-3 text-xs text-slate-600">
        {{ s.registerName }} · {{ s.openedAt | hnDate }} – {{ s.closedAt ? (s.closedAt | hnDate) : ('pos.z.stillOpen' | t) }}
      </p>
      @if (s.openedByName) {
        <p class="mb-1 text-xs text-slate-600" data-testid="z-opened-by">{{ 'pos.z.openedBy' | t: { name: s.openedByName } }}</p>
      }
      @if (s.closedByName) {
        <p class="mb-3 text-xs text-slate-600" data-testid="z-closed-by">{{ 'pos.z.closedBy' | t: { name: s.closedByName } }}</p>
      }

      <dl class="grid grid-cols-[1fr_auto] gap-x-4 gap-y-1">
        <dt>{{ 'pos.z.receiptRange' | t }}</dt>
        <dd class="text-end tabular-nums" data-testid="z-range">
          {{ z.firstReceiptNumber === null ? '–' : ('pos.z.range' | t: { from: z.firstReceiptNumber, to: z.lastReceiptNumber }) }}
        </dd>
        <dt>{{ 'pos.z.receiptCount' | t }}</dt>
        <dd class="text-end tabular-nums">{{ z.receiptCount }}</dd>
        <dt class="ps-3 text-slate-600">{{ 'receiptKind.Sale' | t }}</dt>
        <dd class="text-end tabular-nums text-slate-600">{{ z.saleCount }}</dd>
        <dt class="ps-3 text-slate-600">{{ 'receiptKind.Return' | t }}</dt>
        <dd class="text-end tabular-nums text-slate-600">{{ z.returnCount }}</dd>
        <dt class="ps-3 text-slate-600">{{ 'receiptKind.Storno' | t }}</dt>
        <dd class="text-end tabular-nums text-slate-600">{{ z.stornoCount }}</dd>
      </dl>

      <h3 class="mt-4 mb-1 text-xs font-semibold uppercase tracking-wide text-slate-500">{{ 'pos.z.revenue' | t }}</h3>
      <dl class="grid grid-cols-[1fr_auto] gap-x-4 gap-y-1">
        <dt class="font-semibold">{{ 'pos.z.totalGross' | t }}</dt>
        <dd class="text-end font-semibold tabular-nums" data-testid="z-total-gross">{{ z.totalGrossMinor | money }}</dd>
        <dt>{{ 'pos.z.totalNet' | t }}</dt>
        <dd class="text-end tabular-nums">{{ z.totalNetMinor | money }}</dd>
        <dt>{{ 'pos.z.totalVat' | t }}</dt>
        <dd class="text-end tabular-nums">{{ z.totalVatMinor | money }}</dd>
      </dl>

      <h3 class="mt-4 mb-1 text-xs font-semibold uppercase tracking-wide text-slate-500">{{ 'pos.z.byPayment' | t }}</h3>
      <dl class="grid grid-cols-[1fr_auto] gap-x-4 gap-y-1" data-testid="z-by-payment">
        @for (p of z.byPaymentMethod; track p.method) {
          <dt>{{ p.method | tEnum: 'posPaymentMethod' }}</dt>
          <dd class="text-end tabular-nums" [attr.data-method]="p.method">{{ p.amountMinor | money }}</dd>
        } @empty {
          <dt class="text-slate-500">–</dt><dd></dd>
        }
      </dl>

      <h3 class="mt-4 mb-1 text-xs font-semibold uppercase tracking-wide text-slate-500">{{ 'pos.z.byTax' | t }}</h3>
      <div class="overflow-x-auto">
        <table class="w-full text-sm" data-testid="z-by-tax">
          <thead>
            <tr class="text-xs text-slate-500">
              <th scope="col" class="text-start font-normal">{{ 'pos.z.treatment' | t }}</th>
              <th scope="col" class="text-end font-normal">{{ 'common.gross' | t }}</th>
              <th scope="col" class="text-end font-normal">{{ 'common.net' | t }}</th>
              <th scope="col" class="text-end font-normal">{{ 'pos.receipt.vat' | t }}</th>
            </tr>
          </thead>
          <tbody>
            @for (t of z.byTaxTreatment; track t.treatment + t.vatRateBasisPoints) {
              <tr [attr.data-treatment]="t.treatment">
                <td>{{ t.treatment | tEnum: 'taxTreatment' }} ({{ t.vatRateBasisPoints / 100 }} %)</td>
                <td class="text-end tabular-nums">{{ t.grossMinor | money }}</td>
                <td class="text-end tabular-nums">{{ t.netMinor | money }}</td>
                <td class="text-end tabular-nums">{{ t.vatMinor | money }}</td>
              </tr>
            } @empty {
              <tr><td colspan="4" class="text-slate-500">–</td></tr>
            }
          </tbody>
        </table>
      </div>

      <h3 class="mt-4 mb-1 text-xs font-semibold uppercase tracking-wide text-slate-500">{{ 'pos.z.cash' | t }}</h3>
      <dl class="grid grid-cols-[1fr_auto] gap-x-4 gap-y-1" data-testid="z-cash">
        <dt>{{ 'pos.z.opening' | t }}</dt>
        <dd class="text-end tabular-nums">{{ z.openingCashMinor | money }}</dd>
        <dt>{{ 'pos.z.cashPayments' | t }}</dt>
        <dd class="text-end tabular-nums">{{ z.cashPaymentsMinor | signedMoney }}</dd>
        <dt>{{ 'pos.z.deposits' | t }}</dt>
        <dd class="text-end tabular-nums">{{ z.depositsMinor | signedMoney }}</dd>
        <dt>{{ 'pos.z.withdrawals' | t }}</dt>
        <dd class="text-end tabular-nums">{{ -z.withdrawalsMinor | signedMoney }}</dd>
        <dt class="border-t border-slate-200 pt-1 font-semibold">{{ 'pos.z.expected' | t }}</dt>
        <dd class="border-t border-slate-200 pt-1 text-end font-semibold tabular-nums" data-testid="z-expected">{{ s.expectedCashMinor | money }}</dd>
        @if (s.countedCashMinor !== null) {
          <dt>{{ 'pos.z.counted' | t }}</dt>
          <dd class="text-end tabular-nums" data-testid="z-counted">{{ s.countedCashMinor | money }}</dd>
          <dt class="font-semibold">{{ 'pos.z.difference' | t }}</dt>
          <dd class="text-end font-semibold tabular-nums" [class.text-red-700]="(s.differenceMinor ?? 0) < 0" data-testid="z-difference">{{ s.differenceMinor | signedMoney }}</dd>
        }
      </dl>
      @if (s.closingNote) {
        <p class="mt-3 text-xs"><span class="font-semibold">{{ 'pos.session.note' | t }}:</span> {{ s.closingNote }}</p>
      }
      @for (c of counts(); track c.key) {
        <h3 class="mt-4 mb-1 text-xs font-semibold uppercase tracking-wide text-slate-500">{{ 'pos.z.' + c.key | t }}</h3>
        <table class="w-full text-sm" [attr.data-testid]="'z-' + c.key">
          <thead class="sr-only">
            <tr><th scope="col">{{ 'pos.cashCount.denomination' | t }}</th><th scope="col">{{ 'pos.cashCount.count' | t }}</th><th scope="col">{{ 'pos.receipt.amount' | t }}</th></tr>
          </thead>
          <tbody>
            @for (d of c.list; track d.valueMinor) {
              <tr [attr.data-denomination]="d.valueMinor">
                <td dir="ltr" class="text-start tabular-nums">{{ label(d.valueMinor) }}</td>
                <td class="text-end tabular-nums">× {{ d.count }}</td>
                <td class="text-end tabular-nums">{{ d.valueMinor * d.count | money }}</td>
              </tr>
            }
          </tbody>
          <tfoot>
            <tr class="border-t border-slate-200 font-semibold">
              <td colspan="2">{{ 'pos.cashCount.total' | t }}</td>
              <td class="text-end tabular-nums" [attr.data-testid]="'z-' + c.key + '-total'">{{ c.total | money }}</td>
            </tr>
          </tfoot>
        </table>
      }
    </article>
  `,
})
export class ZReportComponent {
  readonly session = input.required<CashSession>();
  readonly printable = input(false);
  protected readonly label = denominationLabel;
  /** Opening/closing cash counts by denomination (only when they were counted that way). */
  protected readonly counts = computed(() => {
    const s = this.session();
    return [
      { key: 'openingCount', list: s.openingDenominations ?? [] },
      { key: 'closingCount', list: s.closingDenominations ?? [] },
    ]
      .filter((c) => c.list.length)
      .map((c) => ({ ...c, total: countTotal(fromDenominations(c.list)) }));
  });
}
