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

    <div class="mb-3 flex flex-wrap items-end gap-3">
      <hn-field [label]="'common.status' | t" for="order-status" class="w-48">
        <select hnSelect id="order-status" [value]="status()" (change)="setStatus($any($event.target).value)">
          <option value="">{{ 'common.all' | t }}</option>
          @for (s of statuses; track s) {
            <option [value]="s" [selected]="s === status()">{{ s | tEnum: 'orderStatus' }}</option>
          }
        </select>
      </hn-field>
      <button hnButton type="button" (click)="orders.reload()">{{ 'common.refresh' | t }}</button>
    </div>

    <hn-banner class="mb-3" [error]="orders.error()" />

    <hn-table [loading]="orders.loading()">
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
              <tr><td colspan="8"><hn-empty-state [title]="'orders.empty' | t" /></td></tr>
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
