import { OrderTimelineComponent } from './order-timeline';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Observable, firstValueFrom } from 'rxjs';
import { DisputesApi, OrdersApi, PaymentsApi, ShipmentsApi } from '../../core/api/handynet-api';
import { Order } from '../../core/api/models';
import { SessionStore } from '../../core/auth/session.store';
import { I18nService } from '../../core/i18n/i18n.service';
import { ToastService } from '../../core/ui-state/toast.service';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';
import { DisputeChange, OrderDisputeComponent } from './dispute/order-dispute';
import { OrderPaymentComponent } from './payment/order-payment';
import { OrderShippingComponent, ShippingChange } from './shipping/order-shipping';
import { hasOpenShipment } from './shipping/shipping.logic';

type ReasonAction = 'cancel' | 'reject';

/** Background processing (e.g. Manual shipment → Delivered after the buyer's receipt) runs every few seconds. */
const SHIPMENT_REFETCH_DELAY_MS = 4000;
/** The refund after a dispute (and the payout after completion) is processed by a background job a few seconds later. */
const PAYMENT_REFETCH_DELAY_MS = 5000;

@Component({
  selector: 'hn-order-detail-page',
  imports: [FormsModule, RouterLink, OrderShippingComponent, OrderPaymentComponent, OrderDisputeComponent, OrderTimelineComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './order-detail.page.html',
})
export class OrderDetailPage {
  private readonly api = inject(OrdersApi);
  private readonly shipmentsApi = inject(ShipmentsApi);
  private readonly paymentsApi = inject(PaymentsApi);
  private readonly disputesApi = inject(DisputesApi);
  private readonly session = inject(SessionStore);
  private readonly toast = inject(ToastService);
  private readonly i18n = inject(I18nService);

  /** Route parameter (component input binding). */
  readonly id = input.required<string>();

  protected readonly order = createQuery(
    () => this.id(),
    (id) => this.api.get(id),
  );
  protected readonly shipments = createQuery(
    () => this.id(),
    (id) => this.shipmentsApi.forOrder(id),
  );
  protected readonly payments = createQuery(
    () => this.id(),
    (id) => this.paymentsApi.forOrder(id),
  );
  protected readonly disputes = createQuery(
    () => this.id(),
    (id) => this.disputesApi.forOrder(id),
  );
  /** A booked (non-cancelled) shipment replaces the plain "mark as shipped" action and blocks rejecting. */
  private readonly openShipment = computed(() => hasOpenShipment(this.shipments.value() ?? []));
  protected readonly busy = signal<string | null>(null);
  protected readonly actionError = signal<unknown>(null);
  protected readonly reasonDialog = signal<ReasonAction | null>(null);
  protected reason = '';
  private refetchTimer: ReturnType<typeof setTimeout> | null = null;
  private paymentsTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      if (this.refetchTimer) clearTimeout(this.refetchTimer);
      if (this.paymentsTimer) clearTimeout(this.paymentsTimer);
    });
  }

  protected readonly isBuyer = computed(() => this.order.value()?.buyerOrganizationId === this.session.organizationId());
  protected readonly isSeller = computed(() => this.order.value()?.sellerOrganizationId === this.session.organizationId());
  private readonly canFulfil = computed(() => this.isSeller() && this.session.allows('B2BSales', 'orders.fulfil'));

  protected readonly canCancel = computed(() => this.isBuyer() && this.order.value()?.status === 'Placed' && this.session.can('orders.place'));
  protected readonly canConfirm = computed(() => this.canFulfil() && this.order.value()?.status === 'Placed');
  protected readonly canShip = computed(
    () => this.canFulfil() && this.order.value()?.status === 'Confirmed' && (this.shipments.value() !== null || !!this.shipments.error()) && !this.openShipment(),
  );
  protected readonly canReject = computed(
    () => this.canFulfil() && (this.order.value()?.status === 'Placed' || this.order.value()?.status === 'Confirmed') && !this.openShipment(),
  );
  protected readonly backLink = computed(() => (this.isSeller() ? '/app/bestellungen/verkauf' : '/app/bestellungen/einkauf'));

  protected confirm(): Promise<void> {
    return this.run('confirm', (id) => this.api.confirm(id), 'orders.confirmed');
  }

  protected ship(): Promise<void> {
    return this.run('ship', (id) => this.api.ship(id), 'orders.shipped');
  }

  protected onShippingChanged(change: ShippingChange): void {
    if (change.order) this.order.set(change.order);
    else if (change.reloadOrder) this.order.reload();
    this.shipments.reload();
    // Order status changes may trigger payment processing (payout on completion).
    if (change.order || change.reloadOrder) this.payments.reload();
    if (change.refetchShipmentsSoon) this.refetchShipmentsLater();
  }

  protected onDisputeChanged(change: DisputeChange): void {
    this.disputes.reload();
    if (change.reloadOrder) {
      this.order.reload();
      this.payments.reload();
    }
    if (change.refetchPaymentsSoon) {
      if (this.paymentsTimer) clearTimeout(this.paymentsTimer);
      this.paymentsTimer = setTimeout(() => {
        this.paymentsTimer = null;
        this.payments.reload();
      }, PAYMENT_REFETCH_DELAY_MS);
    }
  }

  /** One delayed re-fetch (no polling) for changes the server applies in the background a few seconds later. */
  private refetchShipmentsLater(): void {
    if (this.refetchTimer) clearTimeout(this.refetchTimer);
    this.refetchTimer = setTimeout(() => {
      this.refetchTimer = null;
      this.shipments.reload();
    }, SHIPMENT_REFETCH_DELAY_MS);
  }

  protected openReason(action: ReasonAction): void {
    this.reason = '';
    this.actionError.set(null);
    this.reasonDialog.set(action);
  }

  protected async submitReason(): Promise<void> {
    const action = this.reasonDialog();
    if (!action) return;
    if (action === 'reject' && !this.reason.trim()) return;
    const reason = this.reason.trim();
    await this.run(
      action,
      (id) => (action === 'reject' ? this.api.reject(id, reason) : this.api.cancel(id, reason || null)),
      action === 'reject' ? 'orders.rejected' : 'orders.cancelled',
    );
    if (!this.actionError()) this.reasonDialog.set(null);
  }

  private async run(name: string, call: (id: string) => Observable<Order>, successKey: string): Promise<void> {
    const current = this.order.value();
    if (!current || this.busy()) return;
    this.busy.set(name);
    this.actionError.set(null);
    try {
      this.order.set(await firstValueFrom(call(current.id)));
      this.toast.success(this.i18n.t(successKey, { number: current.number }));
    } catch (e) {
      this.actionError.set(e);
      this.order.reload();
      // e.g. 409 order.has_open_shipment: the shipments changed meanwhile.
      this.shipments.reload();
    } finally {
      // Cancelled/rejected paid orders are refunded by the server.
      this.payments.reload();
      this.busy.set(null);
    }
  }
}
