import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { of } from 'rxjs';
import { MarketplaceApi } from '../../core/api/handynet-api';
import { Listing } from '../../core/api/models';
import { SessionStore } from '../../core/auth/session.store';
import { grossFromNetMinor } from '../../core/format/money';
import { I18nService } from '../../core/i18n/i18n.service';
import { ToastService } from '../../core/ui-state/toast.service';
import { createQuery } from '../../shared/data/query';
import { ImageGalleryComponent } from '../../shared/product-images/image-gallery';
import { UI } from '../../shared/ui';
import { CartStore } from '../cart/cart.store';
import { quantityProblem } from './order-quantity';

/**
 * Marketplace detail modal (`?offer={id}`): all product data, gallery, the offer's terms with net and gross price,
 * other sellers' offers for the same product, quantity + add to cart for each. Read-only for products by design.
 */
@Component({
  selector: 'hn-offer-detail-dialog',
  imports: [NgTemplateOutlet, ...UI, ImageGalleryComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-dialog [open]="!!offerId()" [title]="title()" size="xl" testId="offer-detail" (closed)="closed.emit()">
      @if (detail.error(); as err) {
        <hn-banner [error]="err" />
      }
      @if (detail.loading() && !current()) {
        <p class="py-8 text-center text-sm text-slate-500">{{ 'common.loading' | t }}</p>
      }
      @if (current(); as d) {
        <div class="grid gap-5 md:grid-cols-[minmax(0,2fr)_minmax(0,3fr)]">
          <hn-image-gallery [images]="d.product.images" [productName]="d.product.brand + ' ' + d.product.name" />

          <div class="space-y-4 text-sm">
            <section [attr.aria-labelledby]="'od-product-' + d.offer.offerId">
              <h3 class="mb-1 text-xs font-semibold tracking-wide text-slate-500 uppercase" [id]="'od-product-' + d.offer.offerId">
                {{ 'marketplace.detail.product' | t }}
              </h3>
              <dl class="grid grid-cols-[max-content_1fr] gap-x-4 gap-y-1" data-testid="detail-product">
                <dt class="text-slate-500">{{ 'marketplace.brand' | t }}</dt>
                <dd>{{ d.product.brand }}</dd>
                <dt class="text-slate-500">{{ 'common.name' | t }}</dt>
                <dd class="font-medium">{{ d.product.name }}</dd>
                <dt class="text-slate-500">{{ 'marketplace.category' | t }}</dt>
                <dd>{{ d.product.category | tEnum: 'category' }}</dd>
                <dt class="text-slate-500">{{ 'common.quality' | t }}</dt>
                <dd>{{ d.product.quality | tEnum: 'quality' }}</dd>
                <dt class="text-slate-500">EAN</dt>
                <dd class="hn-mono">{{ d.product.ean ?? '–' }}</dd>
                <dt class="text-slate-500">MPN</dt>
                <dd class="hn-mono">{{ d.product.mpn ?? '–' }}</dd>
                <dt class="text-slate-500">{{ 'marketplace.detail.model' | t }}</dt>
                <dd>{{ d.product.modelName ?? '–' }}</dd>
                <dt class="text-slate-500">{{ 'marketplace.detail.compatible' | t }}</dt>
                <dd>
                  @if (d.product.compatibleModels.length) {
                    <ul class="flex flex-wrap gap-1">
                      @for (m of d.product.compatibleModels; track m) {
                        <li class="rounded border border-slate-200 bg-slate-50 px-1.5 py-0.5 text-xs">{{ m }}</li>
                      }
                    </ul>
                  } @else {
                    –
                  }
                </dd>
              </dl>
            </section>

            <section [attr.aria-labelledby]="'od-offer-' + d.offer.offerId" class="rounded-md border border-slate-200 p-3">
              <h3 class="mb-1 text-xs font-semibold tracking-wide text-slate-500 uppercase" [id]="'od-offer-' + d.offer.offerId">
                {{ 'marketplace.detail.offer' | t }}
              </h3>
              <dl class="grid grid-cols-[max-content_1fr] gap-x-4 gap-y-1" data-testid="detail-offer">
                <dt class="text-slate-500">{{ 'marketplace.seller' | t }}</dt>
                <dd>{{ d.offer.sellerName }} <span class="text-slate-500">· {{ d.offer.sellerCity }}</span></dd>
                <dt class="text-slate-500">{{ 'common.sku' | t }}</dt>
                <dd class="hn-mono">{{ d.offer.supplierSku }}</dd>
                <dt class="text-slate-500">{{ 'common.condition' | t }}</dt>
                <dd>{{ d.offer.condition | tEnum: 'condition' }}</dd>
                <dt class="text-slate-500">{{ 'marketplace.detail.netPrice' | t }}</dt>
                <dd class="text-base font-semibold" data-testid="detail-net">{{ d.offer.priceMinor | money: d.offer.currency }}</dd>
                <dt class="text-slate-500">{{ 'marketplace.detail.grossPrice' | t }}</dt>
                <dd data-testid="detail-gross">
                  {{ gross(d.offer) | money: d.offer.currency }}
                  <span class="text-xs text-slate-500">{{ 'marketplace.detail.grossHint' | t }}</span>
                </dd>
                <dt class="text-slate-500">{{ 'marketplace.available' | t }}</dt>
                <dd [class.text-red-700]="d.offer.available < d.offer.minOrderQuantity">{{ d.offer.available }}</dd>
                <dt class="text-slate-500">{{ 'marketplace.delivery' | t }}</dt>
                <dd>{{ 'marketplace.detail.deliveryDays' | t: { days: d.offer.deliveryDays } }}</dd>
                <dt class="text-slate-500">{{ 'marketplace.detail.moq' | t }}</dt>
                <dd>{{ d.offer.minOrderQuantity }}</dd>
                <dt class="text-slate-500">{{ 'common.updatedAt' | t }}</dt>
                <dd>{{ d.offer.updatedAt | hnDate }}</dd>
              </dl>
              <div class="mt-3">
                <ng-container *ngTemplateOutlet="addRow; context: { $implicit: d.offer, main: true }" />
              </div>
            </section>
          </div>
        </div>

        <section class="mt-5" [attr.aria-labelledby]="'od-others-' + d.offer.offerId">
          <h3 class="mb-2 text-sm font-semibold" [id]="'od-others-' + d.offer.offerId">
            {{ 'marketplace.detail.otherOffers' | t: { count: d.otherOffers.length } }}
          </h3>
          @if (d.otherOffers.length === 0) {
            <p class="text-sm text-slate-500">{{ 'marketplace.detail.noOtherOffers' | t }}</p>
          } @else {
            <div class="overflow-x-auto rounded-md border border-slate-200">
              <table class="hn-table" data-testid="other-offers">
                <thead>
                  <tr>
                    <th scope="col">{{ 'marketplace.seller' | t }}</th>
                    <th scope="col">{{ 'common.sku' | t }}</th>
                    <th scope="col">{{ 'common.condition' | t }}</th>
                    <th scope="col" class="num">{{ 'marketplace.netPrice' | t }}</th>
                    <th scope="col" class="num">{{ 'common.gross' | t }}</th>
                    <th scope="col" class="num">{{ 'marketplace.available' | t }}</th>
                    <th scope="col" class="num">{{ 'marketplace.delivery' | t }}</th>
                    <th scope="col" class="num">{{ 'marketplace.moq' | t }}</th>
                    <th scope="col"><span class="sr-only">{{ 'marketplace.addToCart' | t }}</span></th>
                  </tr>
                </thead>
                <tbody>
                  @for (o of d.otherOffers; track o.offerId) {
                    <tr>
                      <td class="whitespace-nowrap">{{ o.sellerName }}<span class="block text-xs text-slate-500">{{ o.sellerCity }}</span></td>
                      <td class="hn-mono">{{ o.supplierSku }}</td>
                      <td class="whitespace-nowrap">{{ o.condition | tEnum: 'condition' }}</td>
                      <td class="num font-semibold">{{ o.priceMinor | money: o.currency }}</td>
                      <td class="num text-slate-600">{{ gross(o) | money: o.currency }}</td>
                      <td class="num" [class.text-red-700]="o.available < o.minOrderQuantity">{{ o.available }}</td>
                      <td class="num">{{ 'marketplace.days' | t: { days: o.deliveryDays } }}</td>
                      <td class="num">{{ o.minOrderQuantity }}</td>
                      <td class="whitespace-nowrap">
                        <ng-container *ngTemplateOutlet="addRow; context: { $implicit: o, main: false }" />
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        </section>
      }

      <ng-template #addRow let-l let-main="main">
        @if (isOwn(l)) {
          <span class="text-xs text-slate-500">{{ 'marketplace.ownOffer' | t }}</span>
        } @else if (canOrder(l)) {
          <div class="flex items-start gap-2">
            <div>
              <label [class]="main ? 'mb-1 block text-xs font-medium text-slate-700' : 'sr-only'" [for]="'od-qty-' + l.offerId">{{ 'common.quantity' | t }}</label>
              <input
                hnInput
                class="w-24 py-1 text-end"
                type="number"
                [id]="'od-qty-' + l.offerId"
                [min]="l.minOrderQuantity"
                [max]="remaining(l)"
                step="1"
                [value]="quantityFor(l)"
                (input)="setQuantity(l, $any($event.target).valueAsNumber)"
                (keydown.enter)="$event.preventDefault(); addToCart(l)"
                [attr.aria-invalid]="!!quantityError(l)"
                [attr.aria-describedby]="quantityError(l) ? 'od-qty-err-' + l.offerId : null"
                [attr.data-testid]="main ? 'detail-qty' : null"
              />
              @if (quantityError(l); as err) {
                <p class="mt-0.5 max-w-48 text-[11px] whitespace-normal text-red-700" [id]="'od-qty-err-' + l.offerId">{{ err }}</p>
              } @else if (cart.quantityOf(l.offerId) > 0) {
                <p class="mt-0.5 text-[11px] text-slate-500">{{ 'marketplace.inCart' | t: { qty: cart.quantityOf(l.offerId) } }}</p>
              }
            </div>
            <button
              hnButton
              [size]="main ? 'md' : 'sm'"
              variant="primary"
              type="button"
              [class.mt-5]="main"
              [class.mt-0.5]="!main"
              [disabled]="!!quantityError(l)"
              (click)="addToCart(l)"
              [attr.aria-label]="('marketplace.addToCart' | t) + ': ' + l.sellerName + ', ' + l.supplierSku"
              [attr.data-testid]="main ? 'detail-add' : 'other-add'"
            >
              {{ 'marketplace.add' | t }}
            </button>
          </div>
        } @else {
          <span class="text-xs text-slate-500">{{ 'marketplace.notOrderable' | t }}</span>
        }
      </ng-template>
    </hn-dialog>
  `,
})
export class OfferDetailDialogComponent {
  private readonly api = inject(MarketplaceApi);
  private readonly toast = inject(ToastService);
  private readonly i18n = inject(I18nService);
  private readonly session = inject(SessionStore);
  protected readonly cart = inject(CartStore);

  readonly offerId = input<string | null>(null);
  readonly closed = output<void>();

  protected readonly detail = createQuery(
    () => this.offerId(),
    (id) => (id ? this.api.detail(id) : of(null)),
  );
  /** Only the detail of the requested offer (the query keeps the previous value while loading). */
  protected readonly current = computed(() => {
    const d = this.detail.value();
    return d && d.offer.offerId === this.offerId() ? d : null;
  });
  protected readonly title = computed(() => {
    const d = this.current();
    return d ? `${d.product.brand} ${d.product.name}` : this.i18n.t('marketplace.detail.title');
  });

  private readonly quantities = signal<Record<string, number>>({});

  protected gross(l: Listing): number {
    return grossFromNetMinor(l.priceMinor);
  }

  protected isOwn(l: Listing): boolean {
    return l.sellerOrganizationId === this.session.organizationId();
  }

  protected canOrder(l: Listing): boolean {
    return !this.isOwn(l) && l.available >= l.minOrderQuantity && this.session.can('orders.place');
  }

  protected quantityFor(l: Listing): number {
    return this.quantities()[l.offerId] ?? l.minOrderQuantity;
  }

  protected setQuantity(l: Listing, value: number): void {
    this.quantities.update((q) => ({ ...q, [l.offerId]: value }));
  }

  protected remaining(l: Listing): number {
    return l.available - this.cart.quantityOf(l.offerId);
  }

  protected quantityError(l: Listing): string | null {
    const p = quantityProblem(l, this.quantityFor(l), this.cart.quantityOf(l.offerId));
    return p ? this.i18n.t(p.key, p.params) : null;
  }

  protected addToCart(l: Listing): void {
    if (!this.canOrder(l) || this.quantityError(l)) return;
    const q = this.quantityFor(l);
    this.cart.add(l, q);
    this.toast.success(this.i18n.t('marketplace.added', { qty: q, name: `${l.brand} ${l.productName}` }));
  }
}
