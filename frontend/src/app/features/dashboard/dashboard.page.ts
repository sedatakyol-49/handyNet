import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { OrdersApi } from '../../core/api/handynet-api';
import { AuthService } from '../../core/auth/auth.service';
import { SessionStore } from '../../core/auth/session.store';
import { UI } from '../../shared/ui';
import { NAV_ITEMS } from '../shell/navigation';

interface Counter {
  labelKey: string;
  link: string;
  queryParams: Record<string, string>;
  value: number | null;
}

@Component({
  selector: 'hn-dashboard-page',
  imports: [RouterLink, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './dashboard.page.html',
})
export class DashboardPage {
  protected readonly session = inject(SessionStore);
  private readonly auth = inject(AuthService);
  private readonly orders = inject(OrdersApi);

  protected readonly refreshing = signal(false);
  protected readonly counters = signal<Counter[]>([]);

  /** The POS is a standalone app (/kasse), launched from here instead of a menu entry. */
  protected readonly canUsePos = computed(() => this.session.allows('Pos', 'pos.sell'));

  protected readonly quickLinks = computed(() =>
    NAV_ITEMS.filter((i) => i.path !== '/app/uebersicht' && !i.adminOnly && !i.always && this.session.allows(i.module, i.permission)),
  );

  constructor() {
    void this.loadCounters();
  }

  protected async refreshStatus(): Promise<void> {
    this.refreshing.set(true);
    await this.auth.loadMe(true);
    this.refreshing.set(false);
    void this.loadCounters();
  }

  /** Real counts from the order API (no synthetic metrics). */
  private async loadCounters(): Promise<void> {
    const s = this.session;
    const defs: Omit<Counter, 'value'>[] = [];
    if (s.allows('B2BSales', 'orders.read')) {
      defs.push({ labelKey: 'dashboard.salesToConfirm', link: '/app/bestellungen/verkauf', queryParams: { status: 'Placed' } });
      defs.push({ labelKey: 'dashboard.salesToShip', link: '/app/bestellungen/verkauf', queryParams: { status: 'Confirmed' } });
      defs.push({ labelKey: 'dashboard.salesDisputed', link: '/app/bestellungen/verkauf', queryParams: { status: 'Disputed' } });
    }
    if (s.allows('B2BPurchasing', 'orders.read')) {
      defs.push({ labelKey: 'dashboard.purchasesOpen', link: '/app/bestellungen/einkauf', queryParams: { status: 'Placed' } });
      defs.push({ labelKey: 'dashboard.purchasesToInspect', link: '/app/bestellungen/einkauf', queryParams: { status: 'Delivered' } });
      defs.push({ labelKey: 'dashboard.purchasesDisputed', link: '/app/bestellungen/einkauf', queryParams: { status: 'Disputed' } });
    }
    this.counters.set(defs.map((d) => ({ ...d, value: null })));
    const values = await Promise.all(
      defs.map((d) =>
        firstValueFrom(
          this.orders.list({ role: d.link.endsWith('verkauf') ? 'sales' : 'buyer', status: d.queryParams['status'], pageSize: 1 }),
        )
          .then((r) => r.totalCount)
          .catch(() => null),
      ),
    );
    this.counters.set(defs.map((d, i) => ({ ...d, value: values[i] })));
  }
}
