import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { Location } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { firstValueFrom, of } from 'rxjs';
import { ImportsApi, InventoryApi } from '../../core/api/handynet-api';
import { ImportBatch, ImportOptions, ImportRowStatus, OFFER_CONDITIONS } from '../../core/api/models';
import { SessionStore } from '../../core/auth/session.store';
import { I18nService } from '../../core/i18n/i18n.service';
import { ToastService } from '../../core/ui-state/toast.service';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';

const MAX_BYTES = 10 * 1024 * 1024;

@Component({
  selector: 'hn-import-wizard-page',
  imports: [FormsModule, RouterLink, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './import-wizard.page.html',
})
export class ImportWizardPage {
  private readonly api = inject(ImportsApi);
  private readonly inventory = inject(InventoryApi);
  private readonly toast = inject(ToastService);
  private readonly i18n = inject(I18nService);
  private readonly location = inject(Location);
  protected readonly session = inject(SessionStore);

  /** Route parameter when an existing import is opened from the history. */
  readonly id = input<string | undefined>(undefined);

  protected readonly conditions = OFFER_CONDITIONS;
  protected readonly file = signal<File | null>(null);
  protected readonly fileError = signal<string | null>(null);
  protected options: ImportOptions = { warehouseId: null, publishOffers: true, defaultDeliveryDays: 2, defaultCondition: 'New' };

  protected readonly batch = signal<ImportBatch | null>(null);
  protected readonly busy = signal<'upload' | 'commit' | 'discard' | 'load' | null>(null);
  protected readonly error = signal<unknown>(null);

  protected readonly rowStatus = signal<ImportRowStatus | null>(null);
  protected readonly rowPage = signal(1);
  protected readonly rowPageSize = 50;

  protected readonly step = computed(() => (this.batch() ? (this.batch()!.status === 'Previewed' ? 2 : 3) : 1));
  protected readonly editable = computed(() => this.batch()?.status === 'Previewed');

  protected readonly fields = createQuery(() => true, () => this.api.fields());
  protected readonly warehouses = createQuery(
    () => this.session.hasModule('Inventory'),
    (load) => (load ? this.inventory.warehouses() : of([])),
  );
  protected readonly rows = createQuery(
    () => {
      const b = this.batch();
      return b ? { id: b.id, status: this.rowStatus(), page: this.rowPage() } : null;
    },
    (q) => (q ? this.api.rows(q.id, q.status, q.page, this.rowPageSize) : of(null)),
  );

  /** Editable column mapping: target field → source column ('' = not mapped). */
  protected mapping: Record<string, string> = {};
  protected readonly showMapping = signal(false);

  constructor() {
    effect(() => {
      const id = this.id();
      if (id) untracked(() => void this.load(id));
    });
  }

  protected isRequired(field: string): boolean {
    return (this.fields.value()?.required ?? []).includes(field);
  }

  protected onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const f = input.files?.[0] ?? null;
    this.fileError.set(null);
    if (f && !/\.(csv|xlsx)$/i.test(f.name)) {
      this.fileError.set(this.i18n.t('imports.fileType'));
      this.file.set(null);
      return;
    }
    if (f && f.size > MAX_BYTES) {
      this.fileError.set(this.i18n.t('imports.fileTooLarge'));
      this.file.set(null);
      return;
    }
    this.file.set(f);
  }

  /** Step 1 → 2: upload with server-side mapping detection. */
  protected async upload(): Promise<void> {
    const f = this.file();
    if (!f) {
      this.fileError.set(this.i18n.t('imports.fileRequired'));
      return;
    }
    await this.send(f, null);
  }

  /** Re-runs the dry run with the adjusted column mapping (the previous preview is discarded). */
  protected async remap(): Promise<void> {
    const f = this.file();
    const previous = this.batch();
    if (!f) {
      this.fileError.set(this.i18n.t('imports.reselectFile'));
      return;
    }
    const mapping = Object.fromEntries(Object.entries(this.mapping).filter(([, column]) => !!column));
    const ok = await this.send(f, mapping);
    if (ok && previous && previous.status === 'Previewed') {
      await firstValueFrom(this.api.discard(previous.id)).catch(() => undefined);
    }
  }

  protected async commit(): Promise<void> {
    const b = this.batch();
    if (!b) return;
    this.busy.set('commit');
    this.error.set(null);
    try {
      const result = await firstValueFrom(this.api.commit(b.id));
      this.setBatch(result);
      this.toast.success(this.i18n.t('imports.committed'));
    } catch (e) {
      this.error.set(e);
    } finally {
      this.busy.set(null);
    }
  }

  protected async discard(): Promise<void> {
    const b = this.batch();
    if (!b) return;
    this.busy.set('discard');
    this.error.set(null);
    try {
      this.setBatch(await firstValueFrom(this.api.discard(b.id)));
      this.toast.info(this.i18n.t('imports.discarded'));
    } catch (e) {
      this.error.set(e);
    } finally {
      this.busy.set(null);
    }
  }

  protected startOver(): void {
    this.batch.set(null);
    this.file.set(null);
    this.error.set(null);
    this.location.replaceState('/app/importe/neu');
  }

  protected filterRows(status: ImportRowStatus | null): void {
    this.rowStatus.set(status);
    this.rowPage.set(1);
  }

  private async send(f: File, mapping: Record<string, string> | null): Promise<boolean> {
    this.busy.set('upload');
    this.error.set(null);
    try {
      const options: ImportOptions = { ...this.options, warehouseId: this.options.warehouseId || null };
      const result = await firstValueFrom(this.api.upload(f, mapping, options));
      this.setBatch(result);
      this.location.replaceState(`/app/importe/${result.id}`);
      return true;
    } catch (e) {
      this.error.set(e);
      return false;
    } finally {
      this.busy.set(null);
    }
  }

  private async load(id: string): Promise<void> {
    this.busy.set('load');
    try {
      this.setBatch(await firstValueFrom(this.api.get(id)));
    } catch (e) {
      this.error.set(e);
    } finally {
      this.busy.set(null);
    }
  }

  private setBatch(b: ImportBatch): void {
    this.batch.set(b);
    this.mapping = { ...b.mapping };
    this.options = { ...this.options, warehouseId: b.warehouseId, publishOffers: b.publishOffers };
    this.rowPage.set(1);
    // Default to the problems when there are any.
    this.rowStatus.set(b.summary.errorRows > 0 ? 'Error' : b.summary.conflictRows > 0 ? 'Conflict' : null);
    this.showMapping.set(b.status === 'Previewed' && b.summary.validRows === 0);
  }
}
