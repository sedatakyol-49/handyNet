import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiProblem, toApiProblem } from '../../core/api/api-problem';
import { MarketplaceApi, OrdersApi } from '../../core/api/handynet-api';
import { Order } from '../../core/api/models';
import { I18nService } from '../../core/i18n/i18n.service';
import { describeProblem } from '../../core/ui-state/toast.service';
import { UI } from '../../shared/ui';
import { CartItem, CartStore, SellerGroup } from './cart.store';

interface GroupState {
  busy: boolean;
  problem: ApiProblem | null;
  message: string | null;
  reference: string;
}

/** Problem codes after which the cart lines are re-read from the marketplace so the user can adjust. */
const REFRESH_CODES = ['order.price_changed', 'order.insufficient_stock', 'order.below_minimum', 'order.offer_unavailable'];

@Component({
  selector: 'hn-cart-page',
  imports: [RouterLink, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './cart.page.html',
})
export class CartPage {
  protected readonly cart = inject(CartStore);
  private readonly orders = inject(OrdersApi);
  private readonly marketplace = inject(MarketplaceApi);
  private readonly i18n = inject(I18nService);

  protected readonly state = signal<Record<string, GroupState>>({});
  protected readonly placed = signal<Order[]>([]);

  protected groupState(sellerId: string): GroupState {
    return this.state()[sellerId] ?? { busy: false, problem: null, message: null, reference: '' };
  }

  private patch(sellerId: string, patch: Partial<GroupState>): void {
    this.state.update((s) => ({ ...s, [sellerId]: { ...this.groupState(sellerId), ...patch } }));
  }

  protected setReference(sellerId: string, value: string): void {
    this.patch(sellerId, { reference: value });
  }

  protected lineIssue(item: CartItem): string | null {
    if (item.available < item.minOrderQuantity || item.available <= 0) return this.i18n.t('cart.unavailable');
    if (item.quantity > item.available) return this.i18n.t('cart.insufficient', { available: item.available });
    if (item.quantity < item.minOrderQuantity) return this.i18n.t('marketplace.qtyBelowMinimum', { min: item.minOrderQuantity });
    return null;
  }

  protected hasIssues(group: SellerGroup): boolean {
    return group.items.some((i) => this.lineIssue(i) !== null);
  }

  protected hasPriceChanges(group: SellerGroup): boolean {
    return group.items.some((i) => i.previousPriceMinor !== null && i.previousPriceMinor !== undefined);
  }

  protected updateQuantity(item: CartItem, value: number): void {
    if (Number.isFinite(value)) this.cart.setQuantity(item.offerId, value);
  }

  protected async checkout(group: SellerGroup): Promise<void> {
    const sellerId = group.sellerOrganizationId;
    if (this.groupState(sellerId).busy || this.hasIssues(group)) return;
    // Same key for every retry of this attempt, so a lost response never creates a second order.
    const key = this.cart.checkoutKey(sellerId);
    this.patch(sellerId, { busy: true, problem: null, message: null });
    try {
      const order = await firstValueFrom(
        this.orders.place(
          {
            lines: group.items.map((i) => ({ offerId: i.offerId, quantity: i.quantity, expectedUnitPriceMinor: i.priceMinor })),
            buyerReference: this.groupState(sellerId).reference.trim() || null,
          },
          key,
        ),
      );
      this.cart.completeCheckout(sellerId);
      this.placed.update((list) => [order, ...list]);
      this.patch(sellerId, { busy: false, reference: '' });
    } catch (e) {
      const problem = toApiProblem(e);
      // Network errors / 5xx: the order may or may not exist → keep the key so a retry is idempotent.
      if (problem.status >= 400 && problem.status < 500) this.cart.resetCheckoutKey(sellerId);
      let message: string | null = null;
      if (problem.code && REFRESH_CODES.includes(problem.code)) {
        await this.refreshGroup(group);
        message = this.i18n.t(`cart.conflict.${problem.code.replace('order.', '')}`);
      } else if (problem.status === 0 || problem.status >= 500) {
        message = this.i18n.t('cart.retryHint');
      }
      this.patch(sellerId, { busy: false, problem, message });
    }
  }

  protected acknowledge(group: SellerGroup): void {
    this.cart.acknowledgePrices(group.sellerOrganizationId);
  }

  protected problemText(problem: ApiProblem): string {
    return describeProblem(this.i18n, problem);
  }

  /** Reloads price, stock and minimum quantity of each line from the marketplace. */
  private async refreshGroup(group: SellerGroup): Promise<void> {
    await Promise.all(
      group.items.map((item) =>
        firstValueFrom(this.marketplace.offer(item.offerId))
          .then((listing) => this.cart.refreshFromListing(listing))
          .catch(() => this.cart.markUnavailable(item.offerId)),
      ),
    );
  }
}
