import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { catchError, firstValueFrom, forkJoin, of } from 'rxjs';
import { InventoryApi, PosApi } from '../../core/api/handynet-api';
import { CashSession, PosRegister } from '../../core/api/models';
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
    <div class="mb-5 flex flex-wrap items-end justify-between gap-3">
      <div class="min-w-0">
        <p class="pos-section-label">{{ orgName() }}</p>
        <h1 class="mt-0.5 text-2xl font-extrabold tracking-tight">{{ 'pos.registers.title' | t }}</h1>
        <p class="mt-1 text-sm text-pos-muted">{{ 'pos.registers.subtitle' | t }}</p>
      </div>
      <button type="button" class="pos-soft" [attr.aria-busy]="registers.loading() || null" (click)="reloadAll()">
        @if (registers.loading()) {
          <span class="size-3.5 animate-spin rounded-full border-2 border-current border-r-transparent" aria-hidden="true"></span>
        }
        {{ 'common.refresh' | t }}
      </button>
    </div>

    <hn-banner class="mb-3" [error]="registers.error()" />

    @let list = registers.value() ?? [];
    @if (registers.value()?.length === 0) {
        <div class="pos-card mb-4">
          <hn-empty-state [title]="'pos.registers.emptyTitle' | t" [text]="(canManage() ? 'pos.registers.emptyManage' : 'pos.registers.emptySell') | t" />
        </div>
    }
    @if (list.length || canManage()) {
      <ul class="grid gap-4 sm:grid-cols-2 xl:grid-cols-3" data-testid="pos-registers">
        @for (r of list; track r.id) {
          @let s = sessionFor(r);
          <li class="pos-card flex flex-col p-5 shadow-[0_10px_30px_-22px_rgb(15_30_40/0.45)]" [class.opacity-75]="!r.isActive" [attr.data-register]="r.name">
            <div class="flex items-start gap-3">
              <span class="grid size-11 shrink-0 place-items-center rounded-xl" [class]="r.openSessionId ? 'bg-pos-accent-soft text-pos-accent-text' : 'bg-pos-key text-pos-muted'" aria-hidden="true">
                <svg class="size-6" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round"><path d="M4 10h16v10H4zM7 10V5h10v5M8 14h2M14 14h2M8 17h8" /></svg>
              </span>
              <div class="min-w-0 flex-1">
                <h2 class="truncate text-lg font-bold">{{ r.name }}</h2>
                <div class="mt-1 flex flex-wrap gap-1.5">
                  @if (!r.isActive) {
                    <span class="pos-pill bg-pos-key text-pos-muted">{{ 'Inactive' | tEnum: 'posRegisterStatus' }}</span>
                  }
                  @if (r.openSessionId) {
                    <span class="pos-pill bg-pos-ok-soft text-pos-ok-text"><span class="size-[7px] rounded-full bg-pos-ok" aria-hidden="true"></span>{{ 'Open' | tEnum: 'posSessionStatus' }}</span>
                  } @else {
                    <span class="pos-pill bg-pos-key text-pos-muted"><span class="size-[7px] rounded-full bg-slate-400" aria-hidden="true"></span>{{ 'Closed' | tEnum: 'posSessionStatus' }}</span>
                  }
                </div>
              </div>
            </div>

            <dl class="mt-4 grid grid-cols-2 gap-2 text-sm">
              <div class="min-w-0 rounded-[10px] bg-pos-surface px-3 py-2">
                <dt class="text-xs text-pos-muted">{{ 'pos.registers.warehouse' | t }}</dt>
                <dd class="truncate font-semibold">{{ warehouseName(r.warehouseId) }}</dd>
              </div>
              <div class="min-w-0 rounded-[10px] bg-pos-surface px-3 py-2">
                <dt class="text-xs text-pos-muted">{{ 'pos.registers.nextNumber' | t }}</dt>
                <dd class="font-semibold tabular-nums">{{ r.nextReceiptNumber }}</dd>
              </div>
              @if (s) {
                <div class="col-span-2 rounded-[10px] bg-pos-accent-soft px-3 py-2 text-pos-accent-text" data-testid="pos-register-session">
                  <dt class="sr-only">{{ 'pos.sessions.opened' | t }}</dt>
                  <dd class="text-xs font-semibold">{{ sessionLine(s) }}</dd>
                  <dd class="mt-1 flex items-baseline justify-between gap-2">
                    <span class="text-xs">{{ 'pos.till.sessionReceipts' | t: { count: s.summary.receiptCount } }}</span>
                    <span class="tabular-nums"><span class="sr-only">{{ 'pos.z.totalGross' | t }}: </span><span class="text-lg font-bold">{{ s.summary.totalGrossMinor | money }}</span></span>
                  </dd>
                </div>
              } @else if (!r.openSessionId) {
                <div class="col-span-2 rounded-[10px] border border-dashed border-pos-line px-3 py-2 text-xs text-pos-muted">{{ 'pos.app.noSession' | t }}</div>
              }
            </dl>

            <div class="mt-auto pt-4">
              @if (r.isActive) {
                <a class="w-full text-base" [class]="r.openSessionId ? 'pos-pay-btn justify-center' : 'pos-soft pos-soft-accent min-h-[60px]'" [routerLink]="['/kasse', r.id]" data-testid="pos-open-till">
                  {{ (r.openSessionId ? 'pos.registers.toTill' : 'pos.registers.openSession') | t }}
                </a>
              }
              <div class="mt-2 grid grid-cols-2 gap-2">
                <a class="pos-soft" [routerLink]="['/kasse', r.id, 'belege']">{{ 'pos.subnav.receipts' | t }}</a>
                <a class="pos-soft" [routerLink]="['/kasse', r.id, 'sitzungen']">{{ 'pos.subnav.sessions' | t }}</a>
              </div>
              @if (canManage()) {
                <div class="mt-3 flex flex-wrap gap-1 border-t border-pos-line pt-2">
                  <button type="button" class="min-h-11 rounded-[10px] px-3 text-sm font-semibold text-pos-muted hover:bg-pos-key hover:text-pos-ink" (click)="openRename(r)" data-testid="pos-rename">{{ 'pos.registers.rename' | t }}</button>
                  <button type="button" class="min-h-11 rounded-[10px] px-3 text-sm font-semibold text-pos-muted hover:bg-pos-key hover:text-pos-ink" (click)="openToggle(r)" data-testid="pos-toggle">
                    {{ (r.isActive ? 'pos.registers.deactivate' : 'pos.registers.activate') | t }}
                  </button>
                </div>
              }
            </div>
          </li>
        }

        @if (canManage()) {
          <li class="flex flex-col rounded-xl border-2 border-dashed border-pos-line p-5">
            <section class="flex flex-1 flex-col" aria-labelledby="pos-create-heading">
              <div class="mb-3 flex items-center gap-3">
                <span class="grid size-11 shrink-0 place-items-center rounded-xl bg-pos-key text-pos-muted" aria-hidden="true">
                  <svg class="size-6" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><path d="M12 5v14M5 12h14" /></svg>
                </span>
                <h2 id="pos-create-heading" class="text-lg font-bold">{{ 'pos.registers.createTitle' | t }}</h2>
              </div>
              <hn-banner class="mb-3" [text]="createError()" />
              <form class="flex flex-1 flex-col gap-3" (ngSubmit)="create()" novalidate>
                <hn-field [label]="'common.name' | t" for="pos-new-name" [required]="true" [errors]="nameError()">
                  <input hnInput id="pos-new-name" name="name" autocomplete="off" class="min-h-11" [maxlength]="maxName" [(ngModel)]="newName"
                    [attr.aria-invalid]="!!nameError()" [attr.aria-describedby]="nameError() ? 'pos-new-name-error' : null" data-testid="pos-new-name" />
                </hn-field>
                @if (hasInventory()) {
                  <hn-field [label]="'pos.registers.warehouse' | t" for="pos-new-wh" [hint]="'pos.registers.warehouseHint' | t">
                    <select hnSelect id="pos-new-wh" name="warehouse" class="min-h-11" [(ngModel)]="newWarehouseId" aria-describedby="pos-new-wh-hint">
                      <option value="">{{ 'pos.registers.defaultWarehouse' | t }}</option>
                      @for (w of warehouses.value() ?? []; track w.id) {
                        <option [value]="w.id">{{ w.code }} – {{ w.name }}</option>
                      }
                    </select>
                  </hn-field>
                }
                <button type="submit" class="pos-soft pos-soft-accent mt-auto w-full" [attr.aria-busy]="busy() || null" [disabled]="busy()" data-testid="pos-create">
                  @if (busy()) {
                    <span class="size-3.5 animate-spin rounded-full border-2 border-current border-r-transparent" aria-hidden="true"></span>
                  }
                  {{ 'common.create' | t }}
                </button>
              </form>
            </section>
          </li>
        }
      </ul>
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

  /** Current sessions of the registers with an open session (opening time, receipts, gross so far). */
  private readonly openSessions = createQuery(
    () =>
      (this.registers.value() ?? [])
        .filter((r) => r.openSessionId)
        .map((r) => r.id)
        .join(','),
    (ids) => (ids ? forkJoin(ids.split(',').map((id) => this.api.currentSession(id).pipe(catchError(() => of(null))))) : of([])),
  );
  protected readonly orgName = computed(() => this.session.membership()?.organizationName ?? '');

  protected sessionFor(r: PosRegister): CashSession | null {
    if (!r.openSessionId) return null;
    return this.openSessions.value()?.find((s) => s?.id === r.openSessionId) ?? null;
  }

  protected sessionLine(s: CashSession): string {
    const since = new Intl.DateTimeFormat(this.i18n.locale(), { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' }).format(new Date(s.openedAt));
    const text = this.i18n.t('pos.till.sessionOpen', { since });
    return s.openedByName ? `${text} · ${s.openedByName}` : text;
  }

  protected reloadAll(): void {
    this.registers.reload();
  }

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
