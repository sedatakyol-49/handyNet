import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { InvoicesApi } from '../../../core/api/handynet-api';
import { InvoiceParty } from '../../../core/api/models';
import { I18nService } from '../../../core/i18n/i18n.service';
import { createQuery } from '../../../shared/data/query';
import { UI } from '../../../shared/ui';
import { InvoicePdfDownloader } from './invoice-pdf';
import { formatBillingMonth, formatDateOnly, formatVatRate, isPlaceholderParty, partyLines } from './invoice.logic';

/** One commission invoice or correction exactly as issued (snapshot of issuer/recipient, lines, totals) + PDF. */
@Component({
  selector: 'hn-invoice-detail-page',
  imports: [RouterLink, ...UI],
  providers: [InvoicePdfDownloader],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @let inv = invoice.value();
    <hn-page-header [title]="inv ? (inv.kind | tEnum: 'invoiceKind') + ' ' + inv.number : ('invoices.title' | t)" [description]="'invoices.detail.subtitle' | t">
      <a hnButton routerLink="/app/finanzen/rechnungen" data-testid="invoice-back">{{ 'invoices.back' | t }}</a>
      @if (inv) {
        <button hnButton variant="primary" type="button" [loading]="pdf.busyId() === inv.id" [disabled]="!!pdf.busyId()" (click)="pdf.download(inv)"
          data-testid="invoice-detail-pdf">
          {{ 'invoices.downloadPdf' | t }}
        </button>
      }
    </hn-page-header>

    <hn-banner class="mb-3" [error]="invoice.error()" />

    @if (inv) {
      @if (sample()) {
        <hn-banner class="mb-3" tone="warning" [text]="'invoices.detail.placeholder' | t" data-testid="invoice-sample" />
      }

      <div class="mb-4 grid gap-4 lg:grid-cols-3">
        <section class="hn-card p-4" aria-labelledby="inv-data-heading" data-testid="invoice-header">
          <h2 id="inv-data-heading" class="mb-2 text-sm">{{ 'invoices.detail.document' | t }}</h2>
          <dl class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm">
            <dt class="hn-muted">{{ 'invoices.number' | t }}</dt>
            <dd class="hn-mono" data-testid="invoice-number"><bdi>{{ inv.number }}</bdi></dd>
            <dt class="hn-muted">{{ 'invoices.kind' | t }}</dt>
            <dd><hn-status-badge [status]="inv.kind" kind="invoiceKind" [tone]="inv.kind === 'CreditNote' ? 'warning' : 'neutral'" /></dd>
            <dt class="hn-muted">{{ 'invoices.issueDate' | t }}</dt>
            <dd>{{ date(inv.issueDate) }}</dd>
            <dt class="hn-muted">{{ 'invoices.billingMonth' | t }}</dt>
            <dd>{{ month(inv.billingMonth) }}</dd>
            <dt class="hn-muted">{{ 'invoices.servicePeriod' | t }}</dt>
            <dd>{{ 'invoices.periodRange' | t: { from: date(inv.servicePeriodStart), to: date(inv.servicePeriodEnd) } }}</dd>
            <dt class="hn-muted">{{ 'invoices.detail.vatRate' | t }}</dt>
            <dd>{{ vatRate(inv.vatRateBasisPoints) }}</dd>
            @if (inv.referencedInvoiceId && inv.referencedInvoiceNumber) {
              <dt class="hn-muted">{{ 'invoices.detail.correctsInvoice' | t }}</dt>
              <dd>
                <a class="hn-link hn-mono" [routerLink]="['/app/finanzen/rechnungen', inv.referencedInvoiceId]" data-testid="invoice-referenced"
                  ><bdi>{{ inv.referencedInvoiceNumber }}</bdi></a
                >
              </dd>
            }
          </dl>
        </section>

        @for (p of parties(); track p.key) {
          <section class="hn-card p-4" [attr.aria-labelledby]="'inv-' + p.key + '-heading'" [attr.data-testid]="'invoice-' + p.key">
            <h2 [id]="'inv-' + p.key + '-heading'" class="mb-2 text-sm">{{ p.labelKey | t }}</h2>
            <address class="text-sm not-italic">
              @for (line of p.lines; track $index) {
                <span class="block">{{ line }}</span>
              }
            </address>
            @if (p.party.vatId || p.party.taxNumber) {
              <dl class="mt-2 grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-xs">
                @if (p.party.vatId) {
                  <dt class="hn-muted">{{ 'invoices.detail.vatId' | t }}</dt>
                  <dd class="hn-mono"><bdi>{{ p.party.vatId }}</bdi></dd>
                }
                @if (p.party.taxNumber) {
                  <dt class="hn-muted">{{ 'invoices.detail.taxNumber' | t }}</dt>
                  <dd class="hn-mono"><bdi>{{ p.party.taxNumber }}</bdi></dd>
                }
              </dl>
            }
          </section>
        }
      </div>

      <section aria-labelledby="inv-lines-heading">
        <h2 id="inv-lines-heading" class="mb-1 text-sm">{{ 'invoices.lines' | t }}</h2>
        <p class="mb-2 text-xs text-slate-600">
          {{ (inv.kind === 'CreditNote' ? 'invoices.detail.correctionNote' : 'invoices.detail.serviceText') | t: { number: inv.referencedInvoiceNumber ?? '' } }}
        </p>
        <hn-table>
          <table class="hn-table" data-testid="invoice-lines">
            <thead>
              <tr>
                <th scope="col" class="num">{{ 'invoices.detail.line' | t }}</th>
                <th scope="col">{{ 'invoices.detail.order' | t }}</th>
                <th scope="col">{{ 'invoices.detail.paidAt' | t }}</th>
                <th scope="col" class="num">{{ 'invoices.net' | t }}</th>
                <th scope="col" class="num">{{ 'invoices.vat' | t }}</th>
                <th scope="col" class="num">{{ 'invoices.gross' | t }}</th>
              </tr>
            </thead>
            <tbody>
              @for (l of inv.lines; track l.lineNumber) {
                <tr [attr.data-order-number]="l.orderNumber">
                  <td class="num">{{ l.lineNumber }}</td>
                  <td>
                    <a class="hn-link hn-mono text-xs" [routerLink]="['/app/bestellungen', l.orderId]" [attr.aria-label]="'finance.openOrderNumber' | t: { number: l.orderNumber }"
                      ><bdi>{{ l.orderNumber }}</bdi></a
                    >
                  </td>
                  <td class="whitespace-nowrap">{{ l.paidAt | hnDate }}</td>
                  <td class="num"><bdi dir="ltr">{{ l.netMinor | money: inv.currency }}</bdi></td>
                  <td class="num"><bdi dir="ltr">{{ l.vatMinor | money: inv.currency }}</bdi></td>
                  <td class="num"><bdi dir="ltr">{{ l.grossMinor | money: inv.currency }}</bdi></td>
                </tr>
              }
            </tbody>
            <tfoot data-testid="invoice-totals">
              <tr>
                <th scope="row" colspan="3" class="text-end font-medium">{{ 'invoices.detail.totalNet' | t }}</th>
                <td class="num" data-testid="invoice-total-net"><bdi dir="ltr">{{ inv.totalNetMinor | money: inv.currency }}</bdi></td>
                <td colspan="2"></td>
              </tr>
              <tr>
                <th scope="row" colspan="3" class="text-end font-medium">{{ 'invoices.detail.totalVat' | t: { rate: vatRate(inv.vatRateBasisPoints) } }}</th>
                <td></td>
                <td class="num" data-testid="invoice-total-vat"><bdi dir="ltr">{{ inv.totalVatMinor | money: inv.currency }}</bdi></td>
                <td></td>
              </tr>
              <tr>
                <th scope="row" colspan="3" class="text-end font-semibold">{{ 'invoices.detail.totalGross' | t }}</th>
                <td colspan="2"></td>
                <td class="num font-semibold" data-testid="invoice-total-gross"><bdi dir="ltr">{{ inv.totalGrossMinor | money: inv.currency }}</bdi></td>
              </tr>
            </tfoot>
          </table>
        </hn-table>
        <p class="mt-2 text-xs text-slate-600">{{ 'invoices.detail.withheldNote' | t }}</p>
        <p class="mt-1 text-xs text-slate-500">{{ 'invoices.detail.issuedAt' | t: { date: (inv.issuedAt | hnDate) } }}</p>
      </section>
    } @else if (invoice.loading()) {
      <p class="hn-muted text-sm">{{ 'common.loading' | t }}</p>
    }
  `,
})
export class InvoiceDetailPage {
  private readonly api = inject(InvoicesApi);
  private readonly i18n = inject(I18nService);
  protected readonly pdf = inject(InvoicePdfDownloader);

  /** Route parameter: invoice id. */
  readonly id = input.required<string>();

  protected readonly invoice = createQuery(
    () => this.id(),
    (id) => this.api.get(id),
  );

  protected readonly sample = computed(() => isPlaceholderParty(this.invoice.value()?.issuer));

  protected readonly parties = computed(() => {
    const inv = this.invoice.value();
    if (!inv) return [];
    const block = (key: 'issuer' | 'recipient', labelKey: string, party: InvoiceParty) => ({ key, labelKey, party, lines: partyLines(party) });
    return [block('issuer', 'invoices.detail.issuer', inv.issuer), block('recipient', 'invoices.detail.recipient', inv.recipient)];
  });

  protected month(value: string): string {
    return formatBillingMonth(value, this.i18n.locale());
  }

  protected date(value: string): string {
    return formatDateOnly(value, this.i18n.locale());
  }

  protected vatRate(bp: number): string {
    return formatVatRate(bp, this.i18n.locale());
  }
}
