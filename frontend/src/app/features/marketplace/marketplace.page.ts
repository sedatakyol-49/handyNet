import { ChangeDetectionStrategy, Component, ElementRef, inject, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { CatalogApi, MarketplaceApi } from '../../core/api/handynet-api';
import { Listing, MarketplaceSort } from '../../core/api/models';
import { SessionStore } from '../../core/auth/session.store';
import { I18nService } from '../../core/i18n/i18n.service';
import { ToastService } from '../../core/ui-state/toast.service';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';
import { CartStore, clampQuantity } from '../cart/cart.store';

@Component({
  selector: 'hn-marketplace-page',
  imports: [FormsModule, RouterLink, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './marketplace.page.html',
  host: { '(document:keydown)': 'onKeydown($event)' },
})
export class MarketplacePage {
  private readonly api = inject(MarketplaceApi);
  private readonly catalog = inject(CatalogApi);
  private readonly toast = inject(ToastService);
  private readonly i18n = inject(I18nService);
  protected readonly cart = inject(CartStore);
  protected readonly session = inject(SessionStore);

  private readonly searchBox = viewChild.required<ElementRef<HTMLInputElement>>('searchBox');
  private debounce: ReturnType<typeof setTimeout> | undefined;

  protected readonly searchText = signal('');
  protected readonly search = signal('');
  protected readonly category = signal('');
  protected readonly brand = signal('');
  protected readonly inStockOnly = signal(true);
  protected readonly sort = signal<MarketplaceSort>('price');
  protected readonly page = signal(1);
  protected readonly pageSize = 50;
  /** Quantity inputs per offer (defaults to the minimum order quantity). */
  protected readonly quantities = signal<Record<string, number>>({});

  protected readonly categories = createQuery(() => true, () => this.catalog.categories());

  protected readonly results = createQuery(
    () => ({
      search: this.search(),
      category: this.category(),
      brand: this.brand(),
      inStockOnly: this.inStockOnly(),
      sort: this.sort(),
      page: this.page(),
      pageSize: this.pageSize,
    }),
    (q) => this.api.offers(q),
  );

  protected onSearchInput(value: string): void {
    this.searchText.set(value);
    clearTimeout(this.debounce);
    this.debounce = setTimeout(() => this.applySearch(), 300);
  }

  protected applySearch(): void {
    clearTimeout(this.debounce);
    this.search.set(this.searchText().trim());
    this.page.set(1);
  }

  protected setFilter(apply: () => void): void {
    apply();
    this.page.set(1);
  }

  protected resetFilters(): void {
    this.searchText.set('');
    this.search.set('');
    this.category.set('');
    this.brand.set('');
    this.inStockOnly.set(true);
    this.sort.set('price');
    this.page.set(1);
  }

  /** "/" focuses the search box (unless the user is typing in a field). */
  protected onKeydown(event: KeyboardEvent): void {
    if (event.key !== '/' || event.ctrlKey || event.metaKey || event.altKey) return;
    const target = event.target as HTMLElement | null;
    if (target && (target.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName))) return;
    event.preventDefault();
    this.searchBox().nativeElement.focus();
    this.searchBox().nativeElement.select();
  }

  protected quantityFor(l: Listing): number {
    return this.quantities()[l.offerId] ?? l.minOrderQuantity;
  }

  protected setQuantity(l: Listing, value: number): void {
    this.quantities.update((q) => ({ ...q, [l.offerId]: value }));
  }

  protected isOwn(l: Listing): boolean {
    return l.sellerOrganizationId === this.session.organizationId();
  }

  /** Remaining quantity that can still be added, considering what is already in the cart. */
  protected remaining(l: Listing): number {
    return l.available - this.cart.quantityOf(l.offerId);
  }

  protected quantityError(l: Listing): string | null {
    const q = this.quantityFor(l);
    if (!Number.isInteger(q) || q < 1) return this.i18n.t('marketplace.qtyInvalid');
    const inCart = this.cart.quantityOf(l.offerId);
    if (inCart + q < l.minOrderQuantity) return this.i18n.t('marketplace.qtyBelowMinimum', { min: l.minOrderQuantity });
    if (inCart + q > l.available) return this.i18n.t('marketplace.qtyAboveAvailable', { available: l.available });
    return null;
  }

  protected canOrder(l: Listing): boolean {
    return !this.isOwn(l) && l.available >= l.minOrderQuantity && this.session.can('orders.place');
  }

  protected addToCart(l: Listing): void {
    if (this.quantityError(l) || !this.canOrder(l)) return;
    const q = this.quantityFor(l);
    if (clampQuantity(this.cart.quantityOf(l.offerId) + q, l.minOrderQuantity, l.available) === null) return;
    this.cart.add(l, q);
    this.toast.success(this.i18n.t('marketplace.added', { qty: q, name: `${l.brand} ${l.productName}` }));
  }
}
