import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AdminDisputesApi } from '../../core/api/handynet-api';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';
import { AdminTabsComponent } from './admin-tabs';

/** Platform admin: queue of escalated disputes (oldest escalation first as delivered by the API). */
@Component({
  selector: 'hn-admin-disputes-page',
  imports: [RouterLink, AdminTabsComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-page-header [title]="'admin.title' | t" [description]="'admin.disputes.subtitle' | t">
      <button hnButton type="button" [loading]="queue.loading()" (click)="queue.reload()">{{ 'common.refresh' | t }}</button>
    </hn-page-header>
    <hn-admin-tabs />

    <hn-banner class="mb-3" [error]="queue.error()" />
    <p class="mb-2 text-xs text-slate-600">{{ 'admin.disputes.idsHint' | t }}</p>

    <hn-table [loading]="queue.loading()">
      <table class="hn-table" data-testid="admin-disputes-table">
        <thead>
          <tr>
            <th scope="col">{{ 'admin.disputes.escalatedAt' | t }}</th>
            <th scope="col">{{ 'admin.disputes.order' | t }}</th>
            <th scope="col">{{ 'disputes.reason' | t }}</th>
            <th scope="col">{{ 'orders.buyer' | t }}</th>
            <th scope="col">{{ 'orders.seller' | t }}</th>
            <th scope="col"><span class="sr-only">{{ 'common.actions' | t }}</span></th>
          </tr>
        </thead>
        <tbody>
          @for (d of queue.value() ?? []; track d.id) {
            <tr [attr.data-order-id]="d.orderId" [attr.data-dispute-id]="d.id">
              <td class="whitespace-nowrap">{{ d.escalatedAt | hnDate }}</td>
              <td class="hn-mono text-xs" [title]="d.orderId">{{ d.orderNumber }}</td>
              <td>{{ d.reason | tEnum: 'disputeReason' }}</td>
              <td [title]="d.buyerOrganizationId">{{ d.buyerName }}</td>
              <td [title]="d.sellerOrganizationId">{{ d.sellerName }}</td>
              <td class="text-end">
                <a hnButton size="sm" variant="primary" [routerLink]="['/app/administration/reklamationen', d.id]" data-testid="admin-dispute-open"
                  [attr.aria-label]="'admin.disputes.openLabel' | t: { order: d.orderNumber }">
                  {{ 'admin.disputes.open' | t }}
                </a>
              </td>
            </tr>
          } @empty {
            @if (!queue.loading()) {
              <tr><td colspan="6"><hn-empty-state [title]="'admin.disputes.empty' | t" [text]="'admin.disputes.emptyText' | t" /></td></tr>
            }
          }
        </tbody>
      </table>
    </hn-table>
  `,
})
export class AdminDisputesPage {
  private readonly api = inject(AdminDisputesApi);
  protected readonly queue = createQuery(
    () => true,
    () => this.api.escalated(),
  );
}
