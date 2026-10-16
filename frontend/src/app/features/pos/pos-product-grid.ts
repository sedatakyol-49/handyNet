import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { CatalogApi, InventoryApi } from '../../core/api/handynet-api';
import { StockRow } from '../../core/api/models';
import { StorageService } from '../../core/storage.service';
import { UI } from '../../shared/ui';
import { ProductTile, filterTiles, initials, productTiles, tileCategories } from './pos.logic';

/** Catalog categories are global and practically immutable: cached per browser. */
const CATEGORY_CACHE_KEY = 'hn.pos.productCategories';
const STOCK_PAGE_SIZE = 200;
const MAX_STOCK_PAGES = 3;
const MAX_CATEGORY_LOOKUPS = 300;
const LOOKUP_CONCURRENCY = 4;
const FALLBACK_CATEGORY_ORDER = [
  'Smartphone', 'Tablet', 'Wearable', 'Display', 'Battery', 'Housing', 'Camera', 'ChargingPort', 'SmallParts', 'Accessory', 'Tool', 'Other',
];

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
 * Product area of the selling screen: category filter and tiles for the products stocked in the register's
 * warehouse (`GET /api/inventory/stock`). The API has no product images or sales prices, so tiles show the category
 * icon (or a monogram) and the last price used in this browser session.
 */
@Component({
  selector: 'hn-pos-product-grid',
  imports: [...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'flex min-h-0 flex-col' },
  template: `
    @if (categories().length > 1) {
      <div class="flex shrink-0 gap-1.5 overflow-x-auto px-3 pb-2" role="group" [attr.aria-label]="'pos.sell.categories' | t" data-testid="pos-categories">
        <button type="button" class="pos-chip" [attr.aria-pressed]="category() === null" (click)="category.set(null)" data-testid="pos-category-all">
          {{ 'pos.sell.allCategories' | t }}
        </button>
        @for (c of categories(); track c) {
          <button type="button" class="pos-chip" [attr.aria-pressed]="category() === c" (click)="category.set(c)" [attr.data-testid]="'pos-category-' + c">
            {{ c | tEnum: 'category' }}
          </button>
        }
      </div>
    }

    <div class="min-h-0 flex-1 overflow-y-auto px-3 pb-3">
      @if (error()) {
        <hn-banner [error]="error()" />
      } @else if (loading() && !rows().length) {
        <p class="py-10 text-center text-sm text-slate-500">{{ 'common.loading' | t }}</p>
      } @else if (!tiles().length) {
        <div class="py-10 text-center text-sm text-slate-500" data-testid="pos-products-empty">
          <p class="font-medium text-slate-700">{{ 'pos.sell.noProducts' | t }}</p>
          <p class="mt-1">{{ 'pos.sell.noProductsHint' | t }}</p>
        </div>
      } @else if (!visible().length) {
        <p class="py-10 text-center text-sm text-slate-500" data-testid="pos-products-none">{{ 'pos.sell.noMatch' | t }}</p>
      } @else {
        <ul class="grid grid-cols-[repeat(auto-fill,minmax(9.5rem,1fr))] gap-2" [attr.aria-label]="'pos.sell.products' | t" data-testid="pos-products">
          @for (p of visible(); track p.productId) {
            @let soldOut = p.available <= 0;
            @let last = lastPrice()(p.productId);
            <li>
              <button type="button" class="pos-tile" [disabled]="soldOut" (click)="pick.emit(p)" [attr.data-product]="p.name" data-testid="pos-product">
                <span class="grid h-16 place-items-center rounded-md bg-slate-100 text-slate-600" aria-hidden="true">
                  @if (p.category && icons[p.category]) {
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
                  <span class="tabular-nums font-semibold text-slate-800">
                    @if (last !== null) {
                      {{ last | money }}
                    }
                  </span>
                  <span class="rounded px-1.5 py-0.5 font-medium tabular-nums" [class]="soldOut ? 'bg-red-50 text-red-800' : p.available <= 3 ? 'bg-amber-50 text-amber-900' : 'bg-slate-100 text-slate-700'">
                    {{ soldOut ? ('pos.sell.soldOut' | t) : ('pos.sell.stock' | t: { count: p.available }) }}
                  </span>
                </span>
              </button>
            </li>
          }
        </ul>
        @if (truncated()) {
          <p class="mt-3 text-center text-xs text-slate-500">{{ 'pos.sell.truncated' | t: { count: rows().length } }}</p>
        }
      }
    </div>
  `,
})
export class PosProductGridComponent {
  private readonly inventory = inject(InventoryApi);
  private readonly catalog = inject(CatalogApi);
  private readonly storage = inject(StorageService);
  private readonly destroyRef = inject(DestroyRef);

