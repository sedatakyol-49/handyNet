import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { TranslateEnumPipe } from '../../core/i18n/translate.pipe';

type Tone = 'neutral' | 'info' | 'success' | 'warning' | 'danger';

const TONES: Record<Tone, string> = {
  neutral: 'bg-slate-100 text-slate-700 ring-slate-300',
  info: 'bg-blue-50 text-blue-800 ring-blue-200',
  success: 'bg-emerald-50 text-emerald-800 ring-emerald-200',
  warning: 'bg-amber-50 text-amber-900 ring-amber-200',
  danger: 'bg-red-50 text-red-800 ring-red-200',
};

const STATUS_TONES: Record<string, Tone> = {
  // organizations
  Verified: 'success',
  PendingVerification: 'warning',
  Rejected: 'danger',
  Suspended: 'danger',
  // orders
  Placed: 'info',
  Confirmed: 'warning',
  Shipped: 'info',
  Delivered: 'warning', // order: buyer inspection running; shipment: see SHIPMENT_TONES
  Disputed: 'danger', // buyer complained: completion and payout on hold
  Completed: 'success',
  Refunded: 'info',
  Cancelled: 'neutral',
  // offers
  Published: 'success',
  Paused: 'warning',
  Draft: 'neutral',
  Archived: 'neutral',
  // imports
  Previewed: 'info',
  Committed: 'success',
  Discarded: 'neutral',
  Valid: 'success',
  Error: 'danger',
  Conflict: 'warning',
};

/** Shipment statuses share names with order statuses (Delivered, Cancelled) but need their own tones. */
const SHIPMENT_TONES: Record<string, Tone> = {
  LabelCreated: 'warning', // booked, but not yet handed to the carrier
  HandedOver: 'info',
  InTransit: 'info',
  Delivered: 'success',
  Exception: 'danger',
  Cancelled: 'neutral',
};

/** Payment statuses of the payment service provider (note: "Canceled", not "Cancelled"). */
const PAYMENT_TONES: Record<string, Tone> = {
  Open: 'warning',
  Paid: 'success',
  Failed: 'danger',
  Canceled: 'neutral',
  Expired: 'neutral',
  Refunded: 'info',
};

/** Dispute statuses (Refunded shares its name with order/payment statuses). */
const DISPUTE_TONES: Record<string, Tone> = {
  Open: 'warning',
  Escalated: 'danger',
  Refunded: 'info',
  ClosedWithoutRefund: 'neutral',
};

const KIND_TONES: Record<string, Record<string, Tone>> = {
  shipmentStatus: SHIPMENT_TONES,
  paymentStatus: PAYMENT_TONES,
  disputeStatus: DISPUTE_TONES,
};

/** `<hn-status-badge [status]="order.status" kind="orderStatus" />` — kind selects the translation prefix. */
@Component({
  selector: 'hn-status-badge',
  imports: [TranslateEnumPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span
    class="inline-flex items-center rounded px-1.5 py-0.5 text-xs font-medium whitespace-nowrap ring-1 ring-inset"
    [class]="toneClass()"
    >{{ status() | tEnum: kind() }}</span
  >`,
})
export class StatusBadgeComponent {
  readonly status = input.required<string>();
  readonly kind = input<string>('status');
  readonly tone = input<Tone | null>(null);
  protected readonly toneClass = computed(() => {
    const byKind = KIND_TONES[this.kind()]?.[this.status()];
    return TONES[this.tone() ?? byKind ?? STATUS_TONES[this.status()] ?? 'neutral'];
  });
}
