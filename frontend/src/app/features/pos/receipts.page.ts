import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { of } from 'rxjs';
import { OrganizationApi, PosApi } from '../../core/api/handynet-api';
import { POS_PAYMENT_METHODS, PosCustomer, ReceiptKind } from '../../core/api/models';
import { SessionStore } from '../../core/auth/session.store';
import { I18nService } from '../../core/i18n/i18n.service';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';
import { PosCustomerDialogComponent } from './pos-customer-dialog';
import { PosSubnavComponent } from './pos-subnav';
import {
  EMPTY_SALES_FILTER,
  SalesFilter,
  SalesScope,
  activeFilterCount,
  effectiveScope,
  filterFromParams,
  filterToParams,
  filterToQuery,
} from './sales-filter';

/**
 * Order history of a register (`GET /api/pos/sales`): scope (current session / this register / all registers) and a
 * filter bar (receipt number, date range, amount range, cashier, customer, payment method, kind). Filters live in the
 * URL, so a filtered list can be reloaded or shared.
 */
@Component({
  selector: 'hn-pos-receipts-page',
  host: { class: 'pos-page block' },
  imports: [RouterLink, PosCustomerDialogComponent, PosSubnavComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-pos-subnav [registerId]="registerId()" />
    <hn-page-header [title]="'pos.receipts.title' | t" [description]="register.value()?.name ?? null">
      <button hnButton type="button" class="min-h-11" [loading]="receipts.loading()" (click)="receipts.reload()">{{ 'common.refresh' | t }}</button>
    </hn-page-header>

    <div class="mb-3 grid grid-cols-3 gap-1.5 sm:max-w-xl" role="group" [attr.aria-label]="'pos.receipts.scope' | t">
      @for (s of scopes; track s) {
        <button type="button" class="pos-mode" [attr.aria-pressed]="scope() === s" [disabled]="s === 'session' && !sessionId()" (click)="setScope(s)" [attr.data-testid]="'pos-receipts-scope-' + s">
          {{ 'pos.receipts.scopes.' + s | t }}
        </button>
      }
    </div>

    <form class="hn-card mb-3 p-3" (submit)="$event.preventDefault(); apply()" novalidate [attr.aria-label]="'pos.receipts.filters' | t" data-testid="pos-receipts-filter">
      <div class="grid grid-cols-2 gap-3 sm:grid-cols-4 xl:grid-cols-8">
        <hn-field [label]="'pos.receipt.number' | t" for="pos-f-number" [errors]="err('number')">
          <input hnInput id="pos-f-number" inputmode="numeric" class="h-11" autocomplete="off" [value]="draft().number" (input)="set('number', $any($event.target).value)" data-testid="pos-f-number" />
        </hn-field>
        <hn-field [label]="'pos.receipts.from' | t" for="pos-f-from" [errors]="err('from')">
          <input hnInput id="pos-f-from" type="date" class="h-11" [value]="draft().from" (input)="set('from', $any($event.target).value)" data-testid="pos-f-from" />
        </hn-field>
        <hn-field [label]="'pos.receipts.to' | t" for="pos-f-to" [errors]="err('to') || err('dateRange')">
          <input hnInput id="pos-f-to" type="date" class="h-11" [value]="draft().to" (input)="set('to', $any($event.target).value)" data-testid="pos-f-to" />
        </hn-field>
        <hn-field [label]="'pos.receipts.minTotal' | t" for="pos-f-min" [errors]="err('minTotal')">
          <input hnInput id="pos-f-min" inputmode="decimal" class="h-11 text-end" placeholder="0,00" autocomplete="off" [value]="draft().minTotal" (input)="set('minTotal', $any($event.target).value)" data-testid="pos-f-min" />
        </hn-field>
        <hn-field [label]="'pos.receipts.maxTotal' | t" for="pos-f-max" [errors]="err('maxTotal') || err('amountRange')">
          <input hnInput id="pos-f-max" inputmode="decimal" class="h-11 text-end" placeholder="0,00" autocomplete="off" [value]="draft().maxTotal" (input)="set('maxTotal', $any($event.target).value)" data-testid="pos-f-max" />
        </hn-field>
        <hn-field [label]="'pos.receipt.cashier' | t" for="pos-f-cashier">
          <select hnSelect id="pos-f-cashier" class="h-11" (change)="set('cashierId', $any($event.target).value)" data-testid="pos-f-cashier">
            <option value="" [selected]="!draft().cashierId">{{ 'pos.receipts.any' | t }}</option>
            @for (c of cashiers(); track c.id) {
              <option [value]="c.id" [selected]="c.id === draft().cashierId">{{ c.name }}</option>
            }
          </select>
        </hn-field>
        <hn-field [label]="'pos.receipts.paymentMethod' | t" for="pos-f-method">
          <select hnSelect id="pos-f-method" class="h-11" (change)="set('paymentMethod', $any($event.target).value)" data-testid="pos-f-method">
            <option value="" [selected]="!draft().paymentMethod">{{ 'pos.receipts.any' | t }}</option>
            @for (m of methods; track m) {
              <option [value]="m" [selected]="m === draft().paymentMethod">{{ m | tEnum: 'posPaymentMethod' }}</option>
            }
          </select>
        </hn-field>
        <hn-field [label]="'pos.receipts.kind' | t" for="pos-f-kind">
          <select hnSelect id="pos-f-kind" class="h-11" (change)="set('kind', $any($event.target).value)" data-testid="pos-f-kind">
            <option value="" [selected]="!draft().kind">{{ 'pos.receipts.any' | t }}</option>
            @for (k of kinds; track k) {
              <option [value]="k" [selected]="k === draft().kind">{{ k | tEnum: 'receiptKind' }}</option>
            }
          </select>
        </hn-field>
      </div>
      <div class="mt-3 flex flex-wrap items-center gap-2">
        <span class="text-xs font-medium text-slate-700">{{ 'pos.receipt.customer' | t }}:</span>
        @if (draft().customerId) {
          <span class="inline-flex min-h-11 items-center gap-2 rounded-full border border-slate-300 bg-white ps-3 text-sm" data-testid="pos-f-customer">
            {{ draft().customerName || ('pos.receipts.customerSelected' | t) }}
            <button type="button" class="grid size-11 place-items-center rounded-full text-slate-500 hover:text-red-700" (click)="clearCustomer()" [attr.aria-label]="'pos.receipts.clearCustomer' | t">✕</button>
          </span>
        } @else {
          <button hnButton type="button" class="min-h-11" (click)="pickCustomer.set(true)" data-testid="pos-f-customer-pick">{{ 'pos.customers.choose' | t }}</button>
        }
        <span class="ms-auto flex gap-2">
          <button hnButton variant="ghost" type="button" class="min-h-11" [disabled]="!activeCount() && !dirty()" (click)="reset()" data-testid="pos-f-reset">{{ 'pos.receipts.reset' | t }}</button>
          <button hnButton variant="primary" type="submit" class="min-h-11" data-testid="pos-f-apply">
            {{ 'pos.receipts.apply' | t }}
            @if (activeCount()) {
              <span class="ms-1 rounded-full bg-white/20 px-1.5 text-xs tabular-nums">{{ activeCount() }}</span>
            }
          </button>
        </span>
      </div>
    </form>

    <hn-banner class="mb-3" [error]="receipts.error()" />
    <hn-table [loading]="receipts.loading()">
      <table class="hn-table" data-testid="pos-receipts">
        <thead>
          <tr>
            <th scope="col" class="num">{{ 'pos.receipt.number' | t }}</th>
            <th scope="col">{{ 'pos.receipts.kind' | t }}</th>
            <th scope="col">{{ 'pos.receipt.date' | t }}</th>
            <th scope="col">{{ 'pos.receipt.cashier' | t }}</th>
            <th scope="col">{{ 'pos.receipt.customer' | t }}</th>
            <th scope="col">{{ 'pos.receipts.paymentMethod' | t }}</th>
            <th scope="col" class="num">{{ 'pos.receipt.total' | t }}</th>
            <th scope="col" class="num">{{ 'pos.detail.reprints' | t }}</th>
            <th scope="col"><span class="sr-only">{{ 'common.actions' | t }}</span></th>
          </tr>
        </thead>
        <tbody>
          @for (r of receipts.value()?.items ?? []; track r.id) {
            <tr [attr.data-receipt]="r.number">
              <td class="num font-semibold">{{ r.number }}</td>
              <td><hn-status-badge [status]="r.kind" kind="receiptKind" [tone]="r.kind === 'Sale' ? 'success' : r.kind === 'Storno' ? 'danger' : 'warning'" /></td>
              <td class="text-xs whitespace-nowrap">{{ r.createdAt | hnDate }}</td>
              <td class="text-sm">{{ r.createdByName ?? '–' }}</td>
              <td class="text-sm" data-testid="pos-receipt-customer">{{ r.customerName ?? '–' }}</td>
              <td class="text-sm">
                @for (m of r.paymentMethods ?? []; track m; let last = $last) {
                  {{ m | tEnum: 'posPaymentMethod' }}{{ last ? '' : ', ' }}
                }
              </td>
              <td class="num"><bdi dir="ltr" [class.text-red-700]="r.totalGrossMinor < 0">{{ r.totalGrossMinor | money }}</bdi></td>
              <td class="num" data-testid="pos-receipt-reprints">{{ r.reprintCount ?? 0 }}</td>
              <td class="text-end"><a class="hn-link inline-flex min-h-11 items-center" [routerLink]="['/kasse/belege', r.id]">{{ 'pos.receipts.open' | t }}</a></td>
            </tr>
          } @empty {
            <tr><td colspan="9" class="py-8 text-center text-sm text-slate-500">{{ (activeCount() ? 'pos.receipts.noMatch' : 'pos.receipts.empty') | t }}</td></tr>
          }
        </tbody>
      </table>
      <hn-pagination footer [page]="page()" [pageSize]="pageSize" [totalCount]="receipts.value()?.totalCount ?? 0" (pageChange)="page.set($event)" />
    </hn-table>

    <hn-pos-customer-dialog [open]="pickCustomer()" mode="pick" [attachedId]="draft().customerId || null" (chosen)="chooseCustomer($event)" (closed)="pickCustomer.set(false)" />
  `,
})
export class PosReceiptsPage {
  private readonly api = inject(PosApi);
  private readonly orgApi = inject(OrganizationApi);
  private readonly store = inject(SessionStore);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly i18n = inject(I18nService);
  readonly registerId = input.required<string>();

  protected readonly pageSize = 50;
  protected readonly page = signal(1);
  protected readonly scopes: SalesScope[] = ['session', 'register', 'all'];
  protected readonly methods = POS_PAYMENT_METHODS;
  protected readonly kinds: ReceiptKind[] = ['Sale', 'Return', 'Storno'];
  protected readonly pickCustomer = signal(false);

  private readonly params = toSignal(this.route.queryParamMap, { requireSync: true });
  /** Filter as applied (from the URL). */
  protected readonly filter = computed(() => {
    const p = this.params();
    return filterFromParams(Object.fromEntries(p.keys.map((k) => [k, p.get(k)])));
  });
  /** Filter as being edited in the bar. */
  protected readonly draft = signal<SalesFilter>(EMPTY_SALES_FILTER);
  protected readonly activeCount = computed(() => activeFilterCount(this.filter()));
  protected readonly dirty = computed(() => JSON.stringify(this.draft()) !== JSON.stringify(this.filter()));

  protected readonly register = createQuery(
    () => this.registerId(),
    (id) => this.api.register(id),
  );
  protected readonly sessionId = computed(() => this.register.value()?.openSessionId ?? null);
  protected readonly scope = computed(() => effectiveScope(this.filter(), !!this.sessionId()));
  private readonly draftCheck = computed(() => filterToQuery(this.draft(), { registerId: this.registerId(), sessionId: this.sessionId() }));
  private readonly showErrors = signal(false);

  protected readonly receipts = createQuery(
    () => ({
      ready: !!this.register.value() || !!this.register.error(),
      query: filterToQuery(this.filter(), { registerId: this.registerId(), sessionId: this.sessionId() }).query,
      page: this.page(),
    }),
    (q) => (q.ready ? this.api.receipts({ ...q.query, page: q.page, pageSize: this.pageSize }) : of(null)),
  );

  /** Cashier choices: the team (organization.manage), otherwise the cashiers seen in the list plus oneself. */
  private readonly staff = createQuery(
    () => this.store.can('organization.manage'),
    (allowed) => (allowed ? this.orgApi.staff() : of([])),
  );
  protected readonly cashiers = computed(() => {
    const map = new Map<string, string>();
    for (const s of this.staff.value() ?? []) map.set(s.userId, s.displayName);
    const me = this.store.me();
    if (me) map.set(me.userId, me.displayName);
    for (const r of this.receipts.value()?.items ?? []) if (r.createdBy && r.createdByName) map.set(r.createdBy, r.createdByName);
    return [...map.entries()].map(([id, name]) => ({ id, name })).sort((a, b) => a.name.localeCompare(b.name));
  });

  constructor() {
    effect(() => {
      const f = this.filter();
      untracked(() => {
        this.draft.set(f);
        this.page.set(1);
        this.showErrors.set(false);
      });
    });
  }

  protected err(field: 'number' | 'from' | 'to' | 'minTotal' | 'maxTotal' | 'dateRange' | 'amountRange'): string | null {
    if (!this.showErrors() || !this.draftCheck().errors[field]) return null;
    return field === 'dateRange' || field === 'amountRange' ? this.t(`pos.receipts.errors.${field}`) : this.t('pos.receipts.errors.invalid');
  }

  private t(key: string): string {
    return this.i18n.t(key);
  }

  protected set<K extends keyof SalesFilter>(key: K, value: SalesFilter[K]): void {
    this.draft.update((f) => ({ ...f, [key]: value }));
  }

  protected setScope(scope: SalesScope): void {
    this.navigate({ ...this.filter(), scope });
  }

  protected apply(): void {
    this.showErrors.set(true);
    if (!this.draftCheck().valid) return;
    this.navigate(this.draft());
  }

  protected reset(): void {
    this.navigate({ ...EMPTY_SALES_FILTER, scope: this.filter().scope });
  }

  protected chooseCustomer(c: PosCustomer): void {
    this.pickCustomer.set(false);
    this.navigate({ ...this.draft(), customerId: c.id, customerName: c.name });
  }

  protected clearCustomer(): void {
    this.navigate({ ...this.draft(), customerId: '', customerName: '' });
  }

  private navigate(f: SalesFilter): void {
    void this.router.navigate([], { relativeTo: this.route, queryParams: filterToParams(f), replaceUrl: true });
  }
}
