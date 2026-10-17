import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { PosApi } from '../../core/api/handynet-api';
import { PosCategoryCount, PosProduct } from '../../core/api/models';
import { UI } from '../../shared/ui';
import { gridSearch, initials, mergeProductPages } from './pos.logic';

const PAGE_SIZE = 48;
const SEARCH_DEBOUNCE_MS = 250;
const LONG_PRESS_MS = 550;

/** Outline icons per catalog category (24×24, stroke). */
const ICONS: Record<string, string> = {
  Smartphone: 'M8 3h8a1.5 1.5 0 0 1 1.5 1.5v15A1.5 1.5 0 0 1 16 21H8a1.5 1.5 0 0 1-1.5-1.5v-15A1.5 1.5 0 0 1 8 3zM11 18h2',
  Tablet: 'M5.5 3h13A1.5 1.5 0 0 1 20 4.5v15a1.5 1.5 0 0 1-1.5 1.5h-13A1.5 1.5 0 0 1 4 19.5v-15A1.5 1.5 0 0 1 5.5 3zM11 18h2',
  Wearable: 'M9 2h6l1 4H8zM8 18l1 4h6l1-4M7 6h10v12H7z',
  Display: 'M3 4h18v12H3zM8 20h8M12 16v4',
  Battery: 'M3 8h16v8H3zM19 10.5h2v3h-2M6 11v2M9 11v2',
  Housing: 'M7 3h10a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2zM9 6.5h2.5',
  Camera: 'M4 7h4l1.5-2h5L16 7h4v12H4zM12 10a3 3 0 1 0 0 6 3 3 0 0 0 0-6z',
  ChargingPort: 'M9 3v5M15 3v5M7 8h10v4a5 5 0 0 1-10 0zM12 17v4',
  SmallParts: 'M12 3l7.5 4.5v9L12 21l-7.5-4.5v-9zM12 12l7.5-4.5M12 12v9M12 12 4.5 7.5',
  Accessory: 'M4 15v-3a8 8 0 0 1 16 0v3M4 15h3v5H5a1 1 0 0 1-1-1zM20 15h-3v5h2a1 1 0 0 0 1-1z',
  Tool: 'M14.5 6.5a4 4 0 0 0 5 5L12 19a2.1 2.1 0 0 1-3-3l7.5-7.5a4 4 0 0 0-2-2zM6 18l-2 2',
  Other: 'M4 7.5 12 3l8 4.5v9L12 21l-8-4.5zM4 7.5l8 4.5 8-4.5M12 12v9',
};

/**
 * Product area of the selling screen: category chips with counts and tiles for the products of the register's
 * warehouse with their till prices (`GET /api/pos/registers/{id}/products`, server search, paged with infinite
 * scroll and a "load more" fallback). With `pos.manage` a tile's price can be edited (corner button, long-press or
 * right-click).
 */
