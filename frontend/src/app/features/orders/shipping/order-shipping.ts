import {
  ChangeDetectionStrategy,
  Component,
  DOCUMENT,
  DestroyRef,
  PLATFORM_ID,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Observable, firstValueFrom, of } from 'rxjs';
import { fieldErrors, toApiProblem } from '../../../core/api/api-problem';
import { OrdersApi, ShipmentsApi } from '../../../core/api/handynet-api';
import { CarrierProfile, Order, Shipment } from '../../../core/api/models';
import { SessionStore } from '../../../core/auth/session.store';
import { I18nService } from '../../../core/i18n/i18n.service';
import { ToastService } from '../../../core/ui-state/toast.service';
import { formatMoney } from '../../../core/format/money';
import { createQuery } from '../../../shared/data/query';
import { UI } from '../../../shared/ui';
import {
  ShipmentFormValue,
  eventsNewestFirst,
  formatRemaining,
  hasOpenShipment,
  inspectionRunning,
  insurable,
  insuranceRequired,
  parseCm,
  parseKgToGrams,
  profileFor,
  validateShipmentForm,
} from './shipping.logic';

/** Actions that need an explicit confirmation because they change order state or stock. */
type ConfirmAction = 'handover' | 'cancelShipment' | 'confirmDelivery' | 'accept';

const CONFIRM_TEXTS: Record<ConfirmAction, { title: string; text: string }> = {
  handover: { title: 'shipping.handover', text: 'shipping.confirmText.handover' },
  cancelShipment: { title: 'shipping.cancel', text: 'shipping.confirmText.cancelShipment' },
  confirmDelivery: { title: 'shipping.buyer.confirmDelivery', text: 'shipping.confirmText.confirmDelivery' },
  accept: { title: 'shipping.buyer.accept', text: 'shipping.confirmText.accept' },
};

export interface ShippingChange {
  /** Updated order returned by an order endpoint (buyer actions). */
  order?: Order;
  /** The order status may have changed on the server (handover, sync) – reload it. */
  reloadOrder?: boolean;
  /**
   * The server updates related data shortly afterwards in the background (receipt confirmed: a Manual shipment becomes
   * Delivered a few seconds later) – re-fetch the shipments once more after a short delay.
   */
  refetchShipmentsSoon?: boolean;
}

/**
 * "Versand" section of the order detail page: seller books / hands over / tracks a shipment,
 * buyer sees tracking, confirms receipt and accepts the goods during the inspection window.
 */
