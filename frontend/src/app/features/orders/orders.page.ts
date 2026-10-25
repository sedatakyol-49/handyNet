import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { OrdersApi } from '../../core/api/handynet-api';
import { ORDER_STATUSES } from '../../core/api/models';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';

@Component({
  selector: 'hn-orders-page',
  imports: [RouterLink, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-page-header
      [title]="(role === 'sales' ? 'orders.salesTitle' : 'orders.purchasesTitle') | t"
      [description]="(role === 'sales' ? 'orders.salesSubtitle' : 'orders.purchasesSubtitle') | t"
    />

    <div class="mb-3 flex flex-wrap items-center gap-2">
      <div class="flex flex-wrap gap-1" role="group" [attr.aria-label]="'common.status' | t">
        <button type="button" class="min-h-11 rounded-full border px-3 text-sm transition-colors" [attr.aria-pressed]="status() === ''"
          [class]="status() === '' ? 'border-accent-600 bg-accent-50 font-semibold text-accent-800' : 'border-slate-200 bg-white hover:border-accent-600'"
          (click)="setStatus('')">{{ 'common.all' | t }}</button>
        @for (s of statuses; track s) {
          <button type="button" class="min-h-11 rounded-full border px-3 text-sm transition-colors" [attr.aria-pressed]="status() === s"
            [class]="status() === s ? 'border-accent-600 bg-accent-50 font-semibold text-accent-800' : 'border-slate-200 bg-white hover:border-accent-600'"
            (click)="setStatus(s)" [attr.data-status]="s">{{ s | tEnum: 'orderStatus' }}</button>
        }
      </div>
      <button hnButton size="sm" type="button" class="ms-auto" [loading]="orders.loading()" (click)="orders.reload()">{{ 'common.refresh' | t }}</button>
    </div>

    <!-- phone/tablet: cards; desktop: table -->
    <ul class="mb-3 grid gap-3 sm:grid-cols-2 md:hidden" data-testid="orders-cards">
      @for (o of orders.value()?.items ?? []; track o.id) {
        <li>
          <a [routerLink]="['/app/bestellungen', o.id]" class="flex h-full flex-col rounded-xl border border-slate-200 bg-white p-4 transition hover:border-accent-600 hover:shadow-md focus-visible:outline-2 focus-visible:outline-accent-600">
            <div class="flex items-start justify-between gap-2">
              <span class="hn-mono text-xs font-semibold">{{ o.number }}</span>
              <hn-status-badge [status]="o.status" kind="orderStatus" />
            </div>
            <p class="mt-1 truncate text-sm font-medium">{{ role === 'sales' ? o.buyerName : o.sellerName }}</p>
            <p class="text-xs text-slate-500">{{ o.placedAt | hnDate }} · {{ 'orders.positionsCount' | t: { count: o.lines.length } }}</p>
            <p class="mt-auto pt-2 text-end text-lg font-bold tabular-nums">{{ o.grossTotalMinor | money: o.currency }}</p>
          </a>
        </li>
      } @empty {
        @if (!orders.loading()) {
          <li class="sm:col-span-2"><div class="hn-card"><hn-empty-state [title]="'orders.empty' | t" [text]="(role === 'sales' ? 'orders.emptySales' : 'orders.emptyPurchases') | t" /></div></li>
        }
      }
    </ul>

    <hn-banner class="mb-3" [error]="orders.error()" />

    <div class="md:hidden">
      <hn-pagination [page]="page()" [pageSize]="pageSize" [totalCount]="orders.value()?.totalCount ?? 0" (pageChange)="page.set($event)" />
    </div>
    <hn-table class="hidden md:block" [loading]="orders.loading()">
      <table class="hn-table" data-testid="orders-table">
        <thead>
          <tr>
            <th scope="col">{{ 'orders.number' | t }}</th>
            <th scope="col">{{ 'orders.placedAt' | t }}</th>
            <th scope="col">{{ (role === 'sales' ? 'orders.buyer' : 'orders.seller') | t }}</th>
            <th scope="col">{{ 'orders.reference' | t }}</th>
            <th scope="col" class="num">{{ 'orders.lines' | t }}</th>
            <th scope="col" class="num">{{ 'common.net' | t }}</th>
            <th scope="col" class="num">{{ 'common.gross' | t }}</th>
            <th scope="col">{{ 'common.status' | t }}</th>
          </tr>
        </thead>
        <tbody>
          @for (o of orders.value()?.items ?? []; track o.id) {
            <tr>
              <td><a class="hn-link font-mono text-xs" [routerLink]="['/app/bestellungen', o.id]">{{ o.number }}</a></td>
              <td class="whitespace-nowrap">{{ o.placedAt | hnDate }}</td>
              <td>{{ role === 'sales' ? o.buyerName : o.sellerName }}</td>
              <td class="text-slate-600">{{ o.buyerReference ?? '–' }}</td>
              <td class="num">{{ o.lines.length }}</td>
              <td class="num">{{ o.netTotalMinor | money: o.currency }}</td>
              <td class="num font-medium">{{ o.grossTotalMinor | money: o.currency }}</td>
              <td><hn-status-badge [status]="o.status" kind="orderStatus" /></td>
            </tr>
          } @empty {
            @if (!orders.loading()) {
              <tr><td colspan="8"><hn-empty-state [title]="'orders.empty' | t" [text]="(role === 'sales' ? 'orders.emptySales' : 'orders.emptyPurchases') | t" /></td></tr>
            }
          }
        </tbody>
      </table>
      <div footer class="border-t border-slate-200 px-2">
        <hn-pagination [page]="page()" [pageSize]="pageSize" [totalCount]="orders.value()?.totalCount ?? 0" (pageChange)="page.set($event)" />
      </div>
    </hn-table>
  `,
})
export class OrdersPage {
  private readonly api = inject(OrdersApi);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  protected readonly role: 'buyer' | 'sales' = this.route.snapshot.data['role'] === 'sales' ? 'sales' : 'buyer';
  protected readonly statuses = ORDER_STATUSES;
  protected readonly status = signal(this.route.snapshot.queryParamMap.get('status') ?? '');
  protected readonly page = signal(1);
  protected readonly pageSize = 25;

  protected readonly orders = createQuery(
    () => ({ role: this.role, status: this.status(), page: this.page(), pageSize: this.pageSize }),
    (q) => this.api.list(q),
  );

  protected setStatus(value: string): void {
    this.status.set(value);
    this.page.set(1);
    void this.router.navigate([], { queryParams: { status: value || null }, replaceUrl: true });
  }
}