@Component({
  selector: 'hn-pos-product-grid',
  imports: [...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'flex min-h-0 flex-col' },
  template: `
    <div class="flex shrink-0 gap-1.5 overflow-x-auto px-3 pb-2" role="group" [attr.aria-label]="'pos.sell.categories' | t" data-testid="pos-categories">
      <button type="button" class="pos-chip" [attr.aria-pressed]="category() === null" (click)="setCategory(null)" data-testid="pos-category-all">
        {{ 'pos.sell.allCategories' | t }}
        <span class="ms-1 text-xs tabular-nums opacity-75">{{ categoryTotal() }}</span>
      </button>
      @for (c of categories(); track c.category) {
        <button type="button" class="pos-chip" [attr.aria-pressed]="category() === c.category" (click)="setCategory(c.category)" [attr.data-testid]="'pos-category-' + c.category">
          {{ c.category | tEnum: 'category' }}
          <span class="ms-1 text-xs tabular-nums opacity-75">{{ c.productCount }}</span>
        </button>
      }
      <button type="button" class="pos-chip ms-auto" [attr.aria-pressed]="includeOutOfStock()" (click)="toggleOutOfStock()" data-testid="pos-show-soldout">
        {{ 'pos.sell.showSoldOut' | t }}
      </button>
    </div>

    <div #scroller class="min-h-0 flex-1 overflow-y-auto px-3 pb-3" [attr.aria-busy]="loading() || null">
      @if (error()) {
        <hn-banner [error]="error()" />
      } @else if (loading() && !items().length) {
        <p class="py-10 text-center text-sm text-slate-500">{{ 'common.loading' | t }}</p>
      } @else if (!items().length) {
        @if (appliedSearch() || category()) {
          <p class="py-10 text-center text-sm text-slate-500" data-testid="pos-products-none">{{ 'pos.sell.noMatch' | t }}</p>
        } @else {
          <div class="py-10 text-center text-sm text-slate-500" data-testid="pos-products-empty">
            <p class="font-medium text-slate-700">{{ 'pos.sell.noProducts' | t }}</p>
            <p class="mt-1">{{ 'pos.sell.noProductsHint' | t }}</p>
          </div>
        }
      } @else {
        <ul class="grid grid-cols-[repeat(auto-fill,minmax(9.5rem,1fr))] gap-2" [attr.aria-label]="'pos.sell.products' | t" data-testid="pos-products">
          @for (p of items(); track p.productId) {
            @let soldOut = p.available <= 0;
            <li class="relative">
              <button type="button" class="pos-tile" [disabled]="soldOut" (click)="onTileClick(p)" (contextmenu)="onContextMenu($event, p)"
                (pointerdown)="startPress(p)" (pointerup)="cancelPress()" (pointerleave)="cancelPress()" (pointercancel)="cancelPress()"
                [attr.data-product]="p.name" [attr.aria-describedby]="'pos-tile-price-' + p.productId" data-testid="pos-product">
                <span class="grid h-16 place-items-center overflow-hidden rounded-md bg-slate-100 text-slate-600" aria-hidden="true">
                  @if (p.imageUrl) {
                    <img [src]="p.imageUrl" alt="" class="size-full object-contain" loading="lazy" />
                  } @else if (icons[p.category]) {
                    <svg class="size-8" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">
                      <path [attr.d]="icons[p.category]" />
                    </svg>
                  } @else {
                    <span class="text-lg font-semibold tracking-wide">{{ monogram(p) }}</span>
                  }
                </span>
                <span class="mt-1.5 line-clamp-2 text-start text-sm font-semibold leading-snug text-slate-900">{{ p.name }}</span>
                <span class="truncate text-start text-xs text-slate-500">{{ p.brand }}</span>
                <span class="mt-auto flex items-end justify-between gap-1 pt-1.5 text-xs">
                  <span [id]="'pos-tile-price-' + p.productId" class="tabular-nums" data-testid="pos-product-price">
                    @if (p.priceMinor !== null) {
                      <span class="text-sm font-bold text-slate-900">{{ p.priceMinor | money }}</span>
                    } @else {
                      <span class="text-slate-500">{{ 'pos.sell.noPrice' | t }}</span>
                    }
                  </span>
                  <span class="rounded px-1.5 py-0.5 font-medium tabular-nums" [class]="soldOut ? 'bg-red-50 text-red-800' : p.available <= 3 ? 'bg-amber-50 text-amber-900' : 'bg-slate-100 text-slate-700'">
                    {{ soldOut ? ('pos.sell.soldOut' | t) : ('pos.sell.stock' | t: { count: p.available }) }}
                  </span>
                </span>
                @if (p.serialized) {
                  <span class="mt-1 text-start text-[11px] font-medium text-amber-900">{{ 'pos.sell.imeiOnly' | t }}</span>
                }
              </button>
              @if (canManagePrices()) {
                <button type="button" class="absolute end-1 top-1 grid size-11 place-items-center rounded-lg bg-white/90 text-slate-600 shadow-xs hover:text-accent-800"
                  (click)="editPrice.emit(p)" [attr.aria-label]="'pos.price.editFor' | t: { name: p.name }" [title]="'pos.price.edit' | t" data-testid="pos-product-edit-price">
                  <svg class="size-5" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" aria-hidden="true"><path d="M4 20h4L19 9l-4-4L4 16zM13.5 6.5l4 4" /></svg>
                </button>
              }
            </li>
          }
        </ul>
        @if (hasMore()) {
          <div #sentinel class="mt-3 text-center">
            <button hnButton type="button" class="min-h-11" [loading]="loading()" (click)="loadMore()" data-testid="pos-products-more">
              {{ 'pos.sell.loadMore' | t: { shown: items().length, total: total() } }}
            </button>
          </div>
        }
      }
    </div>
  `,
})
export class PosProductGridComponent {
  private readonly api = inject(PosApi);

  readonly registerId = input.required<string>();
  /** Text of the search/scan field (codes are looked up by the till, not searched). */
  readonly search = input('');
  readonly canManagePrices = input(false);
  readonly pick = output<PosProduct>();
  readonly editPrice = output<PosProduct>();

  protected readonly icons = ICONS;
  protected readonly items = signal<PosProduct[]>([]);
  protected readonly total = signal(0);
  protected readonly loading = signal(false);
  protected readonly error = signal<unknown>(null);
  protected readonly category = signal<string | null>(null);
  protected readonly includeOutOfStock = signal(false);
  protected readonly categories = signal<PosCategoryCount[]>([]);
  protected readonly categoryTotal = computed(() => this.categories().reduce((n, c) => n + c.productCount, 0));
  /** The search text the shown items belong to (debounced). */
  protected readonly appliedSearch = signal('');
  private page = 0;
  protected readonly hasMore = computed(() => this.items().length < this.total());

  private readonly scroller = viewChild<ElementRef<HTMLElement>>('scroller');
  private readonly sentinel = viewChild<ElementRef<HTMLElement>>('sentinel');
  private loadToken = 0;
  private destroyed = false;
  private pressTimer: ReturnType<typeof setTimeout> | null = null;
  private suppressClick = false;

  /** The only product matching the current search text (Enter adds it), or null. */
  readonly singleMatch = computed(() => {
    const wanted = gridSearch(this.search());
    if (!wanted || this.loading() || wanted !== this.appliedSearch() || this.total() !== 1) return null;
    const only = this.items()[0];
    return only && only.available > 0 ? only : null;
  });