@Component({
  selector: 'hn-order-shipping',
  imports: [FormsModule, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './order-shipping.html',
})
export class OrderShippingComponent {
  private readonly shipmentsApi = inject(ShipmentsApi);
  private readonly ordersApi = inject(OrdersApi);
  private readonly session = inject(SessionStore);
  private readonly toast = inject(ToastService);
  private readonly i18n = inject(I18nService);
  private readonly document = inject(DOCUMENT);

  readonly order = input.required<Order>();
  readonly shipments = input.required<Shipment[]>();
  readonly shipmentsLoading = input(false);
  readonly shipmentsError = input<unknown>(null);
  readonly isSeller = input(false);
  readonly isBuyer = input(false);

  /** Emitted after every successful action; the parent reloads shipments and (if needed) the order. */
  readonly changed = output<ShippingChange>();

  protected readonly busy = signal<string | null>(null);
  protected readonly error = signal<unknown>(null);
  protected readonly confirmAction = signal<{ action: ConfirmAction; shipment: Shipment | null } | null>(null);
  protected readonly clientErrors = signal<Partial<Record<keyof ShipmentFormValue, string>>>({});
  protected readonly submitted = signal(false);
  protected readonly now = signal(Date.now());

  protected form: ShipmentFormValue = emptyForm(false);
  private formOrderId: string | null = null;

  protected readonly canFulfil = computed(() => this.isSeller() && this.session.allows('B2BSales', 'orders.fulfil'));
  private readonly canPlace = computed(() => this.isBuyer() && this.session.can('orders.place'));

  protected readonly showForm = computed(
    () => this.canFulfil() && this.order().status === 'Confirmed' && !this.shipmentsLoading() && !hasOpenShipment(this.shipments()),
  );

  /** Bookable carriers with their limits (liability, weight, length, lithium). */
  protected readonly profiles = createQuery(
    () => this.canFulfil() && this.order().status === 'Confirmed',
    (needed): Observable<CarrierProfile[]> => (needed ? this.shipmentsApi.carrierProfiles() : of([])),
  );
  /** Mirrors `form.carrier` so the carrier-dependent hints are reactive. */
  protected readonly selectedCarrier = signal('');
  protected readonly profile = computed(() => profileFor(this.profiles.value(), this.selectedCarrier()));
  protected readonly insuranceNeeded = computed(() => insuranceRequired(this.order().grossTotalMinor, this.profile()));
  protected readonly notInsurable = computed(() => !insurable(this.order().grossTotalMinor, this.profile()));
  /** Parameters for the carrier-limit texts (money de-DE, weight in the UI locale). */
  protected readonly limits = computed(() => {
    const p = this.profile();
    if (!p) return null;
    return {
      kg: this.weightKg(p.maxWeightGrams),
      cm: p.maxLengthCm,
      amount: formatMoney(p.standardLiabilityMinor, this.order().currency),
      max: formatMoney(p.maxInsuredValueMinor, this.order().currency),
      equipment: this.i18n.t(p.acceptsLithiumInEquipment ? 'shipping.form.allowed' : 'shipping.form.notAllowed'),
      standalone: this.i18n.t(p.acceptsStandaloneLithiumBatteries ? 'shipping.form.allowed' : 'shipping.form.notAllowed'),
    };
  });

  protected readonly confirmTitle = computed(() => CONFIRM_TEXTS[this.confirmAction()?.action ?? 'handover'].title);
  protected readonly confirmText = computed(() => CONFIRM_TEXTS[this.confirmAction()?.action ?? 'handover'].text);

  protected readonly canConfirmDelivery =computed(() => this.canPlace() && this.order().status === 'Shipped');
  protected readonly canAccept = computed(() => this.canPlace() && inspectionRunning(this.order(), this.now()));
  protected readonly remaining = computed(() =>
    formatRemaining(this.order().inspectionEndsAt, this.now(), (key, params) => this.i18n.t(key, params)),
  );

  constructor() {
    // Reset the form whenever another order is shown.
    effect(() => {
      const o = this.order();
      untracked(() => {
        if (this.formOrderId === o.id) return;
        this.formOrderId = o.id;
        this.form = emptyForm(false);
        this.selectedCarrier.set('');
        this.clientErrors.set({});
        this.submitted.set(false);
      });
    });

    // Keeps the inspection countdown current (minute resolution; no animation).
    if (isPlatformBrowser(inject(PLATFORM_ID))) {
      const handle = setInterval(() => this.now.set(Date.now()), 30_000);
      inject(DestroyRef).onDestroy(() => clearInterval(handle));
    }
  }

  protected timeline(s: Shipment) {
    return eventsNewestFirst(s.events);
  }

  protected weightKg(grams: number): string {
    return new Intl.NumberFormat(this.i18n.locale(), { maximumFractionDigits: 3 }).format(grams / 1000);
  }

  /** Client error (translated) or the API's field error for this control. */
  protected fieldError(field: keyof ShipmentFormValue, apiField: string): string | null {
    const client = this.submitted() ? this.clientErrors()[field] : undefined;
    if (client) return this.i18n.t(client, this.limits() ?? undefined);
    const err = this.error();
    const messages = err ? fieldErrors(toApiProblem(err), apiField) : [];
    return messages.length ? messages.join(' ') : null;
  }

  protected revalidate(): void {
    if (this.submitted()) this.clientErrors.set(validateShipmentForm(this.form, this.order().grossTotalMinor, this.profile()));
  }

  /** New carrier selected: pre-check "versichert" when the goods value exceeds its standard liability. */
  protected carrierChanged(carrier: string): void {
    this.selectedCarrier.set(carrier);
    if (insuranceRequired(this.order().grossTotalMinor, this.profile())) this.form.insured = true;
    this.revalidate();
  }

  protected async createShipment(): Promise<void> {
    this.submitted.set(true);
    const errors = validateShipmentForm(this.form, this.order().grossTotalMinor, this.profile());
    this.clientErrors.set(errors);
    if (Object.keys(errors).length) return;
    const f = this.form;
    await this.run('create', async () => {
      await firstValueFrom(
        this.shipmentsApi.create({
          orderId: this.order().id,
          carrier: f.carrier,
          weightGrams: parseKgToGrams(f.weightKg)!,
          lengthCm: parseCm(f.lengthCm)!,
          widthCm: parseCm(f.widthCm)!,
          heightCm: parseCm(f.heightCm)!,
          insured: f.insured,
          trackingNumber: f.carrier === 'Manual' ? f.trackingNumber.trim() : null,
        }),
      );
      this.toast.success(this.i18n.t('shipping.created'));
      this.changed.emit({});
    });
  }

  protected async downloadLabel(s: Shipment): Promise<void> {
    await this.run('label-' + s.id, async () => {
      const blob = await firstValueFrom(this.shipmentsApi.label(s.id));
      const url = URL.createObjectURL(blob);
      const a = this.document.createElement('a');
      a.href = url;
      a.download = `versandetikett-${s.trackingNumber}.pdf`;
      this.document.body.appendChild(a);
      a.click();
      a.remove();
      setTimeout(() => URL.revokeObjectURL(url), 60_000);
    });
  }

  protected async sync(s: Shipment): Promise<void> {
    await this.run('sync-' + s.id, async () => {
      const updated = await firstValueFrom(this.shipmentsApi.sync(s.id));
      this.toast.success(this.i18n.t('shipping.synced'));
      this.changed.emit({ reloadOrder: updated.status !== s.status });
    });
  }

  protected ask(action: ConfirmAction, shipment: Shipment | null = null): void {
    this.error.set(null);
    this.confirmAction.set({ action, shipment });
  }

  protected async confirmed(): Promise<void> {
    const pending = this.confirmAction();
    if (!pending) return;
    const { action, shipment } = pending;
    await this.run(action, async () => {
      switch (action) {
        case 'handover':
          await firstValueFrom(this.shipmentsApi.handOver(shipment!.id));
          this.toast.success(this.i18n.t('shipping.handedOver'));
          this.changed.emit({ reloadOrder: true });
          break;
        case 'cancelShipment':
          await firstValueFrom(this.shipmentsApi.cancel(shipment!.id));
          this.toast.success(this.i18n.t('shipping.cancelled'));
          this.changed.emit({});
          break;
        case 'confirmDelivery': {
          const order = await firstValueFrom(this.ordersApi.confirmDelivery(this.order().id));
          this.toast.success(this.i18n.t('shipping.deliveryConfirmed', { number: order.number }));
          this.changed.emit({ order, refetchShipmentsSoon: true });
          break;
        }
        case 'accept': {
          const order = await firstValueFrom(this.ordersApi.accept(this.order().id));
          this.toast.success(this.i18n.t('shipping.accepted', { number: order.number }));
          this.changed.emit({ order });
          break;
        }
      }
      this.confirmAction.set(null);
    });
  }

  private async run(name: string, work: () => Promise<void>): Promise<void> {
    if (this.busy()) return;
    this.busy.set(name);
    this.error.set(null);
    try {
      await work();
    } catch (e) {
      this.error.set(e);
      // State may have moved on (e.g. order no longer Confirmed, auto-completed): refresh everything.
      this.changed.emit({ reloadOrder: true });
    } finally {
      this.busy.set(null);
    }
  }
}

function emptyForm(insured: boolean): ShipmentFormValue {
  return { carrier: '', weightKg: '', lengthCm: '', widthCm: '', heightCm: '', insured, trackingNumber: '' };
}
