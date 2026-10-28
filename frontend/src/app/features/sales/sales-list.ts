import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { SalesApi } from '../../core/api/handynet-api';
import { InvoiceListItem, QuotationSummary } from '../../core/api/models';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';
import { SalesNav, receiptLink } from './sales-nav';
import { invoiceBadge, quotationTone } from './sales.logic';

type Kind = 'quotations' | 'invoices';

/**
 * List of quotations or invoices with filters (search, status, payment state, overdue) — shared by the POS app and the
 * back office. Phone: cards; wider screens: a table. Invoices include those issued for POS sales (read only).
 */
@Component({
  selector: 'hn-sales-list',
  imports: [RouterLink, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    <hn-page-header [title]="(kind() === 'quotations' ? 'sales.quotations.title' : 'sales.invoices.title') | t"
      [description]="(kind() === 'quotations' ? 'sales.quotations.description' : 'sales.invoices.description') | t">
      <a hnButton variant="primary" class="min-h-11" [routerLink]="[...base(), 'neu']" data-testid="sales-new">
        {{ (kind() === 'quotations' ? 'sales.quotations.new' : 'sales.invoices.new') | t }}
      </a>
    </hn-page-header>

    <nav class="mb-3 flex flex-wrap gap-1.5" [attr.aria-label]="'sales.switch' | t">
      <a [routerLink]="nav().quotations" class="pos-chip" [attr.aria-pressed]="kind() === 'quotations'" data-testid="sales-tab-quotations">{{ 'sales.quotations.title' | t }}</a>
      <a [routerLink]="nav().invoices" class="pos-chip" [attr.aria-pressed]="kind() === 'invoices'" data-testid="sales-tab-invoices">
        {{ 'sales.invoices.title' | t }}
        @if (summary.value(); as s) {
          @if (s.overdueCount > 0) {
            <span class="rounded-full bg-red-700 px-1.5 text-[11px] font-bold text-white" data-testid="sales-overdue-badge">{{ s.overdueCount }}</span>
          }
        }
      </a>
      @if (nav().receipts; as receipts) {
        <a [routerLink]="receipts" class="pos-chip" aria-pressed="false" data-testid="sales-tab-receipts">{{ 'sales.receipts.title' | t }}</a>
      }
    </nav>

    <form class="mb-3 grid gap-2 sm:grid-cols-2 lg:grid-cols-5" role="search" (submit)="$event.preventDefault(); apply()">
      <div class="lg:col-span-2">
        <label for="sales-search" class="mb-1 block text-xs font-medium text-slate-700">{{ 'sales.list.search' | t }}</label>
        <input hnInput id="sales-search" type="search" class="h-11" autocomplete="off" [placeholder]="'sales.list.searchPlaceholder' | t" [value]="search()"
          (input)="search.set($any($event.target).value)" data-testid="sales-search" />
      </div>
      <div>
        <label for="sales-status" class="mb-1 block text-xs font-medium text-slate-700">{{ 'sales.list.status' | t }}</label>
        <select hnSelect id="sales-status" class="h-11" [value]="status()" (change)="status.set($any($event.target).value); apply()" data-testid="sales-status">
          <option value="">{{ 'sales.list.all' | t }}</option>
          @for (s of statuses(); track s) {
            <option [value]="s">{{ (kind() === 'quotations' ? 'sales.quotationStatus.' : 'sales.invoiceStatus.') + s | t }}</option>
          }
        </select>
      </div>
      @if (kind() === 'invoices') {
        <div>
          <label for="sales-payment" class="mb-1 block text-xs font-medium text-slate-700">{{ 'sales.list.payment' | t }}</label>
          <select hnSelect id="sales-payment" class="h-11" [value]="payment()" (change)="payment.set($any($event.target).value); apply()" data-testid="sales-payment">
            <option value="">{{ 'sales.list.all' | t }}</option>
            <option value="Open">{{ 'sales.badge.Open' | t }}</option>
            <option value="PartiallyPaid">{{ 'sales.badge.PartiallyPaid' | t }}</option>
            <option value="Paid">{{ 'sales.badge.Paid' | t }}</option>
          </select>
        </div>
        <label class="flex min-h-11 items-center gap-2 self-end text-sm">
          <input type="checkbox" class="hn-checkbox" [checked]="overdue()" (change)="overdue.set($any($event.target).checked); apply()" data-testid="sales-overdue" />
          {{ 'sales.list.overdueOnly' | t }}
        </label>
      }
      <div class="flex items-end gap-2 sm:col-span-2 lg:col-span-5">
        <button hnButton type="submit" class="min-h-11" data-testid="sales-apply">{{ 'sales.list.apply' | t }}</button>
        <button hnButton variant="ghost" type="button" class="min-h-11" (click)="reset()">{{ 'sales.list.reset' | t }}</button>
      </div>
    </form>

    @if (activeError(); as e) {
      <hn-banner class="mb-3" [error]="e" />
    }

    @if (kind() === 'quotations') {
      @let rows = quotations.value()?.items ?? [];
      <ul class="space-y-2 sm:hidden" data-testid="sales-cards">
        @for (q of rows; track q.id) {
          <li>
            <a [routerLink]="[...base(), q.id]" class="block rounded-lg border border-slate-200 bg-white p-3 hover:border-accent-600 focus-visible:border-accent-600">
              <span class="flex items-center justify-between gap-2"><span class="font-semibold">{{ q.number }}</span>
                <hn-status-badge [status]="q.status" kind="sales.quotationStatus" [tone]="qTone(q)" /></span>
              <span class="block text-sm text-slate-700">{{ q.customerCompany || q.customerName }}</span>
              <span class="flex justify-between text-xs text-slate-500"><span>{{ q.issueDate | hnDate }}</span><span class="font-semibold text-slate-900">{{ q.totalGrossMinor | money }}</span></span>
            </a>
          </li>
        } @empty {
          <li class="py-8 text-center text-sm text-slate-500">{{ (quotations.loading() ? 'common.loading' : 'sales.list.empty') | t }}</li>
        }
      </ul>
      <hn-table class="hidden sm:block" [loading]="quotations.loading()">
        <table class="hn-table" data-testid="sales-table">
          <thead>
            <tr>
              <th>{{ 'sales.fields.number' | t }}</th>
              <th>{{ 'sales.fields.customer' | t }}</th>
              <th>{{ 'sales.fields.date' | t }}</th>
              <th>{{ 'sales.fields.validUntil' | t }}</th>
              <th>{{ 'sales.list.status' | t }}</th>
              <th class="num">{{ 'sales.fields.total' | t }}</th>
            </tr>
          </thead>
          <tbody>
            @for (q of rows; track q.id) {
              <tr class="hn-row-link" tabindex="0" (click)="open(q.id)" (keydown.enter)="open(q.id)" [attr.data-number]="q.number">
                <td><a [routerLink]="[...base(), q.id]" class="hn-link font-medium" (click)="$event.stopPropagation()">{{ q.number }}</a></td>
                <td>{{ q.customerCompany || q.customerName }}@if (q.customerCompany) {<span class="block text-xs text-slate-500">{{ q.customerName }}</span>}</td>
                <td>{{ q.issueDate | hnDate }}</td>
                <td>{{ q.validUntil | hnDate }}</td>
                <td><hn-status-badge [status]="q.status" kind="sales.quotationStatus" [tone]="qTone(q)" /></td>
                <td class="num">{{ q.totalGrossMinor | money }}</td>
              </tr>
            } @empty {
              <tr class="hn-row-static"><td colspan="6" class="py-8 text-center text-slate-500">{{ (quotations.loading() ? 'common.loading' : 'sales.list.empty') | t }}</td></tr>
            }
          </tbody>
        </table>
        <hn-pagination footer [page]="page()" [pageSize]="pageSize" [totalCount]="quotations.value()?.totalCount ?? 0" (pageChange)="page.set($event)" />
      </hn-table>
    } @else {
      @let rows = invoices.value()?.items ?? [];
      <ul class="space-y-2 sm:hidden" data-testid="sales-cards">
        @for (i of rows; track i.id) {
          @let b = badge(i);
          <li>
            <a [routerLink]="link(i)" class="block rounded-lg border border-slate-200 bg-white p-3 hover:border-accent-600 focus-visible:border-accent-600">
              <span class="flex items-center justify-between gap-2"><span class="font-semibold">{{ i.number ?? ('sales.invoices.draft' | t) }}</span>
                <hn-status-badge [status]="b.key" kind="sales.badge" [tone]="b.tone" /></span>
              <span class="block text-sm text-slate-700">{{ i.customerCompany || i.customerName || '–' }}</span>
              <span class="flex justify-between text-xs text-slate-500"><span>{{ i.invoiceDate | hnDate }}</span><span class="font-semibold text-slate-900">{{ i.totalGrossMinor | money }}</span></span>
            </a>
          </li>
        } @empty {
          <li class="py-8 text-center text-sm text-slate-500">{{ (invoices.loading() ? 'common.loading' : 'sales.list.empty') | t }}</li>
        }
      </ul>
      <hn-table class="hidden sm:block" [loading]="invoices.loading()">
        <table class="hn-table" data-testid="sales-table">
          <thead>
            <tr>
              <th>{{ 'sales.fields.number' | t }}</th>
              <th>{{ 'sales.fields.customer' | t }}</th>
              <th>{{ 'sales.fields.invoiceDate' | t }}</th>
              <th>{{ 'sales.fields.dueDate' | t }}</th>
              <th>{{ 'sales.list.status' | t }}</th>
              <th class="num">{{ 'sales.fields.total' | t }}</th>
              <th class="num">{{ 'sales.fields.open' | t }}</th>
            </tr>
          </thead>
          <tbody>
            @for (i of rows; track i.id) {
              @let b = badge(i);
              <tr class="hn-row-link" tabindex="0" (click)="go(i)" (keydown.enter)="go(i)" [attr.data-number]="i.number">
                <td>
                  <a [routerLink]="link(i)" class="hn-link font-medium" (click)="$event.stopPropagation()">{{ i.number ?? ('sales.invoices.draft' | t) }}</a>
                  @if (i.origin === 'Receipt') {
                    <span class="block text-xs text-slate-500">{{ 'sales.invoices.fromReceipt' | t }}</span>
                  } @else if (i.kind === 'CreditNote') {
                    <span class="block text-xs text-slate-500">{{ 'sales.invoices.creditNote' | t }}</span>
                  }
                </td>
                <td>{{ i.customerCompany || i.customerName || '–' }}</td>
                <td>{{ i.invoiceDate | hnDate }}</td>
                <td [class.text-red-700]="i.overdue" [class.font-semibold]="i.overdue">{{ i.kind === 'Invoice' && i.origin === 'Sale' ? (i.dueDate | hnDate) : '' }}</td>
                <td><hn-status-badge [status]="b.key" kind="sales.badge" [tone]="b.tone" /></td>
                <td class="num">{{ i.totalGrossMinor | money }}</td>
                <td class="num">{{ openOf(i) | money }}</td>
              </tr>
            } @empty {
              <tr class="hn-row-static"><td colspan="7" class="py-8 text-center text-slate-500">{{ (invoices.loading() ? 'common.loading' : 'sales.list.empty') | t }}</td></tr>
            }
          </tbody>
        </table>
        <hn-pagination footer [page]="page()" [pageSize]="pageSize" [totalCount]="invoices.value()?.totalCount ?? 0" (pageChange)="page.set($event)" />
      </hn-table>
    }
  `,
})
export class SalesListComponent {
  private readonly api = inject(SalesApi);
  readonly kind = input.required<Kind>();
  readonly nav = input.required<SalesNav>();
  /** Initial "overdue only" filter (e.g. from the navigation badge). */
  readonly overdueOnly = input(false);

  protected readonly pageSize = 25;
  protected readonly page = signal(1);
  protected readonly search = signal('');
  protected readonly status = signal('');
  protected readonly payment = signal('');
  protected readonly overdue = signal(false);
  private readonly applied = signal({ search: '', status: '', payment: '', overdue: false });

  protected readonly base = computed(() => (this.kind() === 'quotations' ? this.nav().quotations : this.nav().invoices));
  protected readonly statuses = computed(() => (this.kind() === 'quotations' ? ['Draft', 'Sent', 'Accepted', 'Rejected', 'Expired'] : ['Draft', 'Issued']));

  protected readonly quotations = createQuery(
    () => ({ kind: this.kind(), f: this.applied(), page: this.page() }),
    (p) => this.api.quotations({ search: p.f.search || null, status: p.f.status || null, page: p.page, pageSize: this.pageSize }),
  );
  protected readonly invoices = createQuery(
    () => ({ kind: this.kind(), f: this.applied(), page: this.page() }),
    (p) => this.api.invoices({
      search: p.f.search || null, status: p.f.status || null, paymentState: p.f.payment || null, overdue: p.f.overdue || null, page: p.page,
      pageSize: this.pageSize,
    }),
  );
  protected readonly summary = createQuery(
    () => this.kind(),
    () => this.api.summary(),
  );
  protected readonly activeError = computed(() => (this.kind() === 'quotations' ? this.quotations.error() : this.invoices.error()));

  constructor() {
    effect(() => {
      const initial = this.overdueOnly();
      untracked(() => {
        if (initial) {
          this.overdue.set(true);
          this.apply();
        }
      });
    });
  }

  protected apply(): void {
    this.applied.set({ search: this.search().trim(), status: this.status(), payment: this.payment(), overdue: this.overdue() });
    this.page.set(1);
  }

  protected reset(): void {
    this.search.set('');
    this.status.set('');
    this.payment.set('');
    this.overdue.set(false);
    this.apply();
  }

  protected qTone(q: QuotationSummary) {
    return quotationTone(q.status);
  }

  protected badge(i: InvoiceListItem) {
    return invoiceBadge(i);
  }

  protected openOf(i: InvoiceListItem): number {
    return i.kind === 'Invoice' && i.status === 'Issued' && !i.cancelled ? Math.max(0, i.totalGrossMinor - i.paidMinor) : 0;
  }

  protected link(i: InvoiceListItem): string[] {
    return i.origin === 'Receipt' && i.receiptId ? receiptLink(i.receiptId) : [...this.nav().invoices, i.id];
  }

  private readonly router = inject(Router);

  protected open(id: string): void {
    void this.router.navigate([...this.base(), id]);
  }

  protected go(i: InvoiceListItem): void {
    void this.router.navigate(this.link(i));
  }
}