  constructor() {
    const destroyRef = inject(DestroyRef);
    destroyRef.onDestroy(() => {
      this.destroyed = true;
      this.cancelPress();
    });
    // Debounced server search.
    effect((onCleanup) => {
      const wanted = gridSearch(this.search());
      if (wanted === untracked(() => this.appliedSearch())) return;
      const timer = setTimeout(() => this.appliedSearch.set(wanted), SEARCH_DEBOUNCE_MS);
      onCleanup(() => clearTimeout(timer));
    });
    effect(() => {
      const registerId = this.registerId();
      this.appliedSearch();
      this.category();
      this.includeOutOfStock();
      untracked(() => void this.loadFirst(registerId));
    });
    effect(() => {
      const registerId = this.registerId();
      untracked(() => void this.loadCategories(registerId));
    });
    // Infinite scroll: load the next page when the "load more" row scrolls into view.
    effect((onCleanup) => {
      const el = this.sentinel()?.nativeElement;
      const root = this.scroller()?.nativeElement;
      if (!el || !root || typeof IntersectionObserver === 'undefined') return;
      const observer = new IntersectionObserver((entries) => entries.some((e) => e.isIntersecting) && this.loadMore(), { root, rootMargin: '200px' });
      observer.observe(el);
      onCleanup(() => observer.disconnect());
    });
  }

  /** Re-reads the loaded pages and the category counts (after a sale or a price change). */
  reload(): void {
    void this.loadPages(this.registerId(), Math.max(this.page, 1));
    void this.loadCategories(this.registerId());
  }

  protected setCategory(c: string | null): void {
    this.category.set(c);
  }

  protected toggleOutOfStock(): void {
    this.includeOutOfStock.update((v) => !v);
  }

  protected monogram(p: PosProduct): string {
    return initials(p.brand, p.name);
  }

  // ---- tap, long-press (price), right-click (price)
  protected onTileClick(p: PosProduct): void {
    if (this.suppressClick) {
      this.suppressClick = false;
      return;
    }
    this.pick.emit(p);
  }

  protected startPress(p: PosProduct): void {
    this.cancelPress();
    if (!this.canManagePrices()) return;
    this.pressTimer = setTimeout(() => {
      this.pressTimer = null;
      this.suppressClick = true;
      this.editPrice.emit(p);
    }, LONG_PRESS_MS);
  }

  protected cancelPress(): void {
    if (this.pressTimer) clearTimeout(this.pressTimer);
    this.pressTimer = null;
  }

  protected onContextMenu(e: Event, p: PosProduct): void {
    if (!this.canManagePrices()) return;
    e.preventDefault();
    this.cancelPress();
    this.editPrice.emit(p);
  }

  // ---- loading
  protected loadMore(): void {
    if (this.loading() || !this.hasMore()) return;
    void this.loadPage(this.registerId(), this.page + 1, ++this.loadToken);
  }

  private loadFirst(registerId: string): Promise<void> {
    this.page = 0;
    this.items.set([]);
    this.total.set(0);
    this.scroller()?.nativeElement.scrollTo?.({ top: 0 });
    return this.loadPage(registerId, 1, ++this.loadToken);
  }

  private async loadPage(registerId: string, page: number, token: number): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const result = await firstValueFrom(this.api.products(registerId, this.queryFor(page)));
      if (token !== this.loadToken || this.destroyed) return;
      this.items.set(page === 1 ? result.items : mergeProductPages(this.items(), result.items));
      this.total.set(result.totalCount);
      this.page = page;
    } catch (e) {
      if (token === this.loadToken) this.error.set(e);
    } finally {
      if (token === this.loadToken) this.loading.set(false);
    }
  }

  private async loadPages(registerId: string, pages: number): Promise<void> {
    const token = ++this.loadToken;
    this.loading.set(true);
    try {
      let all: PosProduct[] = [];
      let total = 0;
      for (let page = 1; page <= pages; page++) {
        const result = await firstValueFrom(this.api.products(registerId, this.queryFor(page)));
        all = mergeProductPages(all, result.items);
        total = result.totalCount;
        if (all.length >= total || !result.items.length) break;
      }
      if (token !== this.loadToken || this.destroyed) return;
      this.items.set(all);
      this.total.set(total);
      this.page = Math.max(1, Math.ceil(all.length / PAGE_SIZE));
    } catch (e) {
      if (token === this.loadToken) this.error.set(e);
    } finally {
      if (token === this.loadToken) this.loading.set(false);
    }
  }

  private queryFor(page: number) {
    return {
      search: this.appliedSearch() || null,
      category: this.category(),
      includeOutOfStock: this.includeOutOfStock() || undefined,
      page,
      pageSize: PAGE_SIZE,
    };
  }

  private async loadCategories(registerId: string): Promise<void> {
    try {
      const list = await firstValueFrom(this.api.productCategories(registerId));
      if (!this.destroyed) this.categories.set(list);
    } catch {
      /* chips are optional: the grid still works without them */
    }
  }
}
