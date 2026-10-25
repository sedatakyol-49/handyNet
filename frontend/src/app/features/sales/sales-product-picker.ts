import { ChangeDetectionStrategy, Component, effect, inject, input, output, signal, untracked } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { SalesApi } from '../../core/api/handynet-api';
import { PosProduct } from '../../core/api/models';
import { I18nService } from '../../core/i18n/i18n.service';
import { UI } from '../../shared/ui';
import { posErrorText } from '../pos/pos-error';

/** Product search of a warehouse for quotation/invoice lines (touch list, keyboard: type, ↓/Enter via the list buttons). */
@Component({
  selector: 'hn-sales-product-picker',
  imports: [...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-dialog [title]="'sales.picker.title' | t" [open]="open()" [wide]="true" (closed)="closed.emit()" testId="sales-picker">
      @if (open()) {
        <form class="flex gap-2" role="search" (submit)="$event.preventDefault(); load()">
          <label for="sales-picker-search" class="sr-only">{{ 'sales.picker.search' | t }}</label>
          <input hnInput id="sales-picker-search" type="search" class="h-12 min-w-0 flex-1 text-base" autocomplete="off" [placeholder]="'sales.picker.search' | t"
            [value]="search()" (input)="search.set($any($event.target).value)" data-testid="sales-picker-search" autofocus />
          <button hnButton variant="primary" type="submit" class="min-h-12" [loading]="loading()">{{ 'sales.picker.find' | t }}</button>
        </form>
        <label class="mt-2 flex min-h-11 items-center gap-2 text-sm">
          <input type="checkbox" class="hn-checkbox" [checked]="all()" (change)="all.set($any($event.target).checked); load()" />
          {{ 'sales.picker.includeOutOfStock' | t }}
        </label>
        @if (error()) {
          <hn-banner class="mt-2" [text]="error()" />
        }
        <ul class="mt-2 max-h-[55vh] divide-y divide-slate-100 overflow-y-auto rounded-lg border border-slate-200" data-testid="sales-picker-list">
          @for (p of items(); track p.productId) {
            <li>
              <button type="button" class="flex min-h-14 w-full items-center justify-between gap-3 px-3 py-2 text-start hover:bg-slate-50 focus-visible:bg-slate-50"
                (click)="pick(p)" [attr.data-product]="p.name" data-testid="sales-picker-item">
                <span class="min-w-0">
                  <span class="block truncate font-semibold text-slate-900">{{ p.brand }} {{ p.name }}</span>
                  <span class="block text-xs text-slate-500">
                    {{ 'sales.picker.available' | t: { count: p.available } }}
                    @if (p.serialized) {
                      · {{ 'sales.picker.serialized' | t }}
                    }
                    @if (p.ean) {
                      · {{ p.ean }}
                    }
                  </span>
                </span>
                <span class="shrink-0 font-semibold tabular-nums">{{ p.priceMinor === null ? '–' : (p.priceMinor | money) }}</span>
              </button>
            </li>
          } @empty {
            <li class="px-3 py-8 text-center text-sm text-slate-500">{{ (loading() ? 'common.loading' : 'sales.picker.empty') | t }}</li>
          }
        </ul>
      }
    </hn-dialog>
  `,
})
export class SalesProductPickerComponent {
  private readonly api = inject(SalesApi);
  private readonly i18n = inject(I18nService);
  readonly open = input(false);
  readonly warehouseId = input<string | null>(null);
  readonly initialSearch = input('');
  readonly closed = output<void>();
  readonly picked = output<PosProduct>();

  protected readonly search = signal('');
  protected readonly all = signal(false);
  protected readonly items = signal<PosProduct[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);

  constructor() {
    effect(() => {
      if (!this.open()) return;
      const initial = this.initialSearch();
      untracked(() => {
        this.search.set(initial);
        void this.load();
      });
    });
  }

  protected async load(): Promise<void> {
    const warehouse = this.warehouseId();
    if (!warehouse) return;
    this.loading.set(true);
    this.error.set(null);
    try {
      const page = await firstValueFrom(this.api.products(warehouse, { search: this.search().trim() || null, includeOutOfStock: this.all(), pageSize: 50 }));
      this.items.set(page.items);
    } catch (e) {
      this.error.set(posErrorText(this.i18n, e));
    } finally {
      this.loading.set(false);
    }
  }

  protected pick(p: PosProduct): void {
    this.picked.emit(p);
  }
}