  readonly warehouseId = input<string | null>(null);
  readonly search = input('');
  /** Last price used for a product in this browser session (cents) or null. */
  readonly lastPrice = input<(productId: string) => number | null>(() => null);
  readonly pick = output<ProductTile>();

  protected readonly icons = ICONS;
  protected readonly rows = signal<StockRow[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal<unknown>(null);
  protected readonly truncated = signal(false);
  private readonly categoryMap = signal<Record<string, string>>(this.readCache());
  private readonly categoryOrder = signal<string[]>(FALLBACK_CATEGORY_ORDER);
  protected readonly category = signal<string | null>(null);

  protected readonly tiles = computed(() => productTiles(this.rows(), this.categoryMap()));
  protected readonly categories = computed(() => tileCategories(this.tiles(), this.categoryOrder()));
  /** Tiles after category and search filter (read by the selling screen: Enter adds a single match). */
  readonly visible = computed(() => {
    const list = filterTiles(this.tiles(), this.category(), this.search());
    // Products in stock first; sold-out ones stay visible (greyed) so the cashier sees why.
    return [...list.filter((t) => t.available > 0), ...list.filter((t) => t.available <= 0)];
  });

  private destroyed = false;
  private loadToken = 0;

  constructor() {
    this.destroyRef.onDestroy(() => (this.destroyed = true));
    effect(() => {
      const warehouse = this.warehouseId();
      untracked(() => void this.load(warehouse));
    });
    firstValueFrom(this.catalog.categories())
      .then((c) => c.categories.length && this.categoryOrder.set(c.categories))
      .catch(() => undefined);
  }

  /** Re-reads the stock (after a sale the available quantities changed). */
  reload(): void {
    void this.load(this.warehouseId());
  }

  protected monogram(p: ProductTile): string {
    return initials(p.brand, p.name);
  }

  private async load(warehouseId: string | null): Promise<void> {
    const token = ++this.loadToken;
    if (!warehouseId) {
      this.rows.set([]);
      return;
    }
    this.loading.set(true);
    this.error.set(null);
    try {
      const all: StockRow[] = [];
      let total = 0;
      for (let page = 1; page <= MAX_STOCK_PAGES; page++) {
        const result = await firstValueFrom(this.inventory.stock({ warehouseId, page, pageSize: STOCK_PAGE_SIZE }));
        all.push(...result.items);
        total = result.totalCount;
        if (all.length >= total || !result.items.length) break;
      }
      if (token !== this.loadToken || this.destroyed) return;
      this.rows.set(all);
      this.truncated.set(total > all.length);
      void this.resolveCategories(all, token);
    } catch (e) {
      if (token === this.loadToken) this.error.set(e);
    } finally {
      if (token === this.loadToken) this.loading.set(false);
    }
  }

  /** Stock rows carry no category: look missing ones up once (few parallel requests) and cache them. */
  private async resolveCategories(rows: StockRow[], token: number): Promise<void> {
    const known = this.categoryMap();
    const missing = [...new Set(rows.map((r) => r.productId).filter((id) => !known[id]))].slice(0, MAX_CATEGORY_LOOKUPS);
    if (!missing.length) return;
    const found: Record<string, string> = {};
    let next = 0;
    const worker = async () => {
      while (next < missing.length && token === this.loadToken && !this.destroyed) {
        const id = missing[next++];
        try {
          const product = await firstValueFrom(this.catalog.product(id));
          if (product.category) found[id] = product.category;
        } catch {
          /* unknown category: the tile shows a monogram */
        }
      }
    };
    await Promise.all(Array.from({ length: LOOKUP_CONCURRENCY }, worker));
    if (this.destroyed || !Object.keys(found).length) return;
    const merged = { ...this.categoryMap(), ...found };
    this.categoryMap.set(merged);
    this.storage.setJson(CATEGORY_CACHE_KEY, merged);
  }

  private readCache(): Record<string, string> {
    const cached = this.storage.getJson<Record<string, unknown>>(CATEGORY_CACHE_KEY);
    if (!cached || typeof cached !== 'object') return {};
    return Object.fromEntries(Object.entries(cached).filter((e): e is [string, string] => typeof e[1] === 'string'));
  }
}
