import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { PosApi } from '../../core/api/handynet-api';
import { ReceiptSummary } from '../../core/api/models';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';
import { EMPTY_SALES_FILTER, filterToQuery } from '../pos/sales-filter';
import { OFFICE_SALES_NAV, receiptLink } from './sales-nav';

interface ReceiptsFilter {
  register: string;
  number: string;
  from: string;
  to: string;
}

const NO_FILTER: ReceiptsFilter = { register: '', number: '', from: '', to: '' };

/**
 * Back office: till receipts of ALL registers (`GET /api/pos/sales`) - number, date, register, cashier, kind, total,
 * payment methods, TSE state and the invoice issued for the receipt. A row opens the receipt detail.
 */
@Component({
  selector: 'hn-sales-receipts-page',
  imports: [RouterLink, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    <hn-page-header [title]="'sales.receipts.title' | t" [description]="'sales.receipts.description' | t">
      <button hnButton type="button" class="min-h-11" [loading]="receipts.loading()" (click)="receipts.reload()">{{ 'common.refresh' | t }}</button>
    </hn-page-header>

    <nav class="mb-3 flex flex-wrap gap-1.5" [attr.aria-label]="'sales.switch' | t">
      <a [routerLink]="nav.invoices" class="pos-chip" aria-pressed="false" data-testid="sales-tab-invoices">{{ 'sales.invoices.title' | t }}</a>
      <a [routerLink]="nav.quotations" class="pos-chip" aria-pressed="false" data-testid="sales-tab-quotations">{{ 'sales.quotations.title' | t }}</a>
      <a [routerLink]="nav.receipts" class="pos-chip" aria-pressed="true" data-testid="sales-tab-receipts">{{ 'sales.receipts.title' | t }}</a>
    </nav>

    <form class="mb-3 grid gap-2 sm:grid-cols-2 lg:grid-cols-4" role="search" (submit)="$event.preventDefault(); apply()" data-testid="receipts-filter">
      <hn-field [label]="'sales.receipts.register' | t" for="rc-register">
        <select hnSelect id="rc-register" class="h-11" (change)="draft.update(d => ({ ...d, register: $any($event.target).value }))" data-testid="receipts-register">
          <option value="" [selected]="!draft().register">{{ 'sales.receipts.allRegisters' | t }}</option>
          @for (r of registers.value() ?? []; track r.id) {
            <option [value]="r.id" [selected]="r.id === draft().register">{{ r.name }}</option>
          }
        </select>
      </hn-field>
      <hn-field [label]="'pos.receipt.number' | t" for="rc-number" [errors]="numberInvalid() ? ('pos.receipts.errors.invalid' | t) : null">
        <input hnInput id="rc-number" inputmode="numeric" class="h-11" autocomplete="off" [value]="draft().number" (input)="draft.update(d => ({ ...d, number: $any($event.target).value }))" data-testid="receipts-number" />
      </hn-field>
      <hn-field [label]="'pos.receipts.from' | t" for="rc-from">
        <input hnInput id="rc-from" type="date" class="h-11" [value]="draft().from" (input)="draft.update(d => ({ ...d, from: $any($event.target).value }))" data-testid="receipts-from" />
      </hn-field>
      <hn-field [label]="'pos.receipts.to' | t" for="rc-to" [errors]="rangeInvalid() ? ('pos.receipts.errors.dateRange' | t) : null">
        <input hnInput id="rc-to" type="date" class="h-11" [value]="draft().to" (input)="draft.update(d => ({ ...d, to: $any($event.target).value }))" data-testid="receipts-to" />
      </hn-field>
      <div class="flex gap-2 sm:col-span-2 lg:col-span-4">
        <button hnButton variant="primary" type="submit" class="min-h-11" data-testid="receipts-apply">{{ 'pos.receipts.apply' | t }}</button>
        <button hnButton variant="ghost" type="button" class="min-h-11" (click)="reset()">{{ 'pos.receipts.reset' | t }}</button>
      </div>
    </form>

    <hn-banner class="mb-3" [error]="receipts.error()" />
    <ul class="grid gap-2 md:hidden" data-testid="receipts-cards">
      @for (r of receipts.value()?.items ?? []; track r.id) {
        <li>
          <a [routerLink]="link(r)" class="block rounded-lg border border-slate-200 bg-white p-3 hover:border-accent-600 focus-visible:border-accent-600" [attr.data-number]="r.number">
            <span class="flex items-center justify-between gap-2">
              <span class="font-semibold tabular-nums">{{ r.number }}</span>
              <hn-status-badge [status]="r.kind" kind="receiptKind" [tone]="kindTone(r)" />
            </span>
            <span class="block text-xs text-slate-500">{{ r.createdAt | hnDate }} · {{ registerName(r.registerId) }} · {{ r.createdByName ?? '–' }}</span>
            <span class="flex items-center justify-between gap-2 text-sm">
              <span class="text-xs text-slate-600">{{ tseKey(r) | t }}@if (r.invoiceNumber) { · {{ r.invoiceNumber }} }</span>
              <span class="font-semibold tabular-nums"><bdi dir="ltr">{{ r.totalGrossMinor | money }}</bdi></span>
            </span>
          </a>
        </li>
      } @empty {
        <li class="py-8 text-center text-sm text-slate-500">{{ (receipts.loading() ? 'common.loading' : 'sales.receipts.empty') | t }}</li>
      }
    </ul>
    <div class="md:hidden">
      <hn-pagination [page]="page()" [pageSize]="pageSize" [totalCount]="receipts.value()?.totalCount ?? 0" (pageChange)="page.set($event)" />
    </div>
    <hn-table class="hidden md:block" [loading]="receipts.loading()">
      <table class="hn-table" data-testid="receipts-table">
        <thead>
          <tr>
            <th scope="col" class="num">{{ 'pos.receipt.number' | t }}</th>
            <th scope="col">{{ 'pos.receipt.date' | t }}</th>
            <th scope="col">{{ 'sales.receipts.register' | t }}</th>
            <th scope="col">{{ 'pos.receipt.cashier' | t }}</th>
            <th scope="col">{{ 'pos.receipts.kind' | t }}</th>
            <th scope="col">{{ 'pos.receipts.paymentMethod' | t }}</th>
            <th scope="col">{{ 'sales.receipts.tse' | t }}</th>
            <th scope="col">{{ 'sales.receipts.invoice' | t }}</th>
            <th scope="col" class="num">{{ 'pos.receipt.total' | t }}</th>
          </tr>
        </thead>
        <tbody>
          @for (r of receipts.value()?.items ?? []; track r.id) {
            <tr class="hn-row-link" tabindex="0" (click)="open(r)" (keydown.enter)="open(r)" [attr.data-number]="r.number">
              <td class="num font-semibold"><a [routerLink]="link(r)" class="hn-link" (click)="$event.stopPropagation()">{{ r.number }}</a></td>
              <td class="text-xs whitespace-nowrap">{{ r.createdAt | hnDate }}</td>
              <td class="text-sm">{{ registerName(r.registerId) }}</td>
              <td class="text-sm">{{ r.createdByName ?? '–' }}</td>
              <td><hn-status-badge [status]="r.kind" kind="receiptKind" [tone]="kindTone(r)" /></td>
              <td class="text-sm">
                @for (m of r.paymentMethods ?? []; track m; let last = $last) {
                  {{ m | tEnum: 'posPaymentMethod' }}{{ last ? '' : ', ' }}
                }
              </td>
              <td class="text-sm" data-testid="receipt-tse">{{ tseKey(r) | t }}</td>
              <td class="text-sm" data-testid="receipt-invoice">{{ r.invoiceNumber ?? '–' }}</td>
              <td class="num"><bdi dir="ltr" [class.text-red-700]="r.totalGrossMinor < 0">{{ r.totalGrossMinor | money }}</bdi></td>
            </tr>
          } @empty {
            <tr><td colspan="9" class="py-8 text-center text-sm text-slate-500">{{ 'sales.receipts.empty' | t }}</td></tr>
          }
        </tbody>
      </table>
      <hn-pagination footer [page]="page()" [pageSize]="pageSize" [totalCount]="receipts.value()?.totalCount ?? 0" (pageChange)="page.set($event)" />
    </hn-table>
  `,
})
export class SalesReceiptsPage {
  private readonly api = inject(PosApi);
  private readonly router = inject(Router);
  protected readonly nav = OFFICE_SALES_NAV;
  protected readonly pageSize = 25;
  protected readonly page = signal(1);

  protected readonly draft = signal<ReceiptsFilter>(NO_FILTER);
  protected readonly applied = signal<ReceiptsFilter>(NO_FILTER);
  private readonly showErrors = signal(false);

  private readonly draftCheck = computed(() => this.toQuery(this.draft()));
  protected readonly numberInvalid = computed(() => this.showErrors() && !!this.draftCheck().errors.number);
  protected readonly rangeInvalid = computed(() => this.showErrors() && !!this.draftCheck().errors.dateRange);

  protected readonly registers = createQuery(
    () => true,
    () => this.api.registers(),
  );
  protected readonly receipts = createQuery(
    () => ({ q: this.toQuery(this.applied()).query, page: this.page() }),
    (x) => this.api.receipts({ ...x.q, page: x.page, pageSize: this.pageSize }),
  );

  private toQuery(f: ReceiptsFilter) {
    const r = filterToQuery({ ...EMPTY_SALES_FILTER, scope: 'all', number: f.number, from: f.from, to: f.to }, { registerId: '', sessionId: null });
    if (f.register) r.query.registerId = f.register;
    return r;
  }

  protected apply(): void {
    this.showErrors.set(true);
    if (!this.draftCheck().valid) return;
    this.applied.set(this.draft());
    this.page.set(1);
  }

  protected reset(): void {
    this.showErrors.set(false);
    this.draft.set(NO_FILTER);
    this.applied.set(NO_FILTER);
    this.page.set(1);
  }

  protected registerName(id: string): string {
    return this.registers.value()?.find((r) => r.id === id)?.name ?? '–';
  }

  protected kindTone(r: ReceiptSummary): 'success' | 'danger' | 'warning' {
    return r.kind === 'Sale' ? 'success' : r.kind === 'Storno' ? 'danger' : 'warning';
  }

  protected tseKey(r: ReceiptSummary): string {
    if (r.fiscalStatus === 'Signed') return r.fiscalIsTest ? 'sales.receipts.tseTest' : 'sales.receipts.tseSigned';
    if (r.fiscalStatus === 'Failed') return 'sales.receipts.tseOutage';
    return 'sales.receipts.tseNone';
  }

  protected link(r: ReceiptSummary): string[] {
    return receiptLink(r.id);
  }

  protected open(r: ReceiptSummary): void {
    void this.router.navigate(this.link(r));
  }
}
