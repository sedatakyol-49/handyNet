import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, computed, effect, inject, signal, untracked, viewChild } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink, convertToParamMap } from '@angular/router';
import { CatalogApi, MarketplaceApi, MarketplaceQuery } from '../../core/api/handynet-api';
import { Listing, MarketplaceSort } from '../../core/api/models';
import { SessionStore } from '../../core/auth/session.store';
import { formatMoney, minorToInput, parseMoneyToMinor } from '../../core/format/money';
import { I18nService } from '../../core/i18n/i18n.service';
import { ToastService } from '../../core/ui-state/toast.service';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';
import { MultiSelectComponent, MultiSelectOption } from '../../shared/ui/multi-select';
import { CartStore } from '../cart/cart.store';
import {
  FilterChip,
  MarketFilters,
  activeChips,
  filtersFromParams,
  filtersToParams,
  priceRangeInvalid,
  removeChip,
  resetFilters,
  toApiQuery,
  withFilters,
} from './marketplace-filters';
import { OfferDetailDialogComponent } from './offer-detail.dialog';
import { quantityProblem } from './order-quantity';

const TEXT_DEBOUNCE_MS = 350;

/**
 * Marktplatz: read-only for products (no "add product" here by design; products enter via Lager or import).
 * Filters live in the URL (see marketplace-filters.ts); a row click / Enter opens the detail modal (`?offer=`).
 */
