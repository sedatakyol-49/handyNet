import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { firstValueFrom, of } from 'rxjs';
import { InventoryApi, PosApi } from '../../core/api/handynet-api';
import { PosRegister } from '../../core/api/models';
import { SessionStore } from '../../core/auth/session.store';
import { I18nService } from '../../core/i18n/i18n.service';
import { ToastService } from '../../core/ui-state/toast.service';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';
import { posErrorText } from './pos-error';
import { PosContext } from './pos-context';
import { POS_LIMITS } from './pos.logic';

type Dialog = { kind: 'rename' | 'toggle'; register: PosRegister } | null;

/** Kasse: register selection; register management (create, rename, (de)activate) with `pos.manage`. */
@Component({
  selector: 'hn-pos-registers-page',
  host: { class: 'pos-page block' },
  imports: [FormsModule, RouterLink, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-page-header [title]="'pos.registers.title' | t" [description]="'pos.registers.subtitle' | t">
      <button hnButton type="button" class="min-h-11" [loading]="registers.loading()" (click)="registers.reload()">{{ 'common.refresh' | t }}</button>
    </hn-page-header>

    <hn-banner class="mb-3" [error]="registers.error()" />

    @if (registers.value(); as list) {
      @if (list.length === 0) {
        <div class="hn-card mb-4">
          <hn-empty-state [title]="'pos.registers.emptyTitle' | t" [text]="(canManage() ? 'pos.registers.emptyManage' : 'pos.registers.emptySell') | t" />
        </div>
      } @else {
        <ul class="mb-6 grid gap-3 sm:grid-cols-2 xl:grid-cols-3" data-testid="pos-registers">
          @for (r of list; track r.id) {
            <li class="hn-card flex flex-col p-5 shadow-xs" [attr.data-register]="r.name">
              <div class="flex items-start justify-between gap-2">
                <h2 class="text-lg">{{ r.name }}</h2>
                <span class="flex gap-1">
                  @if (!r.isActive) {
                    <hn-status-badge status="Inactive" kind="posRegisterStatus" tone="neutral" />
                  }
                  <hn-status-badge [status]="r.openSessionId ? 'Open' : 'Closed'" kind="posSessionStatus" [tone]="r.openSessionId ? 'success' : 'neutral'" />
                </span>
              </div>
              <dl class="mt-2 grid grid-cols-[auto_1fr] gap-x-3 gap-y-0.5 text-sm text-slate-600">
                <dt>{{ 'pos.registers.warehouse' | t }}</dt>
                <dd>{{ warehouseName(r.warehouseId) }}</dd>
                <dt>{{ 'pos.registers.nextNumber' | t }}</dt>
                <dd class="tabular-nums">{{ r.nextReceiptNumber }}</dd>
              </dl>
              @if (r.isActive) {
                <a hnButton variant="primary" class="mt-4 min-h-14 w-full text-lg" [routerLink]="['/kasse', r.id]" data-testid="pos-open-till">
                  {{ (r.openSessionId ? 'pos.registers.toTill' : 'pos.registers.openSession') | t }}
                </a>
              }
              <div class="mt-2 grid grid-cols-2 gap-2">
                <a hnButton class="min-h-11" [routerLink]="['/kasse', r.id, 'belege']">{{ 'pos.subnav.receipts' | t }}</a>
                <a hnButton class="min-h-11" [routerLink]="['/kasse', r.id, 'sitzungen']">{{ 'pos.subnav.sessions' | t }}</a>
              </div>
              @if (canManage()) {
                <div class="mt-3 flex flex-wrap gap-2 border-t border-slate-100 pt-3">
                  <button hnButton variant="ghost" type="button" class="min-h-11" (click)="openRename(r)" data-testid="pos-rename">{{ 'pos.registers.rename' | t }}</button>
                  <button hnButton variant="ghost" type="button" class="min-h-11" (click)="openToggle(r)" data-testid="pos-toggle">
                    {{ (r.isActive ? 'pos.registers.deactivate' : 'pos.registers.activate') | t }}
                  </button>
                </div>
              }
            </li>
          }
        </ul>
      }
    }

    @if (canManage()) {
      <section class="hn-card max-w-2xl p-4" aria-labelledby="pos-create-heading">
        <h2 id="pos-create-heading" class="mb-3 text-sm">{{ 'pos.registers.createTitle' | t }}</h2>
        <hn-banner class="mb-3" [text]="createError()" />
        <form class="grid gap-3 sm:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_auto] sm:items-start" (ngSubmit)="create()" novalidate>
          <hn-field [label]="'common.name' | t" for="pos-new-name" [required]="true" [errors]="nameError()">
            <input hnInput id="pos-new-name" name="name" autocomplete="off" [maxlength]="maxName" [(ngModel)]="newName"
              [attr.aria-invalid]="!!nameError()" [attr.aria-describedby]="nameError() ? 'pos-new-name-error' : null" data-testid="pos-new-name" />
          </hn-field>
          @if (hasInventory()) {
            <hn-field [label]="'pos.registers.warehouse' | t" for="pos-new-wh" [hint]="'pos.registers.warehouseHint' | t">
              <select hnSelect id="pos-new-wh" name="warehouse" [(ngModel)]="newWarehouseId" aria-describedby="pos-new-wh-hint">
                <option value="">{{ 'pos.registers.defaultWarehouse' | t }}</option>
                @for (w of warehouses.value() ?? []; track w.id) {
                  <option [value]="w.id">{{ w.code }} – {{ w.name }}</option>
                }
              </select>
            </hn-field>
          } @else {
            <span></span>
          }
          <button hnButton variant="primary" type="submit" class="min-h-11 sm:mt-5" [loading]="busy()" [disabled]="busy()" data-testid="pos-create">{{ 'common.create' | t }}</button>
        </form>
      </section>
    }

    <hn-dialog [title]="(dialog()?.kind === 'rename' ? 'pos.registers.renameTitle' : 'pos.registers.toggleTitle') | t" [open]="!!dialog()" (closed)="closeDialog()">
      @if (dialog(); as d) {
        <hn-banner class="mb-3" [text]="dialogError()" />
        @if (d.kind === 'rename') {
          <form (ngSubmit)="rename(d.register)" novalidate>
            <hn-field [label]="'common.name' | t" for="pos-rename-name" [required]="true">
              <input hnInput id="pos-rename-name" name="rename" autocomplete="off" [maxlength]="maxName" [(ngModel)]="renameValue" data-testid="pos-rename-name" />
            </hn-field>
            <div class="mt-4 flex justify-end gap-2">
              <button hnButton class="min-h-11" type="button" (click)="closeDialog()">{{ 'common.cancel' | t }}</button>
              <button hnButton class="min-h-11" variant="primary" type="submit" [loading]="busy()" [disabled]="busy() || !renameValue.trim()" data-testid="pos-rename-save">{{ 'common.save' | t }}</button>
            </div>
          </form>
        } @else {
          <p class="text-sm">
            {{ (d.register.isActive ? 'pos.registers.deactivateText' : 'pos.registers.activateText') | t: { name: d.register.name } }}
          </p>
          <div class="mt-4 flex justify-end gap-2">
            <button hnButton class="min-h-11" type="button" (click)="closeDialog()">{{ 'common.cancel' | t }}</button>
            <button hnButton class="min-h-11" [variant]="d.register.isActive ? 'danger' : 'primary'" type="button" [loading]="busy()" [disabled]="busy()" (click)="toggle(d.register)" data-testid="pos-toggle-confirm">
              {{ (d.register.isActive ? 'pos.registers.deactivate' : 'pos.registers.activate') | t }}
            </button>
          </div>
        }
      }
    </hn-dialog>
  `,
})
export class PosRegistersPage {
  private readonly api = inject(PosApi);
  private readonly inventoryApi = inject(InventoryApi);
  private readonly session = inject(SessionStore);
  private readonly i18n = inject(I18nService);
  private readonly toast = inject(ToastService);
  private readonly ctx = inject(PosContext);

  constructor() {
    // Register selection: no register / session in the top bar.
    this.ctx.setRegister(null, null);
    this.ctx.session.set(undefined);
  }

  protected readonly maxName = POS_LIMITS.registerNameLength;
  protected readonly canManage = computed(() => this.session.can('pos.manage'));
  protected readonly hasInventory = computed(() => this.session.hasModule('Inventory'));

  protected readonly registers = createQuery(
    () => true,
    () => this.api.registers(),
  );
  protected readonly warehouses = createQuery(
    () => this.hasInventory(),
    (enabled) => (enabled ? this.inventoryApi.warehouses() : of([])),
  );

  protected newName = '';
  protected newWarehouseId = '';
  protected renameValue = '';
  protected readonly busy = signal(false);
  protected readonly nameError = signal<string | null>(null);
  protected readonly createError = signal<string | null>(null);
  protected readonly dialog = signal<Dialog>(null);
  protected readonly dialogError = signal<string | null>(null);

  protected warehouseName(id: string): string {
    const w = this.warehouses.value()?.find((x) => x.id === id);
    return w ? `${w.code} – ${w.name}` : '–';
  }

  protected async create(): Promise<void> {
    const name = this.newName.trim();
    this.nameError.set(name ? null : this.i18n.t('pos.registers.nameRequired'));
    this.createError.set(null);
    if (!name) return;
    this.busy.set(true);
    try {
      await firstValueFrom(this.api.createRegister({ name, warehouseId: this.newWarehouseId || null }));
      this.toast.success(this.i18n.t('pos.registers.created', { name }));
      this.newName = '';
      this.registers.reload();
    } catch (e) {
      this.createError.set(posErrorText(this.i18n, e));
    } finally {
      this.busy.set(false);
    }
  }

  protected openRename(r: PosRegister): void {
    this.renameValue = r.name;
    this.dialogError.set(null);
    this.dialog.set({ kind: 'rename', register: r });
  }

  protected openToggle(r: PosRegister): void {
    this.dialogError.set(null);
    this.dialog.set({ kind: 'toggle', register: r });
  }

  protected closeDialog(): void {
    if (!this.busy()) this.dialog.set(null);
  }

  protected rename(r: PosRegister): Promise<void> {
    return this.update(r, { name: this.renameValue.trim(), isActive: r.isActive });
  }

  protected toggle(r: PosRegister): Promise<void> {
    return this.update(r, { name: r.name, isActive: !r.isActive });
  }

  private async update(r: PosRegister, body: { name: string; isActive: boolean }): Promise<void> {
    if (!body.name) return;
    this.busy.set(true);
    this.dialogError.set(null);
    try {
      await firstValueFrom(this.api.updateRegister(r.id, body));
      this.toast.success(this.i18n.t('pos.registers.saved'));
      this.dialog.set(null);
      this.registers.reload();
    } catch (e) {
      this.dialogError.set(posErrorText(this.i18n, e));
    } finally {
      this.busy.set(false);
    }
  }
}
