import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom, of } from 'rxjs';
import { ApiProblem, fieldErrors, toApiProblem } from '../../core/api/api-problem';
import { InventoryApi } from '../../core/api/handynet-api';
import { StockRow } from '../../core/api/models';
import { SessionStore } from '../../core/auth/session.store';
import { I18nService } from '../../core/i18n/i18n.service';
import { ToastService } from '../../core/ui-state/toast.service';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';

@Component({
  selector: 'hn-inventory-page',
  imports: [FormsModule, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './inventory.page.html',
})
export class InventoryPage {
  private readonly api = inject(InventoryApi);
  private readonly toast = inject(ToastService);
  private readonly i18n = inject(I18nService);
  private readonly session = inject(SessionStore);

  protected readonly canManage = this.session.can('inventory.manage');
  protected readonly tab = signal<'stock' | 'movements'>('stock');
  protected readonly warehouseId = signal('');
  protected readonly stockPage = signal(1);
  protected readonly movementPage = signal(1);
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

  protected warehouseCode(id: string): string {
    return this.warehouseNames().get(id) ?? id.slice(0, 8);
  }

  protected selectWarehouse(id: string): void {
    this.warehouseId.set(id);
    this.stockPage.set(1);
  }

  protected showMovements(row: StockRow): void {
    this.movementProduct.set({ id: row.productId, label: `${row.brand} ${row.productName}` });
    this.movementPage.set(1);
    this.tab.set('movements');
  }

  protected clearMovementFilter(): void {
    this.movementProduct.set(null);
    this.movementPage.set(1);
  }

  protected openAdjust(row: StockRow): void {
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
