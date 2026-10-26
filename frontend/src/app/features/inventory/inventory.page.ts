import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { firstValueFrom, of } from 'rxjs';
import { ApiProblem, fieldErrors, toApiProblem } from '../../core/api/api-problem';
import { InventoryApi } from '../../core/api/handynet-api';
import { StockRow } from '../../core/api/models';
import { SessionStore } from '../../core/auth/session.store';
import { I18nService } from '../../core/i18n/i18n.service';
import { ToastService } from '../../core/ui-state/toast.service';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';
import { ManualEntryDialogComponent } from './manual-entry/manual-entry.dialog';
import { ProductPanelComponent } from './product-panel/product-panel';

@Component({
  selector: 'hn-inventory-page',
  imports: [FormsModule, RouterLink, ...UI, ManualEntryDialogComponent, ProductPanelComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './inventory.page.html',
})
export class InventoryPage {
  private readonly api = inject(InventoryApi);
  private readonly toast = inject(ToastService);
  private readonly i18n = inject(I18nService);
  private readonly session = inject(SessionStore);

  protected readonly canManage = this.session.can('inventory.manage');
  /** Artikel anlegen (manual entry); creating products/offers inside needs more (checked in the wizard and the API). */
  protected readonly entryOpen = signal(false);
  protected readonly tab = signal<'stock' | 'movements'>('stock');
  protected readonly warehouseId = signal('');
  protected readonly stockPage = signal(1);
  protected readonly movementPage = signal(1);
  /** Row last worked on (adjusted or its movements opened); highlighted as the selected row. */
  protected readonly activeRow = signal<string | null>(null);
  protected readonly movementProduct = signal<{ id: string; label: string } | null>(null);
  protected readonly pageSize = 50;

  protected readonly warehouses = createQuery(() => true, () => this.api.warehouses());
  protected readonly stock = createQuery(
    () => ({ warehouseId: this.warehouseId() || null, page: this.stockPage(), pageSize: this.pageSize }),
    (q) => this.api.stock(q),
  );
  protected readonly movements = createQuery(
    () => (this.tab() === 'movements' ? { productId: this.movementProduct()?.id ?? null, page: this.movementPage(), pageSize: this.pageSize } : null),
    (q) => (q ? this.api.movements(q) : of(null)),
  );

  private readonly warehouseNames = computed(() => new Map((this.warehouses.value() ?? []).map((w) => [w.id, w.code])));

  // Adjustment dialog
  protected readonly adjusting = signal<StockRow | null>(null);
  protected adjustment = { delta: 0, reason: '' };
  protected readonly adjustProblem = signal<ApiProblem | null>(null);
  protected readonly adjustBusy = signal(false);

  // Warehouse dialog
  protected readonly warehouseOpen = signal(false);
  protected newWarehouse = { code: '', name: '' };
  protected readonly warehouseProblem = signal<ApiProblem | null>(null);
  protected readonly warehouseBusy = signal(false);

  protected onEntrySaved(): void {
    this.stock.reload();
    if (this.tab() === 'movements') this.movements.reload();
  }

  protected warehouseCode(id: string): string {
    return this.warehouseNames().get(id) ?? id.slice(0, 8);
  }

  protected selectWarehouse(id: string): void {
    this.warehouseId.set(id);
    this.stockPage.set(1);
  }

  /** Tabs keyboard pattern: arrow keys / Home / End move between the two tabs. */
  protected onTabKey(event: KeyboardEvent): void {
    if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
    event.preventDefault();
    const next = event.key === 'Home' ? 'stock' : event.key === 'End' ? 'movements' : this.tab() === 'stock' ? 'movements' : 'stock';
    this.tab.set(next);
    (event.currentTarget as HTMLElement).ownerDocument.getElementById(next === 'stock' ? 'tab-stock' : 'tab-movements')?.focus();
  }

  protected rowKey(row: StockRow): string {
    return row.warehouseId + ':' + row.productId;
  }

  // Product panel (Bilder, Artikeldaten, Preise & Angebot, Bestand) for one stock row
  protected readonly panelRow = signal<StockRow | null>(null);
  /** Bumped after a stock correction so the open panel re-reads its stock and movements. */
  protected readonly panelRefresh = signal(0);

  protected openPanel(row: StockRow): void {
    this.activeRow.set(this.rowKey(row));
    this.panelRow.set(row);
  }

  /** Row click opens the panel, except clicks on the row's own buttons. */
  protected onRowClick(event: MouseEvent, row: StockRow): void {
    if ((event.target as HTMLElement | null)?.closest('button, a, input, select, textarea')) return;
    this.openPanel(row);
  }

  protected onRowKey(event: KeyboardEvent, row: StockRow): void {
    if (event.target !== event.currentTarget) return; // keys inside the row's buttons
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      this.openPanel(row);
    }
  }

  protected onPanelChanged(): void {
    this.stock.reload();
  }

  protected adjustFromPanel(row: StockRow): void {
    this.openAdjust(row);
  }

  protected movementsFromPanel(row: StockRow): void {
    this.panelRow.set(null);
    this.showMovements(row);
  }

  protected showMovements(row: StockRow): void {
    this.activeRow.set(this.rowKey(row));
    this.movementProduct.set({ id: row.productId, label: `${row.brand} ${row.productName}` });
    this.movementPage.set(1);
    this.tab.set('movements');
  }

  protected clearMovementFilter(): void {
    this.movementProduct.set(null);
    this.movementPage.set(1);
  }

  protected openAdjust(row: StockRow): void {
    this.activeRow.set(this.rowKey(row));
    this.adjustment = { delta: 0, reason: '' };
    this.adjustProblem.set(null);
    this.adjusting.set(row);
  }

  protected errors(problem: ApiProblem | null, field: string): string[] {
    return fieldErrors(problem, field);
  }

  protected async submitAdjust(): Promise<void> {
    const row = this.adjusting();
    if (!row) return;
    const delta = Math.trunc(Number(this.adjustment.delta));
    if (!delta || !this.adjustment.reason.trim()) {
      this.adjustProblem.set({ status: 422, title: this.i18n.t('inventory.adjustInvalid'), code: 'client', errors: {} });
      return;
    }
    this.adjustBusy.set(true);
    this.adjustProblem.set(null);
    try {
      await firstValueFrom(
        this.api.adjust({ warehouseId: row.warehouseId, productId: row.productId, delta, reason: this.adjustment.reason.trim() }),
      );
      this.toast.success(this.i18n.t('inventory.adjusted'));
      this.adjusting.set(null);
      this.stock.reload();
      this.panelRefresh.update((n) => n + 1);
    } catch (e) {
      this.adjustProblem.set(toApiProblem(e));
    } finally {
      this.adjustBusy.set(false);
    }
  }

  protected openWarehouse(): void {
    this.newWarehouse = { code: '', name: '' };
    this.warehouseProblem.set(null);
    this.warehouseOpen.set(true);
  }

  protected async submitWarehouse(): Promise<void> {
    this.warehouseBusy.set(true);
    this.warehouseProblem.set(null);
    try {
      await firstValueFrom(this.api.createWarehouse({ code: this.newWarehouse.code.trim(), name: this.newWarehouse.name.trim() }));
      this.toast.success(this.i18n.t('inventory.warehouseCreated'));
      this.warehouseOpen.set(false);
      this.warehouses.reload();
    } catch (e) {
      this.warehouseProblem.set(toApiProblem(e));
    } finally {
      this.warehouseBusy.set(false);
    }
  }
}
