import { ChangeDetectionStrategy, Component, DOCUMENT, computed, inject, input, output, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Observable, firstValueFrom, of } from 'rxjs';
import { toApiProblem } from '../../../core/api/api-problem';
import { PaymentsApi } from '../../../core/api/handynet-api';
import { Order, Payment, SellerBalance } from '../../../core/api/models';
import { SessionStore } from '../../../core/auth/session.store';
import { createQuery } from '../../../shared/data/query';
import { UI } from '../../../shared/ui';
import { buyerPaymentAction, payoutForOrder, relevantPayment, sellerPayoutState } from './payment.logic';

/**
 * "Zahlung" section of the order detail page. Online payment is optional – orders without it remain valid
 * (invoice / bank transfer outside the platform), so nothing here blocks the order flow.
 * - Buyer: start or resume the provider checkout (same tab, the provider redirects back into the app).
 * - Seller: gross amount, commission (net + VAT), seller share and whether it has been paid out.
 */
@Component({
  selector: 'hn-order-payment',
  imports: [RouterLink, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './order-payment.html',
})
export class OrderPaymentComponent {
  private readonly api = inject(PaymentsApi);
  private readonly session = inject(SessionStore);
  private readonly document = inject(DOCUMENT);

  readonly order = input.required<Order>();
  readonly payments = input.required<Payment[]>();
  readonly paymentsLoading = input(false);
  readonly paymentsError = input<unknown>(null);
  readonly isSeller = input(false);
  readonly isBuyer = input(false);

  /** The payment state changed on the server (e.g. already paid) – the parent reloads the payments. */
  readonly changed = output<void>();

  protected readonly busy = signal(false);
  protected readonly error = signal<unknown>(null);

  private readonly canPay = computed(() => this.isBuyer() && this.session.allows('B2BPurchasing', 'orders.place'));
  protected readonly canSeeBalance = computed(() => this.isSeller() && this.session.allows('B2BSales', 'orders.fulfil'));

  protected readonly action = computed(() => (this.canPay() ? buyerPaymentAction(this.order().status, this.payments()) : null));
  protected readonly current = computed(() => relevantPayment(this.payments()));
  /** Earlier attempts (failed, cancelled, expired) besides the relevant payment. */
  protected readonly history = computed(() => this.payments().filter((p) => p !== this.current()));

  /** Seller balance is re-read whenever the order status changes (payout happens on completion). */
  protected readonly balance = createQuery(
    () => (this.canSeeBalance() && this.current()?.status === 'Paid' ? `${this.order().id}:${this.order().status}` : null),
    (key): Observable<SellerBalance | null> => (key ? this.api.balance() : of(null)),
  );
  protected readonly payout = computed(() => payoutForOrder(this.balance.value()?.recentPayouts, this.order().id));
  protected readonly payoutState = computed(() => sellerPayoutState(this.order().status, this.current(), this.payout()));

  /** Starts (or resumes) the payment and opens the provider's checkout page in the same tab. */
  protected async pay(): Promise<void> {
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      const payment = await firstValueFrom(this.api.start(this.order().id));
      if (payment.status === 'Open' && payment.checkoutUrl) {
        this.document.defaultView?.location.assign(payment.checkoutUrl);
        return; // keep the button busy while the browser navigates away
      }
      this.changed.emit();
    } catch (e) {
      this.error.set(e);
      const code = toApiProblem(e).code;
      if (code === 'payment.already_paid' || code === 'payment.order_not_payable') this.changed.emit();
    }
    this.busy.set(false);
  }

  /** Commission shown as a deduction from the gross amount (integer negation; 0 stays 0, never "-0,00 €"). */
  protected deduction(minor: number | null): number {
    return minor ? -minor : 0;
  }

  protected reloadBalance(): void {
    this.balance.reload();
  }
}
