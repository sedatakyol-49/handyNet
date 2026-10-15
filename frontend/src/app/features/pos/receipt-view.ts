import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { ReceiptDocument } from '../../core/api/models';
import { UI } from '../../shared/ui';

/**
 * The receipt exactly as issued (the API's stored document). Marked `hn-print-area`: when printed, only this block is
 * rendered, on a narrow (80 mm) receipt layout (see styles.css). Only one print area may be on a page.
 */
@Component({
  selector: 'hn-receipt-view',
  imports: [...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    @let d = doc();
    <article class="hn-print-area hn-receipt mx-auto max-w-sm rounded-lg border border-slate-300 bg-white p-4 font-mono text-xs text-slate-900"
      [attr.aria-label]="d.title + ' ' + d.receiptNumber" data-testid="receipt">
      @if (d.fiscal.status !== 'Signed') {
        <p class="mb-3 rounded border-2 border-amber-500 bg-amber-50 px-2 py-1.5 text-center text-sm font-bold text-amber-900" role="note" data-testid="receipt-fiscal-notice">
          {{ d.fiscal.notice || ('pos.receipt.notSigned' | t) }}
        </p>
      }
      <header class="mb-2 text-center">
        <p class="text-sm font-bold">{{ d.seller.name }}</p>
        @if (d.seller.legalName && d.seller.legalName !== d.seller.name) {
          <p>{{ d.seller.legalName }}</p>
        }
        <p>{{ d.seller.street }}</p>
        <p>{{ d.seller.postalCode }} {{ d.seller.city }}</p>
        @if (d.seller.vatId) {
          <p>{{ 'pos.receipt.vatId' | t }}: {{ d.seller.vatId }}</p>
        }
      </header>

      <p class="text-center text-sm font-bold" data-testid="receipt-title">{{ d.title }}</p>
      <dl class="my-2 grid grid-cols-[auto_1fr] gap-x-3">
        <dt>{{ 'pos.receipt.number' | t }}</dt>
        <dd class="text-end font-bold" data-testid="receipt-number">{{ d.receiptNumber }}</dd>
        @if (d.referencedReceiptNumber) {
          <dt>{{ 'pos.receipt.reference' | t }}</dt>
          <dd class="text-end" data-testid="receipt-reference">{{ d.referencedReceiptNumber }}</dd>
        }
        <dt>{{ 'pos.receipt.register' | t }}</dt>
        <dd class="text-end">{{ d.registerName }}</dd>
        <dt>{{ 'pos.receipt.date' | t }}</dt>
        <dd class="text-end">{{ d.issuedAt | hnDate }}</dd>
      </dl>

      <table class="w-full border-y border-dashed border-slate-400" data-testid="receipt-lines">
        <caption class="sr-only">{{ 'pos.receipt.lines' | t }}</caption>
        <thead class="sr-only">
          <tr><th scope="col">{{ 'common.product' | t }}</th><th scope="col">{{ 'pos.receipt.amount' | t }}</th><th scope="col">{{ 'pos.receipt.taxLabel' | t }}</th></tr>
        </thead>
        <tbody>
          @for (l of d.lines; track $index) {
            <tr class="align-top">
              <td class="py-1 pe-2">
                <span class="block font-semibold">{{ l.description }}</span>
                @if (l.imei) {
                  <span class="block">IMEI {{ l.imei }}</span>
                }
                <span class="block">{{ l.quantity }} × {{ l.unitPriceMinor | money: d.currency }}</span>
                @if (l.discountMinor) {
                  <span class="block">{{ 'pos.receipt.discount' | t }} {{ -l.discountMinor | money: d.currency }}</span>
                }
              </td>
              <td class="py-1 text-end whitespace-nowrap tabular-nums">{{ l.lineGrossMinor | money: d.currency }}</td>
              <td class="py-1 ps-2 text-end whitespace-nowrap">{{ l.taxLabel }}</td>
            </tr>
          }
        </tbody>
      </table>

      <p class="my-2 flex justify-between text-sm font-bold">
        <span>{{ 'pos.receipt.total' | t }}</span><span class="tabular-nums" data-testid="receipt-total">{{ d.totalGrossMinor | money: d.currency }}</span>
      </p>

      @if (d.payments.length) {
        <dl class="mb-2 grid grid-cols-[1fr_auto] gap-x-3" data-testid="receipt-payments">
          @for (p of d.payments; track $index) {
            @if (p.tenderedMinor !== null && p.tenderedMinor !== p.amountMinor) {
              <dt>{{ 'pos.receipt.tendered' | t: { method: (p.method | tEnum: 'posPaymentMethod') } }}</dt>
              <dd class="text-end tabular-nums">{{ p.tenderedMinor | money: d.currency }}</dd>
            } @else {
              <dt>{{ p.method | tEnum: 'posPaymentMethod' }}</dt>
              <dd class="text-end tabular-nums">{{ p.amountMinor | money: d.currency }}</dd>
            }
          }
          @if (d.changeMinor) {
            <dt class="font-semibold">{{ 'pos.receipt.change' | t }}</dt>
            <dd class="text-end font-semibold tabular-nums" data-testid="receipt-change">{{ d.changeMinor | money: d.currency }}</dd>
          }
        </dl>
      }

      @if (d.vat.length) {
        <table class="mb-2 w-full border-t border-dashed border-slate-400" data-testid="receipt-vat">
          <caption class="py-1 text-start font-semibold">{{ 'pos.receipt.vatBreakdown' | t }}</caption>
          <thead>
            <tr>
              <th scope="col" class="text-start font-normal">{{ 'pos.receipt.rate' | t }}</th>
              <th scope="col" class="text-end font-normal">{{ 'common.net' | t }}</th>
              <th scope="col" class="text-end font-normal">{{ 'pos.receipt.vat' | t }}</th>
              <th scope="col" class="text-end font-normal">{{ 'common.gross' | t }}</th>
            </tr>
          </thead>
          <tbody>
            @for (v of d.vat; track $index) {
              <tr>
                <td>{{ v.label }}</td>
                <td class="text-end tabular-nums">{{ v.netMinor | money: d.currency }}</td>
                <td class="text-end tabular-nums">{{ v.vatMinor | money: d.currency }}</td>
                <td class="text-end tabular-nums">{{ v.grossMinor | money: d.currency }}</td>
              </tr>
            }
          </tbody>
        </table>
      }

      @for (n of notes(); track $index) {
        <p class="mb-1 border-t border-dashed border-slate-400 pt-1" data-testid="receipt-note">{{ n }}</p>
      }

      @if (d.fiscal.status === 'Signed') {
        <dl class="mt-2 grid grid-cols-[auto_1fr] gap-x-2 border-t border-dashed border-slate-400 pt-1 break-all" data-testid="receipt-tse">
          <dt>{{ 'pos.receipt.tseSerial' | t }}</dt><dd>{{ d.fiscal.tseSerialNumber }}</dd>
          <dt>{{ 'pos.receipt.tseTransaction' | t }}</dt><dd>{{ d.fiscal.transactionNumber }}</dd>
          <dt>{{ 'pos.receipt.tseCounter' | t }}</dt><dd>{{ d.fiscal.signatureCounter }}</dd>
          <dt>{{ 'pos.receipt.tseStart' | t }}</dt><dd>{{ d.fiscal.startedAt | hnDate }}</dd>
          <dt>{{ 'pos.receipt.tseEnd' | t }}</dt><dd>{{ d.fiscal.finishedAt | hnDate }}</dd>
          <dt>{{ 'pos.receipt.tseSignature' | t }}</dt><dd>{{ d.fiscal.signature }}</dd>
        </dl>
      }
    </article>
  `,
})
export class ReceiptViewComponent {
  readonly doc = input.required<ReceiptDocument>();
  /** Notes printed below the totals; the fiscal notice is shown prominently at the top instead of repeated here. */
  protected readonly notes = computed(() => {
    const d = this.doc();
    return d.notes.filter((n) => n !== d.fiscal.notice);
  });
}
