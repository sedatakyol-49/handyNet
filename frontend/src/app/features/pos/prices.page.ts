import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { of } from 'rxjs';
import { PosApi } from '../../core/api/handynet-api';
import { PosProduct } from '../../core/api/models';
import { SessionStore } from '../../core/auth/session.store';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';
import { PosPriceDialogComponent, PriceTarget } from './pos-price-dialog';
import { posRights } from './pos-rights';
import { PosSubnavComponent } from './pos-subnav';

/** Price maintenance of a register's products (`pos.manage`): POS price, till price and its source, edit per product. */
@Component({
  selector: 'hn-pos-prices-page',
  host: { class: 'pos-page block' },
  imports: [PosPriceDialogComponent, PosSubnavComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-pos-subnav [registerId]="registerId()" />
    <hn-page-header [title]="'pos.price.pageTitle' | t" [description]="'pos.price.pageText' | t" />
    @if (!canManage()) {
      <hn-banner tone="warning" [text]="'pos.rights.manageOnly' | t" />
    } @else {
      <div class="mb-3 flex flex-wrap gap-2" role="search">
        <label for="pos-prices-search" class="sr-only">{{ 'pos.sell.searchLabel' | t }}</label>
        <input hnInput id="pos-prices-search" type="search" class="h-11 max-w-md flex-1" autocomplete="off" [placeholder]="'pos.price.searchPlaceholder' | t"
          [value]="search()" (input)="search.set($any($event.target).value)" data-testid="pos-prices-search" />
      </div>
      <hn-banner class="mb-3" [error]="products.error()" />
      <hn-table [loading]="products.loading()">
        <table class="hn-table" data-testid="pos-prices">
          <thead>
            <tr>
              <th scope="col">{{ 'common.product' | t }}</th>
              <th scope="col">EAN</th>
              <th scope="col" class="num">{{ 'pos.price.stock' | t }}</th>
              <th scope="col" class="num">{{ 'pos.price.posPrice' | t }}</th>
              <th scope="col" class="num">{{ 'pos.price.tillPrice' | t }}</th>
              <th scope="col">{{ 'pos.price.source' | t }}</th>
              <th scope="col"><span class="sr-only">{{ 'common.actions' | t }}</span></th>
            </tr>
          </thead>
          <tbody>
            @for (p of products.value()?.items ?? []; track p.productId) {
              <tr [attr.data-product]="p.name">
                <td><span class="font-medium">{{ p.name }}</span> <span class="block text-xs text-slate-500">{{ p.brand }}</span></td>
                <td class="hn-mono text-xs">{{ p.ean ?? '–' }}</td>
                <td class="num">{{ p.available }}</td>
                <td class="num">{{ p.posPriceMinor === null ? '–' : (p.posPriceMinor | money) }}</td>
                <td class="num font-semibold" data-testid="pos-prices-till">{{ p.priceMinor === null ? '–' : (p.priceMinor | money) }}</td>
                <td class="text-sm">{{ 'pos.price.sources.' + p.priceSource | t }}</td>
                <td class="text-end">
                  <button hnButton type="button" class="min-h-11" (click)="edit(p)" [attr.aria-label]="'pos.price.editFor' | t: { name: p.name }" data-testid="pos-prices-edit">{{ 'pos.price.edit' | t }}</button>
                </td>
              </tr>
            } @empty {
              <tr><td colspan="7" class="py-8 text-center text-sm text-slate-500">{{ 'pos.sell.noMatch' | t }}</td></tr>
            }
          </tbody>
        </table>
        <hn-pagination footer [page]="page()" [pageSize]="pageSize" [totalCount]="products.value()?.totalCount ?? 0" (pageChange)="page.set($event)" />
      </hn-table>
    }
    <hn-pos-price-dialog [product]="target()" [registerId]="registerId()" (closed)="target.set(null)" (saved)="products.reload()" />
  `,
})
export class PosPricesPage {
  private readonly api = inject(PosApi);
  private readonly store = inject(SessionStore);
  readonly registerId = input.required<string>();

  protected readonly pageSize = 50;
  protected readonly page = signal(1);
  protected readonly search = signal('');
  private readonly appliedSearch = signal('');
  protected readonly target = signal<PriceTarget | null>(null);
  protected readonly canManage = computed(() => posRights(this.store.membership()).manage);

  protected readonly products = createQuery(
    () => ({ id: this.registerId(), search: this.appliedSearch(), page: this.page(), ok: this.canManage() }),
    (q) =>
      q.ok
        ? this.api.products(q.id, { search: q.search || null, includeOutOfStock: true, page: q.page, pageSize: this.pageSize })
        : of(null),
  );

  constructor() {
    effect((onCleanup) => {
      const s = this.search().trim().slice(0, 100);
      const timer = setTimeout(() => {
        this.appliedSearch.set(s);
        this.page.set(1);
      }, 250);
      onCleanup(() => clearTimeout(timer));
    });
  }

  protected edit(p: PosProduct): void {
    this.target.set({ productId: p.productId, brand: p.brand, name: p.name });
  }
}