@Component({
  selector: 'hn-marketplace-page',
  imports: [RouterLink, ...UI, MultiSelectComponent, OfferDetailDialogComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './marketplace.page.html',
  host: { '(document:keydown)': 'onKeydown($event)' },
})
export class MarketplacePage {
  private readonly api = inject(MarketplaceApi);
  protected readonly catalog = inject(CatalogApi);
  private readonly toast = inject(ToastService);
  private readonly i18n = inject(I18nService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  protected readonly cart = inject(CartStore);
  protected readonly session = inject(SessionStore);

  private readonly searchBox = viewChild.required<ElementRef<HTMLInputElement>>('searchBox');
  private readonly timers = new Map<string, ReturnType<typeof setTimeout>>();

  protected readonly pageSize = 50;
  private readonly paramMap = toSignal(this.route.queryParamMap, { initialValue: convertToParamMap({}) });
  protected readonly filters = computed(() => filtersFromParams(this.paramMap()));
  protected readonly offerId = computed(() => this.paramMap().get('offer'));

  // Text inputs keep what the user types; they are applied (debounced) to the URL.
  protected readonly searchText = signal('');
  protected readonly modelText = signal('');
  protected readonly priceMinText = signal('');
  protected readonly priceMaxText = signal('');
  protected readonly moreOpen = signal(false);
  /** Quantity inputs per offer (defaults to the minimum order quantity). */
  protected readonly quantities = signal<Record<string, number>>({});

  protected readonly results = createQuery(
    () => toApiQuery(this.filters(), this.pageSize),
    (q) => this.api.offers(q),
  );
  /** Facets ignore page and sort; the key is a string so paging does not re-count. */
  protected readonly facets = createQuery(
    () => {
      const { page: _p, sort: _s, pageSize: _ps, ...rest } = toApiQuery(this.filters(), this.pageSize);
      return JSON.stringify(rest);
    },
    (key) => this.api.facets(JSON.parse(key) as MarketplaceQuery),
  );

  protected readonly chips = computed(() => activeChips(this.filters()));
  protected readonly priceInvalid = computed(() => priceRangeInvalid(this.filters()));
  protected readonly priceMinInvalid = computed(() => this.invalidMoney(this.priceMinText()));
  protected readonly priceMaxInvalid = computed(() => this.invalidMoney(this.priceMaxText()));

  protected readonly categoryOptions = computed<MultiSelectOption[]>(() =>
    (this.facets.value()?.categories ?? []).map((f) => ({ value: f.value, label: this.i18n.enum('category', f.value), count: f.count })),
  );
  protected readonly brandOptions = computed<MultiSelectOption[]>(() =>
    (this.facets.value()?.brands ?? []).map((f) => ({ value: f.value, label: f.value, count: f.count })),
  );
  protected readonly qualityOptions = computed<MultiSelectOption[]>(() =>
    (this.facets.value()?.qualities ?? []).map((f) => ({ value: f.value, label: this.i18n.enum('quality', f.value), count: f.count })),
  );
  protected readonly conditionOptions = computed<MultiSelectOption[]>(() =>
    (this.facets.value()?.conditions ?? []).map((f) => ({ value: f.value, label: this.i18n.enum('condition', f.value), count: f.count })),
  );
  protected readonly sellerOptions = computed<MultiSelectOption[]>(() =>
    (this.facets.value()?.sellers ?? []).map((s) => ({ value: s.id, label: s.name, hint: s.city, count: s.count })),
  );
  /** Known seller names (facets lose a seller that no longer matches; keep the last name seen for its chip). */
  private readonly sellerNames = new Map<string, string>();

  protected readonly deliveryChoices = computed(() => {
    const base = [1, 2, 3, 5, 7, 14];
    const current = this.filters().maxDeliveryDays;
    return current !== null && !base.includes(current) ? [...base, current].sort((a, b) => a - b) : base;
  });

  protected readonly pricePlaceholder = computed(() => {
    const p = this.facets.value()?.price;
    return p ? { min: minorToInput(p.minMinor), max: minorToInput(p.maxMinor) } : { min: '', max: '' };
  });

  constructor() {
    // URL → text inputs (back/forward navigation, chips, reset), unless the user is typing into that field.
    effect(() => {
      const f = this.filters();
      untracked(() => {
        if (!this.timers.has('search')) this.searchText.set(f.search);
        if (!this.timers.has('model')) this.modelText.set(f.compatibleModel);
        if (!this.timers.has('price')) {
          this.priceMinText.set(f.priceMinMinor === null ? '' : minorToInput(f.priceMinMinor));
          this.priceMaxText.set(f.priceMaxMinor === null ? '' : minorToInput(f.priceMaxMinor));
        }
      });
    });
    effect(() => {
      for (const s of this.facets.value()?.sellers ?? []) this.sellerNames.set(s.id, s.city ? `${s.name} (${s.city})` : s.name);
    });
    inject(DestroyRef).onDestroy(() => this.timers.forEach((t) => clearTimeout(t)));
  }

  // ---------------------------------------------------------------- filter changes

  protected apply(patch: Partial<MarketFilters>, replaceUrl = false): void {
    this.navigate(withFilters(this.filters(), patch), replaceUrl);
  }

  private navigate(f: MarketFilters, replaceUrl: boolean): void {
    void this.router.navigate([], { relativeTo: this.route, queryParams: filtersToParams(f), queryParamsHandling: 'merge', replaceUrl });
  }

  private debounce(key: string, run: () => void): void {
    clearTimeout(this.timers.get(key));
    this.timers.set(
      key,
      setTimeout(() => {
        this.timers.delete(key);
        run();
      }, TEXT_DEBOUNCE_MS),
    );
  }

  private flush(key: string): void {
    clearTimeout(this.timers.get(key));
    this.timers.delete(key);
  }

  protected onSearchInput(value: string): void {
    this.searchText.set(value);
    this.debounce('search', () => this.applySearch());
  }

  protected applySearch(): void {
    this.flush('search');
    if (this.searchText().trim() !== this.filters().search) this.apply({ search: this.searchText().trim() }, true);
  }

  protected onModelInput(value: string): void {
    this.modelText.set(value);
    this.debounce('model', () => this.applyModel());
  }

  protected applyModel(): void {
    this.flush('model');
    if (this.modelText().trim() !== this.filters().compatibleModel) this.apply({ compatibleModel: this.modelText().trim() }, true);
  }

  protected onPriceInput(which: 'min' | 'max', value: string): void {
    (which === 'min' ? this.priceMinText : this.priceMaxText).set(value);
    this.debounce('price', () => this.applyPrice());
  }

  protected applyPrice(): void {
    this.flush('price');
    if (this.priceMinInvalid() || this.priceMaxInvalid()) return; // keep the text, show the error, do not filter
    const min = this.priceMinText().trim() ? parseMoneyToMinor(this.priceMinText()) : null;
    const max = this.priceMaxText().trim() ? parseMoneyToMinor(this.priceMaxText()) : null;
    const f = this.filters();
    if (min !== f.priceMinMinor || max !== f.priceMaxMinor) this.apply({ priceMinMinor: min, priceMaxMinor: max }, true);
  }

  private invalidMoney(text: string): boolean {
    if (!text.trim()) return false;
    const minor = parseMoneyToMinor(text);
    return minor === null || minor < 0;
  }

  protected setSort(value: string): void {
    this.apply({ sort: value as MarketplaceSort });
  }

  protected setDelivery(value: string): void {
    this.apply({ maxDeliveryDays: value === '' ? null : Number(value) });
  }

  protected setPage(page: number): void {
    this.apply({ page });
  }

  protected removeChip(chip: FilterChip): void {
    this.flush('search');
    this.flush('model');
    this.flush('price');
    this.navigate(removeChip(this.filters(), chip), false);
  }

  protected resetAll(): void {
    this.flush('search');
    this.flush('model');
    this.flush('price');
    this.navigate(resetFilters(), false);
  }

  protected chipLabel(chip: FilterChip): string {
    const t = (key: string, params?: Record<string, string | number>) => this.i18n.t(key, params);
    switch (chip.kind) {
      case 'search':
        return t('marketplace.chips.search', { value: chip.value });
      case 'category':
        return this.i18n.enum('category', chip.value);
      case 'brand':
        return chip.value;
      case 'quality':
        return `${t('common.quality')}: ${this.i18n.enum('quality', chip.value)}`;
      case 'condition':
        return `${t('common.condition')}: ${this.i18n.enum('condition', chip.value)}`;
      case 'seller':
        return `${t('marketplace.seller')}: ${this.sellerNames.get(chip.value) ?? chip.value.slice(0, 8)}`;
      case 'priceMin':
        return t('marketplace.chips.priceMin', { value: formatMoney(Number(chip.value)) });
      case 'priceMax':
        return t('marketplace.chips.priceMax', { value: formatMoney(Number(chip.value)) });
      case 'maxDeliveryDays':
        return t('marketplace.chips.maxDeliveryDays', { days: chip.value });
      case 'compatibleModel':
        return t('marketplace.chips.compatibleModel', { value: chip.value });
      case 'hasImages':
        return t('marketplace.filters.hasImages');
      case 'allStock':
        return t('marketplace.chips.allStock');
      case 'sort':
        return t('marketplace.sort');
    }
  }

  // ---------------------------------------------------------------- detail modal

  protected openOffer(l: Listing): void {
    void this.router.navigate([], { relativeTo: this.route, queryParams: { offer: l.offerId }, queryParamsHandling: 'merge' });
  }

  protected closeOffer(): void {
    if (!this.offerId()) return;
    void this.router.navigate([], { relativeTo: this.route, queryParams: { offer: null }, queryParamsHandling: 'merge', replaceUrl: true });
  }

  /** Row click opens the modal unless the click was on an interactive element (cart controls, product button). */
  protected onRowClick(event: MouseEvent, l: Listing): void {
    const target = event.target as HTMLElement | null;
    if (target?.closest('button, a, input, label, select, textarea, [data-row-ignore]')) return;
    this.openOffer(l);
  }

  protected onRowKeydown(event: KeyboardEvent, l: Listing): void {
    if (event.key !== 'Enter' || event.target !== event.currentTarget) return;
    event.preventDefault();
    this.openOffer(l);
  }

  // ---------------------------------------------------------------- keyboard

  /** "/" focuses the search box (unless the user is typing in a field or a dialog is open). */
  protected onKeydown(event: KeyboardEvent): void {
    if (event.key !== '/' || event.ctrlKey || event.metaKey || event.altKey || this.offerId()) return;
    const target = event.target as HTMLElement | null;
    if (target && (target.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName))) return;
    event.preventDefault();
    this.searchBox().nativeElement.focus();
    this.searchBox().nativeElement.select();
  }

  // ---------------------------------------------------------------- add to cart (row)

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
    const p = quantityProblem(l, this.quantityFor(l), this.cart.quantityOf(l.offerId));
    return p ? this.i18n.t(p.key, p.params) : null;
  }

  protected canOrder(l: Listing): boolean {
    return !this.isOwn(l) && l.available >= l.minOrderQuantity && this.session.can('orders.place');
  }

  protected addToCart(l: Listing): void {
    if (this.quantityError(l) || !this.canOrder(l)) return;
    const q = this.quantityFor(l);
    this.cart.add(l, q);
    this.toast.success(this.i18n.t('marketplace.added', { qty: q, name: `${l.brand} ${l.productName}` }));
  }
}
