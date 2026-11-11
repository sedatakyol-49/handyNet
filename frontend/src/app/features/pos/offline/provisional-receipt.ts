import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { SessionStore } from '../../../core/auth/session.store';
import { CompanyBrandingStore } from '../../../core/company/company-branding.store';
import { formatMoney, formatSignedMoney } from '../../../core/format/money';
import { UI } from '../../../shared/ui';
import { Codepage } from '../printing/escpos';
import { LayoutBuilder, PrintLayout, Translate } from '../printing/receipt-layout';
import { QueueEntry } from './offline-queue.logic';

export interface ProvisionalLayoutOptions {
  width: number;
  codepage: Codepage;
  t: Translate;
  locale: string;
  sellerLines: string[];
}

/**
 * Thermal layout of the provisional receipt (same builder as real receipts). It carries no receipt number and no TSE
 * data — both exist only after the sale was booked — and says so at the top and at the bottom.
 */
export function provisionalLayout(e: QueueEntry, o: ProvisionalLayoutOptions): PrintLayout {
  const b = new LayoutBuilder(o.width, o.codepage);
  const t = o.t;
  const when = new Intl.DateTimeFormat(o.locale, { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(e.recordedAt));
  b.text(t('pos.offline.receipt.title'), { align: 'center', bold: true });
  b.text(t('pos.offline.receipt.notice'), { align: 'center', bold: true }).feed();
  for (const l of o.sellerLines) b.text(l, { align: 'center' });
  b.feed();
  b.pair(t('pos.offline.receipt.reference'), e.reference, { bold: true });
  b.pair(t('pos.offline.receipt.recordedAt'), when);
  if (e.registerName) b.pair(t('pos.receipt.register'), e.registerName);
  if (e.cashierName) b.pair(t('pos.receipt.cashier'), e.cashierName);
  b.rule();
  for (const l of e.lines) {
    b.text(l.description, { bold: true });
    if (l.imei) b.text(`IMEI ${l.imei}`);
    b.pair(`  ${l.quantity} x ${formatMoney(l.unitPriceMinor)}`, formatMoney(l.lineGrossMinor));
    if (l.discountMinor) b.text(`  ${t('pos.receipt.discount')} ${formatMoney(-l.discountMinor)}`);
    if (l.note) b.text(`  ${l.note}`);
  }
  b.rule();
  b.pair(t('pos.receipt.total'), formatMoney(e.totalMinor), { bold: true, size: 2 });
  for (const p of e.payments) {
    const method = t(`posPaymentMethod.${p.method}`);
    if (p.tenderedMinor !== null && p.tenderedMinor !== p.amountMinor) b.pair(t('pos.receipt.tendered', { method }), formatMoney(p.tenderedMinor));
    else b.pair(method, formatMoney(p.amountMinor));
  }
  if (e.roundingMinor) b.pair(t('pos.rounding.label'), formatSignedMoney(e.roundingMinor));
  if (e.changeMinor) b.pair(t('pos.receipt.change'), formatMoney(e.changeMinor), { bold: true });
  b.rule();
  b.text(t('pos.offline.receipt.footer'));
  return b.build();
}

/**
 * Provisional receipt of a sale recorded offline, shown and printed (`hn-print-area`) instead of a till receipt. It is
 * not a Kassenbeleg: no receipt number, no TSE signature. The real receipt is issued when the queue syncs.
 */
@Component({
  selector: 'hn-provisional-receipt',
  imports: [...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    @let e = entry();
    <article class="hn-print-area hn-receipt mx-auto max-w-sm rounded-lg border border-slate-300 bg-white p-4 font-mono text-xs text-slate-900"
      [attr.aria-label]="('pos.offline.receipt.title' | t) + ' ' + e.reference" data-testid="provisional-receipt">
      <p class="mb-3 rounded border-2 border-amber-500 bg-amber-50 px-2 py-1.5 text-center text-sm font-bold text-amber-900" role="note" data-testid="provisional-notice">
        {{ 'pos.offline.receipt.title' | t }}<br />
        <span class="text-xs font-semibold">{{ 'pos.offline.receipt.notice' | t }}</span>
      </p>
      <header class="mb-2 text-center">
        @for (line of sellerLines(); track $index) {
          <p [class.font-bold]="$first">{{ line }}</p>
        }
      </header>
      <dl class="mb-2 grid grid-cols-[auto_1fr] gap-x-3">
        <dt class="font-bold">{{ 'pos.offline.receipt.reference' | t }}</dt>
        <dd class="text-end font-bold" data-testid="provisional-reference">{{ e.reference }}</dd>
        <dt>{{ 'pos.offline.receipt.recordedAt' | t }}</dt>
        <dd class="text-end">{{ e.recordedAt | hnDate: 'datetime' }}</dd>
        @if (e.registerName) {
          <dt>{{ 'pos.receipt.register' | t }}</dt>
          <dd class="text-end">{{ e.registerName }}</dd>
        }
        @if (e.cashierName) {
          <dt>{{ 'pos.receipt.cashier' | t }}</dt>
          <dd class="text-end">{{ e.cashierName }}</dd>
        }
      </dl>
      <ul class="mb-2 border-y border-dashed border-slate-400 py-2">
        @for (l of e.lines; track $index) {
          <li class="mb-1 last:mb-0">
            <p class="font-bold break-words">{{ l.description }}</p>
            @if (l.imei) {
              <p>IMEI {{ l.imei }}</p>
            }
            <p class="flex justify-between gap-3 tabular-nums">
              <span>{{ l.quantity }} × {{ l.unitPriceMinor | money }}</span><span>{{ l.lineGrossMinor | money }}</span>
            </p>
            @if (l.discountMinor) {
              <p>{{ 'pos.receipt.discount' | t }} {{ -l.discountMinor | money }}</p>
            }
            @if (l.note) {
              <p class="italic">{{ l.note }}</p>
            }
          </li>
        }
      </ul>
      <p class="mb-2 flex justify-between gap-3 text-base font-bold">
        <span>{{ 'pos.receipt.total' | t }}</span><span class="tabular-nums" data-testid="provisional-total">{{ e.totalMinor | money }}</span>
      </p>
      <dl class="mb-2 grid grid-cols-[1fr_auto] gap-x-3">
        @for (p of e.payments; track $index) {
          @if (p.tenderedMinor !== null && p.tenderedMinor !== p.amountMinor) {
            <dt>{{ 'pos.receipt.tendered' | t: { method: (p.method | tEnum: 'posPaymentMethod') } }}</dt>
            <dd class="text-end tabular-nums">{{ p.tenderedMinor | money }}</dd>
          } @else {
            <dt>{{ p.method | tEnum: 'posPaymentMethod' }}</dt>
            <dd class="text-end tabular-nums">{{ p.amountMinor | money }}</dd>
          }
        }
        @if (e.roundingMinor) {
          <dt>{{ 'pos.rounding.label' | t }}</dt>
          <dd class="text-end tabular-nums">{{ e.roundingMinor | signedMoney }}</dd>
        }
        @if (e.changeMinor) {
          <dt class="font-semibold">{{ 'pos.receipt.change' | t }}</dt>
          <dd class="text-end font-semibold tabular-nums">{{ e.changeMinor | money }}</dd>
        }
      </dl>
      <p class="border-t border-dashed border-slate-400 pt-2 text-[11px] leading-snug">{{ 'pos.offline.receipt.footer' | t }}</p>
    </article>
  `,
})
export class ProvisionalReceiptComponent {
  readonly entry = input.required<QueueEntry>();
  private readonly branding = inject(CompanyBrandingStore);
  private readonly session = inject(SessionStore);

  /** Name and address of the shop as far as the device knows them (the company profile may not be loaded offline). */
  readonly sellerLines = computed(() => {
    const p = this.branding.profile();
    if (!p) return [this.session.membership()?.organizationName ?? ''].filter(Boolean);
    return [p.name, p.legalName !== p.name ? p.legalName : null, p.street, [p.postalCode, p.city].filter(Boolean).join(' '), p.vatId]
      .map((x) => (x ?? '').trim())
      .filter(Boolean);
  });
}
