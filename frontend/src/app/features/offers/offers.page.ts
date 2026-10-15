import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom, of } from 'rxjs';
import { ApiProblem, fieldErrors, toApiProblem } from '../../core/api/api-problem';
import { CatalogApi, InventoryApi, OffersApi } from '../../core/api/handynet-api';
import { CatalogProduct, OFFER_CONDITIONS, OfferCondition, OwnOffer, UpdateOfferRequest } from '../../core/api/models';
import { SessionStore } from '../../core/auth/session.store';
import { minorToDecimal, minorToInput, parseMoneyToMinor } from '../../core/format/money';
import { I18nService } from '../../core/i18n/i18n.service';
import { ToastService } from '../../core/ui-state/toast.service';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';

interface EditDraft {
  price: string;
  minOrderQuantity: number;
  deliveryDays: number;
  condition: OfferCondition;
  status: 'Published' | 'Paused' | 'Archived' | '';
}

interface CreateDraft {
  product: CatalogProduct | null;
  supplierSku: string;
  price: string;
  minOrderQuantity: number;
  deliveryDays: number;
  condition: OfferCondition;
  warehouseId: string;
  publish: boolean;
}

@Component({
  selector: 'hn-offers-page',
  imports: [FormsModule, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './offers.page.html',
})
export class OffersPage {
  private readonly api = inject(OffersApi);
  private readonly catalog = inject(CatalogApi);
  private readonly inventory = inject(InventoryApi);
  private readonly toast = inject(ToastService);
  private readonly i18n = inject(I18nService);
  protected readonly session = inject(SessionStore);

  protected readonly conditions = OFFER_CONDITIONS;
  protected readonly statuses = ['Draft', 'Published', 'Paused', 'Archived'];
  protected readonly search = signal('');
  protected readonly status = signal('');
  protected readonly page = signal(1);
  protected readonly pageSize = 50;
  protected readonly canManage = this.session.can('offers.manage');

  protected readonly offers = createQuery(
    () => ({ search: this.search(), status: this.status(), page: this.page(), pageSize: this.pageSize }),
    (q) => this.api.list(q),
  );

  // Inline editing
  protected readonly editingId = signal<string | null>(null);
  protected draft: EditDraft = { price: '', minOrderQuantity: 1, deliveryDays: 2, condition: 'New', status: '' };
  protected readonly saving = signal(false);
  protected readonly editProblem = signal<ApiProblem | null>(null);

  // Create dialog
  protected readonly createOpen = signal(false);
  protected readonly productSearch = signal('');
  protected readonly products = createQuery(
    () => (this.createOpen() ? this.productSearch() : null),
    (q) => (q === null ? of(null) : this.catalog.products({ search: q, pageSize: 20 })),
  );
  protected readonly warehouses = createQuery(
    () => this.createOpen() && this.session.hasModule('Inventory'),
    (load) => (load ? this.inventory.warehouses() : of([])),
  );
  protected create: CreateDraft = this.emptyCreate();
  protected readonly createProblem = signal<ApiProblem | null>(null);
  protected readonly creating = signal(false);

  protected applySearch(value: string): void {
    this.search.set(value.trim());
    this.page.set(1);
  }

  protected startEdit(o: OwnOffer): void {
    this.editProblem.set(null);
    this.draft = {
      price: minorToInput(o.priceMinor),
      minOrderQuantity: o.minOrderQuantity,
      deliveryDays: o.deliveryDays,
      condition: o.condition,
      status: o.status === 'Draft' ? '' : o.status,
    };
    this.editingId.set(o.id);
  }

  protected cancelEdit(): void {
    this.editingId.set(null);
    this.editProblem.set(null);
  }

  protected async saveEdit(o: OwnOffer): Promise<void> {
    const priceMinor = parseMoneyToMinor(this.draft.price);
    if (priceMinor === null || priceMinor <= 0) {
      this.editProblem.set({ status: 422, title: this.i18n.t('validation.price'), code: 'client', errors: { price: [this.i18n.t('validation.price')] } });
      return;
    }
    const body: UpdateOfferRequest = {
      price: minorToDecimal(priceMinor),
      minOrderQuantity: Number(this.draft.minOrderQuantity),
      deliveryDays: Number(this.draft.deliveryDays),
      condition: this.draft.condition,
      status: this.draft.status === '' || this.draft.status === o.status ? null : this.draft.status,
    };
    this.saving.set(true);
    this.editProblem.set(null);
    try {
      await firstValueFrom(this.api.update(o.id, body));
      this.toast.success(this.i18n.t('offers.saved'));
      this.editingId.set(null);
      this.offers.reload();
    } catch (e) {
      this.editProblem.set(toApiProblem(e));
    } finally {
      this.saving.set(false);
    }
  }

  protected fieldError(problem: ApiProblem | null, field: string): string[] {
    return fieldErrors(problem, field);
  }

  private productDebounce: ReturnType<typeof setTimeout> | undefined;

  protected onProductSearch(value: string): void {
    clearTimeout(this.productDebounce);
    this.productDebounce = setTimeout(() => this.productSearch.set(value.trim()), 300);
  }

  protected openCreate(): void {
    this.create = this.emptyCreate();
    this.createProblem.set(null);
    this.productSearch.set('');
    this.createOpen.set(true);
  }

  protected async submitCreate(): Promise<void> {
    const c = this.create;
    const priceMinor = parseMoneyToMinor(c.price);
    if (!c.product || !c.supplierSku.trim() || priceMinor === null || priceMinor <= 0) {
      this.createProblem.set({ status: 422, title: this.i18n.t('offers.createInvalid'), code: 'client', errors: {} });
      return;
    }
    this.creating.set(true);
    this.createProblem.set(null);
    try {
      await firstValueFrom(
        this.api.create({
          productId: c.product.id,
          warehouseId: c.warehouseId || null,
          supplierSku: c.supplierSku.trim(),
          price: minorToDecimal(priceMinor),
          minOrderQuantity: Number(c.minOrderQuantity),
          deliveryDays: Number(c.deliveryDays),
          condition: c.condition,
          publish: c.publish,
        }),
      );
      this.toast.success(this.i18n.t('offers.created'));
      this.createOpen.set(false);
      this.offers.reload();
    } catch (e) {
      this.createProblem.set(toApiProblem(e));
    } finally {
      this.creating.set(false);
    }
  }

  private emptyCreate(): CreateDraft {
    return { product: null, supplierSku: '', price: '', minOrderQuantity: 1, deliveryDays: 2, condition: 'New', warehouseId: '', publish: true };
  }
}
