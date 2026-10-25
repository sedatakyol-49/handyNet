import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { InvoicesApi } from '../../../core/api/handynet-api';
import { I18nService } from '../../../core/i18n/i18n.service';
import { createQuery } from '../../../shared/data/query';
import { UI } from '../../../shared/ui';
import { InvoicePdfDownloader } from './invoice-pdf';
import { formatBillingMonth, formatDateOnly } from './invoice.logic';

/**
 * Seller (B2BSales + orders.fulfil): commission invoices and corrections issued by HandyNet, newest first.
 * Read only – documents are immutable; refunds after invoicing appear as separate corrections (HN-RK-…).
 */
@Component({
  selector: 'hn-invoices-page',
  imports: [RouterLink, ...UI],
  providers: [InvoicePdfDownloader],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-page-header [title]="'invoices.title' | t" [description]="'invoices.subtitle' | t">
      <a hnButton routerLink="/app/finanzen">{{ 'invoices.backToFinance' | t }}</a>
      <button hnButton type="button" [loading]="invoices.loading()" (click)="invoices.reload()" data-testid="invoices-refresh">
        {{ 'common.refresh' | t }}
      </button>
    </hn-page-header>

    <p class="mb-3 max-w-3xl text-xs text-slate-600">{{ 'invoices.hint' | t }}</p>
    <hn-banner class="mb-3" [error]="invoices.error()" />

    <!-- phone: cards; md+: table -->
    <ul class="grid gap-3 sm:grid-cols-2 md:hidden" data-testid="invoices-cards">
      @for (i of invoices.value()?.items ?? []; track i.id) {
        <li class="hn-card flex flex-col gap-1 p-4" [attr.data-invoice-number]="i.number">
          <div class="flex items-start justify-between gap-2">
            <a class="hn-link hn-mono text-xs font-semibold" [routerLink]="['/app/finanzen/rechnungen', i.id]" [attr.aria-label]="'invoices.openLabel' | t: { number: i.number }"><bdi>{{ i.number }}</bdi></a>
            <hn-status-badge [status]="i.kind" kind="invoiceKind" [tone]="i.kind === 'CreditNote' ? 'warning' : 'neutral'" />
          </div>
          @if (i.referencedInvoiceNumber) {
            <p class="text-xs text-slate-500">{{ 'invoices.corrects' | t: { number: i.referencedInvoiceNumber } }}</p>
          }
          <p class="text-xs text-slate-500">{{ month(i.billingMonth) }} · {{ date(i.issueDate) }}</p>
          <div class="mt-1 flex items-end justify-between gap-2">
            <span class="text-lg font-bold tabular-nums"><bdi dir="ltr">{{ i.totalGrossMinor | money: i.currency }}</bdi></span>
            <button hnButton size="sm" type="button" [loading]="pdf.busyId() === i.id" [disabled]="!!pdf.busyId()" (click)="pdf.download(i)"
              [attr.aria-label]="'invoices.downloadPdfLabel' | t: { number: i.number }" data-testid="invoice-card-pdf">{{ 'invoices.pdf' | t }}</button>
          </div>
        </li>
      } @empty {
        @if (!invoices.loading()) {
          <li class="sm:col-span-2"><div class="hn-card"><hn-empty-state [title]="'invoices.empty' | t" [text]="'invoices.emptyText' | t" /></div></li>
        }
      }
    </ul>
    <div class="md:hidden">
      <hn-pagination [page]="page()" [pageSize]="pageSize" [totalCount]="invoices.value()?.totalCount ?? 0" (pageChange)="page.set($event)" />
    </div>

    <hn-table class="hidden md:block" [loading]="invoices.loading()">
      <table class="hn-table" data-testid="invoices-table">
        <thead>
          <tr>
            <th scope="col">{{ 'invoices.number' | t }}</th>
            <th scope="col">{{ 'invoices.kind' | t }}</th>
            <th scope="col">{{ 'invoices.billingMonth' | t }}</th>
            <th scope="col">{{ 'invoices.issueDate' | t }}</th>
            <th scope="col" class="num">{{ 'invoices.lineCount' | t }}</th>
            <th scope="col" class="num">{{ 'invoices.net' | t }}</th>
            <th scope="col" class="num">{{ 'invoices.vat' | t }}</th>
            <th scope="col" class="num">{{ 'invoices.gross' | t }}</th>
            <th scope="col" class="hn-actions"><span class="sr-only">{{ 'common.actions' | t }}</span></th>
          </tr>
        </thead>
        <tbody>
          @for (i of invoices.value()?.items ?? []; track i.id) {
            <tr [attr.data-invoice-number]="i.number" [attr.data-kind]="i.kind">
              <td class="whitespace-nowrap">
                <a class="hn-link hn-mono text-xs" [routerLink]="['/app/finanzen/rechnungen', i.id]" [attr.aria-label]="'invoices.openLabel' | t: { number: i.number }">
                  <bdi>{{ i.number }}</bdi>
                </a>
                @if (i.referencedInvoiceNumber) {
                  <span class="block text-xs text-slate-500">
                    {{ 'invoices.corrects' | t: { number: i.referencedInvoiceNumber } }}
                  </span>
                }
              </td>
              <td><hn-status-badge [status]="i.kind" kind="invoiceKind" [tone]="i.kind === 'CreditNote' ? 'warning' : 'neutral'" /></td>
              <td class="whitespace-nowrap">{{ month(i.billingMonth) }}</td>
              <td class="whitespace-nowrap">{{ date(i.issueDate) }}</td>
              <td class="num">{{ i.lineCount }}</td>
              <td class="num"><bdi dir="ltr">{{ i.totalNetMinor | money: i.currency }}</bdi></td>
              <td class="num"><bdi dir="ltr">{{ i.totalVatMinor | money: i.currency }}</bdi></td>
              <td class="num font-medium"><bdi dir="ltr">{{ i.totalGrossMinor | money: i.currency }}</bdi></td>
              <td class="hn-actions">
                <button hnButton size="sm" type="button" [loading]="pdf.busyId() === i.id" [disabled]="!!pdf.busyId()" (click)="pdf.download(i)"
                  [attr.aria-label]="'invoices.downloadPdfLabel' | t: { number: i.number }" data-testid="invoice-pdf">
                  {{ 'invoices.pdf' | t }}
                </button>
              </td>
            </tr>
          } @empty {
            @if (!invoices.loading()) {
              <tr><td colspan="9"><hn-empty-state [title]="'invoices.empty' | t" [text]="'invoices.emptyText' | t" /></td></tr>
            }
          }
        </tbody>
      </table>
      <div footer class="border-t border-slate-200 px-2">
        <hn-pagination [page]="page()" [pageSize]="pageSize" [totalCount]="invoices.value()?.totalCount ?? 0" (pageChange)="page.set($event)" />
      </div>
    </hn-table>
  `,
})
export class InvoicesPage {
  private readonly api = inject(InvoicesApi);
  private readonly i18n = inject(I18nService);
  protected readonly pdf = inject(InvoicePdfDownloader);

  protected readonly page = signal(1);
  protected readonly pageSize = 25;

  protected readonly invoices = createQuery(
    () => this.page(),
    (page) => this.api.list(page, this.pageSize),
  );

  protected month(value: string): string {
    return formatBillingMonth(value, this.i18n.locale());
  }

  protected date(value: string): string {
    return formatDateOnly(value, this.i18n.locale());
  }
}
