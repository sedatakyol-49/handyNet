import { ChangeDetectionStrategy, Component, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { PosApi } from '../../../core/api/handynet-api';
import { PosProduct } from '../../../core/api/models';
import { parseMoneyToMinor } from '../../../core/format/money';
import { I18nService } from '../../../core/i18n/i18n.service';
import { ToastService } from '../../../core/ui-state/toast.service';
import { UI } from '../../../shared/ui';
import { posErrorText } from '../pos-error';
import { PosSubnavComponent } from '../pos-subnav';
import { minorToPlain } from '../pos.logic';
import { Pricelist, PricelistApi, PricelistItem } from './pricelist-api';
import { PricelistForm, PricelistFormErrors, basisPointsToPercent, discountedPrice, pricelistInput } from './pricing.logic';

/** "31.12.2026" for a calendar date (no time-zone shift). */
function day(value: string | null): string {
  const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(value ?? '');
  return m ? `${m[3]}.${m[2]}.${m[1]}` : '';
}

/**
 * Kasse → Preislisten (`pos.manage`): customer-specific prices. A list gives item prices for chosen products and an
 * optional general discount off the till price for all others; it applies to the customers it is assigned to while
 * active and valid. Lists are never deleted (receipts refer to them) — they are deactivated.
 */
@Component({
  selector: 'hn-pos-pricelists-page',
  host: { class: 'pos-page block' },
  imports: [FormsModule, RouterLink, PosSubnavComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-pos-subnav [registerId]="registerId()" />
    <div class="mb-4 flex flex-wrap items-end justify-between gap-3">
      <div class="min-w-0">
        <h1 class="text-2xl font-extrabold tracking-tight">{{ 'pos.pricelists.title' | t }}</h1>
        <p class="mt-1 max-w-3xl text-sm text-pos-muted">{{ 'pos.pricelists.subtitle' | t }}</p>
      </div>
      <button type="button" class="pos-soft pos-soft-accent" (click)="openCreate()" data-testid="pos-pricelist-new">{{ 'pos.pricelists.new' | t }}</button>
    </div>
    <hn-banner class="mb-3" [text]="loadError()" />

    @if (loaded() && !lists().length) {
      <div class="pos-card mb-4"><hn-empty-state [title]="'pos.pricelists.emptyTitle' | t" [text]="'pos.pricelists.emptyText' | t" /></div>
    }
    <ul class="grid gap-3 md:grid-cols-2" data-testid="pos-pricelists">
      @for (p of lists(); track p.id) {
        <li class="pos-card flex flex-col p-4" [attr.data-pricelist]="p.name">
          <div class="flex items-start justify-between gap-3">
            <h2 class="min-w-0 truncate text-lg font-bold">{{ p.name }}</h2>
            <span class="pos-pill shrink-0" [class]="p.currentlyValid ? 'bg-pos-ok-soft text-pos-ok-text' : 'bg-pos-key text-pos-muted'" data-testid="pos-pricelist-state">
              {{ (p.currentlyValid ? 'pos.pricelists.valid' : p.isActive ? 'pos.pricelists.notValid' : 'pos.pricelists.inactive') | t }}
            </span>
          </div>
          <dl class="mt-3 grid grid-cols-2 gap-2 text-sm sm:grid-cols-4">
            <div class="rounded-[10px] bg-pos-surface px-3 py-2"><dt class="text-xs text-pos-muted">{{ 'pos.pricelists.discount' | t }}</dt><dd class="font-semibold tabular-nums">{{ percent(p.discountBasisPoints) }} %</dd></div>
            <div class="rounded-[10px] bg-pos-surface px-3 py-2"><dt class="text-xs text-pos-muted">{{ 'pos.pricelists.items' | t }}</dt><dd class="font-semibold tabular-nums" data-testid="pos-pricelist-items">{{ p.itemCount }}</dd></div>
            <div class="rounded-[10px] bg-pos-surface px-3 py-2"><dt class="text-xs text-pos-muted">{{ 'pos.pricelists.customers' | t }}</dt><dd class="font-semibold tabular-nums">{{ p.customerCount }}</dd></div>
            <div class="rounded-[10px] bg-pos-surface px-3 py-2"><dt class="text-xs text-pos-muted">{{ 'pos.pricelists.validity' | t }}</dt><dd class="text-xs font-semibold">{{ validity(p) }}</dd></div>
          </dl>
          <div class="mt-auto flex flex-wrap gap-2 pt-3">
            <a class="pos-soft pos-soft-accent" [routerLink]="['/kasse', registerId(), 'preislisten', p.id]" data-testid="pos-pricelist-open">{{ 'pos.pricelists.editItems' | t }}</a>
            <button type="button" class="pos-soft" (click)="openEdit(p)" data-testid="pos-pricelist-edit">{{ 'common.edit' | t }}</button>
          </div>
        </li>
      }
    </ul>

    <hn-dialog [title]="(editing() ? 'pos.pricelists.editTitle' : 'pos.pricelists.newTitle') | t" [open]="formOpen()" testId="pos-pricelist-form" (closed)="closeForm()">
      @if (formOpen()) {
        <hn-banner class="mb-3" [text]="formError()" />
        <form class="grid gap-3 sm:grid-cols-2" (ngSubmit)="save()" novalidate>
          <hn-field class="sm:col-span-2" [label]="'common.name' | t" for="pl-name" [required]="true" [errors]="err('name')">
            <input hnInput id="pl-name" name="name" autocomplete="off" maxlength="100" [(ngModel)]="form.name" data-testid="pl-name" />
          </hn-field>
          <hn-field class="sm:col-span-2" [label]="'pos.pricelists.form.discount' | t" for="pl-discount" [hint]="'pos.pricelists.form.discountHint' | t" [errors]="err('discountPercent')">
            <input hnInput id="pl-discount" name="discount" inputmode="decimal" autocomplete="off" class="w-28 text-end" [(ngModel)]="form.discountPercent" aria-describedby="pl-discount-hint" data-testid="pl-discount" />
          </hn-field>
          <hn-field [label]="'pos.pricelists.form.validFrom' | t" for="pl-from">
            <input hnInput id="pl-from" name="from" type="date" class="h-11" [(ngModel)]="form.validFrom" data-testid="pl-from" />
          </hn-field>
          <hn-field [label]="'pos.pricelists.form.validTo' | t" for="pl-to" [errors]="err('validTo')">
            <input hnInput id="pl-to" name="to" type="date" class="h-11" [(ngModel)]="form.validTo" data-testid="pl-to" />
          </hn-field>
          <label class="flex min-h-11 items-start gap-2 text-sm sm:col-span-2">
            <input type="checkbox" class="hn-checkbox mt-1" name="active" [(ngModel)]="form.isActive" data-testid="pl-active" />
            <span><span class="font-medium">{{ 'pos.pricelists.form.active' | t }}</span><span class="block text-xs text-pos-muted">{{ 'pos.pricelists.form.activeHint' | t }}</span></span>
          </label>
          <div class="flex justify-end gap-2 sm:col-span-2">
            <button hnButton class="min-h-11" type="button" (click)="closeForm()">{{ 'common.cancel' | t }}</button>
            <button hnButton class="min-h-11" variant="primary" type="submit" [loading]="saving()" [disabled]="saving()" data-testid="pl-save">{{ 'common.save' | t }}</button>
          </div>
        </form>
      }
    </hn-dialog>
  `,
})
export class PosPricelistsPage {
  readonly registerId = input.required<string>();
  private readonly api = inject(PricelistApi);
  private readonly i18n = inject(I18nService);
  private readonly toast = inject(ToastService);

  protected readonly lists = signal<Pricelist[]>([]);
  protected readonly loaded = signal(false);
  protected readonly loadError = signal<string | null>(null);
  protected readonly formOpen = signal(false);
  protected readonly editing = signal<Pricelist | null>(null);
  protected form: PricelistForm = { name: '', discountPercent: '', isActive: true, validFrom: '', validTo: '' };
  protected readonly errors = signal<PricelistFormErrors>({});
  protected readonly formError = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly percent = basisPointsToPercent;

  ngOnInit(): void {
    void this.load();
  }

  protected validity(p: Pricelist): string {
    if (!p.validFrom && !p.validTo) return this.i18n.t('pos.pricelists.always');
    return `${day(p.validFrom) || '…'} – ${day(p.validTo) || '…'}`;
  }

  protected err(key: keyof PricelistFormErrors): string | null {
    const k = this.errors()[key];
    return k ? this.i18n.t(k) : null;
  }

  private async load(): Promise<void> {
    try {
      this.lists.set(await firstValueFrom(this.api.list()));
      this.loadError.set(null);
    } catch (e) {
      this.loadError.set(posErrorText(this.i18n, e));
    } finally {
      this.loaded.set(true);
    }
  }

  protected openCreate(): void {
    this.form = { name: '', discountPercent: '', isActive: true, validFrom: '', validTo: '' };
    this.editing.set(null);
    this.errors.set({});
    this.formError.set(null);
    this.formOpen.set(true);
  }

  protected openEdit(p: Pricelist): void {
    this.form = { name: p.name, discountPercent: p.discountBasisPoints ? basisPointsToPercent(p.discountBasisPoints) : '', isActive: p.isActive, validFrom: p.validFrom ?? '', validTo: p.validTo ?? '' };
    this.editing.set(p);
    this.errors.set({});
    this.formError.set(null);
    this.formOpen.set(true);
  }

  protected closeForm(): void {
    if (!this.saving()) this.formOpen.set(false);
  }

  protected async save(): Promise<void> {
    const editing = this.editing();
    const { input, errors } = pricelistInput(this.form, editing?.version ?? null);
    this.errors.set(errors);
    if (!input) return;
    this.saving.set(true);
    this.formError.set(null);
    try {
      await firstValueFrom(editing ? this.api.update(editing.id, input) : this.api.create(input));
      this.toast.success(this.i18n.t('pos.pricelists.saved', { name: input.name }));
      this.formOpen.set(false);
      await this.load();
    } catch (e) {
      this.formError.set(posErrorText(this.i18n, e));
    } finally {
      this.saving.set(false);
    }
  }
}

/**
 * One pricelist's item prices: search the list's items, add a product of the register's warehouse with its price, change
 * or remove item prices. Shows the regular till price next to each list price.
 */
@Component({
  selector: 'hn-pos-pricelist-items-page',
  host: { class: 'pos-page block' },
  imports: [FormsModule, RouterLink, PosSubnavComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-pos-subnav [registerId]="registerId()" />
    <a class="pos-back mb-2" [routerLink]="['/kasse', registerId(), 'preislisten']"><span class="inline-block rtl:-scale-x-100" aria-hidden="true">←</span> {{ 'pos.pricelists.title' | t }}</a>
    <div class="mb-4 flex flex-wrap items-end justify-between gap-3">
      <div class="min-w-0">
        <h1 class="text-2xl font-extrabold tracking-tight" data-testid="pos-pricelist-name">{{ list()?.name ?? '…' }}</h1>
        @if (list(); as l) {
          <p class="mt-1 text-sm text-pos-muted">
            {{ 'pos.pricelists.itemsSubtitle' | t: { percent: percent(l.discountBasisPoints) } }}
          </p>
        }
      </div>
      <button type="button" class="pos-soft pos-soft-accent" (click)="openAdd()" data-testid="pos-pricelist-add">{{ 'pos.pricelists.addItem' | t }}</button>
    </div>
    <hn-banner class="mb-3" [text]="error()" />

    <div class="mb-3" role="search">
      <label for="pl-items-search" class="sr-only">{{ 'pos.pricelists.searchItems' | t }}</label>
      <input hnInput id="pl-items-search" type="search" class="h-11 w-full sm:max-w-md" autocomplete="off" [placeholder]="'pos.pricelists.searchItems' | t"
        [value]="search()" (input)="search.set($any($event.target).value)" data-testid="pl-items-search" />
    </div>

    @if (loaded() && !items().length) {
      <div class="pos-card"><hn-empty-state [title]="'pos.pricelists.noItemsTitle' | t" [text]="'pos.pricelists.noItemsText' | t" /></div>
    } @else {
      <ul class="pos-card divide-y divide-pos-line" data-testid="pl-items">
        @for (it of items(); track it.productId) {
          <li class="flex flex-wrap items-center gap-x-4 gap-y-2 px-4 py-3" [attr.data-item]="it.name" data-testid="pl-item">
            <div class="min-w-0 basis-full sm:basis-0 sm:flex-1">
              <p class="font-semibold">{{ it.brand }} {{ it.name }}</p>
              <p class="text-xs text-pos-muted">{{ it.ean ?? '–' }}</p>
            </div>
            <div class="text-end text-sm">
              <p class="text-lg font-bold tabular-nums" data-testid="pl-item-price">{{ it.priceMinor | money }}</p>
              <p class="text-xs text-pos-muted">{{ 'pos.pricelists.basePrice' | t }}: {{ it.basePriceMinor === null ? '–' : (it.basePriceMinor | money) }}</p>
            </div>
            <div class="ms-auto flex gap-2">
              <button type="button" class="pos-soft" (click)="openEdit(it)" data-testid="pl-item-edit">{{ 'common.edit' | t }}</button>
              <button type="button" class="pos-soft text-pos-danger" (click)="remove(it)" [attr.aria-label]="'pos.pricelists.removeItemFor' | t: { name: it.name }" data-testid="pl-item-remove">{{ 'common.remove' | t }}</button>
            </div>
          </li>
        }
      </ul>
      @if (items().length < total()) {
        <button hnButton type="button" class="mt-3 min-h-11" (click)="loadPage(page + 1)">{{ 'pos.sell.loadMore' | t: { shown: items().length, total: total() } }}</button>
      }
    }

    <hn-dialog [title]="(target() ? 'pos.pricelists.itemTitle' : 'pos.pricelists.addItem') | t" [open]="itemOpen()" [wide]="!target()" testId="pl-item-dialog" (closed)="itemOpen.set(false)">
      @if (itemOpen()) {
        @if (!target()) {
          <label for="pl-product-search" class="mb-1 block text-sm font-medium">{{ 'pos.pricelists.productSearch' | t }}</label>
          <input hnInput id="pl-product-search" type="search" class="h-11 w-full" autocomplete="off" [value]="productSearch()" (input)="productSearch.set($any($event.target).value)" data-testid="pl-product-search" />
          <ul class="mt-2 max-h-72 divide-y divide-slate-100 overflow-y-auto rounded-lg border border-slate-200" data-testid="pl-products">
            @for (p of products(); track p.productId) {
              <li>
                <button type="button" class="min-h-12 w-full px-3 py-2 text-start hover:bg-slate-50" (click)="pick(p)" data-testid="pl-product">
                  <span class="block font-semibold">{{ p.brand }} {{ p.name }}</span>
                  <span class="block text-xs text-slate-500">{{ 'pos.pricelists.basePrice' | t }}: {{ p.priceMinor === null ? '–' : (p.priceMinor | money) }}</span>
                </button>
              </li>
            } @empty {
              <li class="px-3 py-6 text-center text-sm text-slate-500">{{ (productSearch().trim() ? 'pos.customers.noMatch' : 'pos.pricelists.productSearchHint') | t }}</li>
            }
          </ul>
        } @else {
          @let t = target()!;
          <form (ngSubmit)="saveItem()" novalidate>
            <p class="font-semibold">{{ t.brand }} {{ t.name }}</p>
            <p class="mb-3 text-sm text-slate-500">
              {{ 'pos.pricelists.basePrice' | t }}: {{ t.basePriceMinor === null ? '–' : (t.basePriceMinor | money) }}
              @if (t.basePriceMinor !== null && list()?.discountBasisPoints) {
                · {{ 'pos.pricelists.withDiscount' | t: { price: (discounted(t.basePriceMinor) | money) } }}
              }
            </p>
            <hn-field [label]="'pos.pricelists.listPrice' | t" for="pl-item-price" [required]="true" [errors]="priceError()">
              <input hnInput id="pl-item-price" name="price" inputmode="decimal" autocomplete="off" class="h-12 w-40 text-end text-lg tabular-nums" [(ngModel)]="priceText" data-testid="pl-item-price-input" />
            </hn-field>
            <div class="mt-4 flex justify-end gap-2">
              <button hnButton class="min-h-11" type="button" (click)="itemOpen.set(false)">{{ 'common.cancel' | t }}</button>
              <button hnButton class="min-h-11" variant="primary" type="submit" [loading]="saving()" [disabled]="saving()" data-testid="pl-item-save">{{ 'common.save' | t }}</button>
            </div>
          </form>
        }
      }
    </hn-dialog>
  `,
})
export class PosPricelistItemsPage {
  readonly registerId = input.required<string>();
  readonly id = input.required<string>();
  private readonly api = inject(PricelistApi);
  private readonly pos = inject(PosApi);
  private readonly i18n = inject(I18nService);
  private readonly toast = inject(ToastService);

  protected readonly list = signal<Pricelist | null>(null);
  protected readonly items = signal<PricelistItem[]>([]);
  protected readonly total = signal(0);
  protected readonly loaded = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly search = signal('');
  protected page = 1;
  protected readonly percent = basisPointsToPercent;

  protected readonly itemOpen = signal(false);
  protected readonly target = signal<{ productId: string; brand: string; name: string; basePriceMinor: number | null } | null>(null);
  protected readonly productSearch = signal('');
  protected readonly products = signal<PosProduct[]>([]);
  protected priceText = '';
  protected readonly priceError = signal<string | null>(null);
  protected readonly saving = signal(false);

  constructor() {
    effect((onCleanup) => {
      this.search();
      this.id();
      const t = setTimeout(() => untracked(() => void this.loadPage(1)), 250);
      onCleanup(() => clearTimeout(t));
    });
    effect((onCleanup) => {
      const q = this.productSearch().trim();
      if (!this.itemOpen() || this.target()) return;
      const t = setTimeout(
        () =>
          untracked(() =>
            firstValueFrom(this.pos.products(this.registerId(), { search: q || null, includeOutOfStock: true, page: 1, pageSize: 20 })).then(
              (r) => this.products.set(r.items),
              () => this.products.set([]),
            ),
          ),
        250,
      );
      onCleanup(() => clearTimeout(t));
    });
  }

  ngOnInit(): void {
    void firstValueFrom(this.api.get(this.id())).then((l) => this.list.set(l), (e) => this.error.set(posErrorText(this.i18n, e)));
  }

  protected discounted(base: number): number {
    return discountedPrice(base, this.list()?.discountBasisPoints ?? 0);
  }

  async loadPage(page: number): Promise<void> {
    try {
      const r = await firstValueFrom(this.api.items(this.id(), { search: this.search().trim() || null, page, pageSize: 50 }));
      this.items.update((list) => (page === 1 ? r.items : [...list, ...r.items]));
      this.total.set(r.totalCount);
      this.page = page;
      this.error.set(null);
    } catch (e) {
      this.error.set(posErrorText(this.i18n, e));
    } finally {
      this.loaded.set(true);
    }
  }

  protected openAdd(): void {
    this.target.set(null);
    this.productSearch.set('');
    this.products.set([]);
    this.itemOpen.set(true);
  }

  protected pick(p: PosProduct): void {
    this.target.set({ productId: p.productId, brand: p.brand, name: p.name, basePriceMinor: p.basePriceMinor ?? p.priceMinor });
    this.priceText = '';
    this.priceError.set(null);
  }

  protected openEdit(it: PricelistItem): void {
    this.target.set({ productId: it.productId, brand: it.brand, name: it.name, basePriceMinor: it.basePriceMinor });
    this.priceText = minorToPlain(it.priceMinor);
    this.priceError.set(null);
    this.itemOpen.set(true);
  }

  protected async saveItem(): Promise<void> {
    const t = this.target();
    const price = parseMoneyToMinor(this.priceText);
    if (!t) return;
    if (price === null || price <= 0) return this.priceError.set(this.i18n.t('pos.pricelists.priceInvalid'));
    this.saving.set(true);
    try {
      await firstValueFrom(this.api.setItem(this.id(), t.productId, price));
      this.toast.success(this.i18n.t('pos.pricelists.itemSaved', { name: t.name }));
      this.itemOpen.set(false);
      await this.loadPage(1);
    } catch (e) {
      this.priceError.set(posErrorText(this.i18n, e));
    } finally {
      this.saving.set(false);
    }
  }

  protected async remove(it: PricelistItem): Promise<void> {
    try {
      await firstValueFrom(this.api.removeItem(this.id(), it.productId));
      this.toast.success(this.i18n.t('pos.pricelists.itemRemoved', { name: it.name }));
      await this.loadPage(1);
    } catch (e) {
      this.toast.error(posErrorText(this.i18n, e));
    }
  }
}

