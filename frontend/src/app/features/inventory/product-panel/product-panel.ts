import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom, map, of } from 'rxjs';
import { ApiProblem, fieldErrors, toApiProblem } from '../../../core/api/api-problem';
import { CatalogApi, InventoryApi, OffersApi, PosApi } from '../../../core/api/handynet-api';
import { OFFER_CONDITIONS, OwnOffer, CatalogProductDetail, StockRow, Warehouse } from '../../../core/api/models';
import { SessionStore } from '../../../core/auth/session.store';
import { grossFromNetMinor, minorToInput, parseMoneyToMinor } from '../../../core/format/money';
import { I18nService } from '../../../core/i18n/i18n.service';
import { ToastService, describeProblem } from '../../../core/ui-state/toast.service';
import { createQuery } from '../../../shared/data/query';
import { ImageGalleryComponent } from '../../../shared/product-images/image-gallery';
import { ProductImageManagerComponent } from '../../../shared/product-images/product-image-manager';
import { UI } from '../../../shared/ui';
import { parseModelList } from '../manual-entry/manual-entry.logic';
import {
  FormErrors,
  OfferForm,
  PanelTab,
  ProductForm,
  categoryChangeAllowed,
  latestPurchasePrice,
  nextTab,
  offerCreateRequest,
  offerForm,
  offerStatusChoices,
  offerUpdateRequest,
  panelAccess,
  parsePosPrice,
  pickOffer,
  productDirty,
  productErrorField,
  productForm,
  productRequest,
  stockTotals,
  validateOfferForm,
  validateProductForm,
} from './product-panel.logic';

/**
 * Lager product panel for one stock row: Bilder (manager or read-only gallery), Artikeldaten (editable only for the
 * organization that created the canonical product), Preise & Angebot (own POS price, own marketplace offer, last
 * purchase price) and Bestand (per warehouse, recent movements, Korrigieren). Sections follow the modules and
 * permissions of the selected organization; the API enforces the same rules.
 */
@Component({
  selector: 'hn-product-panel',
  imports: [FormsModule, ...UI, ProductImageManagerComponent, ImageGalleryComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './product-panel.html',
})
export class ProductPanelComponent {
  private readonly catalog = inject(CatalogApi);
  private readonly inventory = inject(InventoryApi);
  private readonly offersApi = inject(OffersApi);
  private readonly pos = inject(PosApi);
  private readonly session = inject(SessionStore);
  private readonly i18n = inject(I18nService);
  private readonly toast = inject(ToastService);

  /** Stock row whose product is shown; null = closed. */
  readonly row = input<StockRow | null>(null);
  readonly warehouses = input<Warehouse[]>([]);
  /** Bumped by the page after a stock correction so the Bestand section re-reads. */
  readonly refresh = input(0);
  readonly closed = output<void>();
  /** Product data or stock changed: the page reloads its table. */
  readonly changed = output<void>();
  readonly adjust = output<StockRow>();
  readonly showMovements = output<StockRow>();

  protected readonly conditions = OFFER_CONDITIONS;
  protected readonly tab = signal<PanelTab>('images');
  protected readonly productId = computed(() => this.row()?.productId ?? null);

  // ---- data
  private readonly detailQuery = createQuery(
    () => this.productId(),
    (id) => (id ? this.catalog.product(id) : of(null)),
  );
  /** Only the loaded product of the current row (the query keeps the previous value while loading). */
  protected readonly product = computed(() => {
    const d = this.detailQuery.value();
    return d && d.id === this.productId() ? d : null;
  });
  protected readonly detailLoading = this.detailQuery.loading;
  protected readonly detailError = this.detailQuery.error;

  protected readonly access = computed(() => panelAccess(this.product(), this.session));
  protected readonly categories = createQuery(
    () => !!this.row(),
    (load) => (load ? this.catalog.categories() : of(null)),
  );

  /** Read-only gallery images (the manager loads its own list). */
  private readonly galleryQuery = createQuery(
    () => (this.product() && !this.access().manageImages ? this.productId() : null),
    (id) => (id ? this.catalog.images(id) : of(null)),
  );
  protected readonly galleryImages = computed(() => this.galleryQuery.value() ?? []);
  protected readonly galleryLoading = this.galleryQuery.loading;

  private readonly stockQuery = createQuery(
    () => (this.productId() ? { productId: this.productId()!, refresh: this.refresh() } : null),
    (q) => (q ? this.inventory.stock({ productId: q.productId, pageSize: 100 }).pipe(map((p) => ({ id: q.productId, items: p.items }))) : of(null)),
  );
  protected readonly stockRows = computed(() => {
    const v = this.stockQuery.value();
    return v && v.id === this.productId() ? v.items : [];
  });
  protected readonly stockLoading = this.stockQuery.loading;
  protected readonly stockError = this.stockQuery.error;
  protected readonly totals = computed(() => stockTotals(this.stockRows()));
  /** The row's warehouse as freshly read (after a correction), else the row the panel was opened with. */
  protected readonly current = computed(() => {
    const r = this.row();
    return this.stockRows().find((s) => s.warehouseId === r?.warehouseId) ?? r;
  });

  private readonly movementQuery = createQuery(
    () => (this.productId() ? { productId: this.productId()!, refresh: this.refresh() } : null),
    (q) => (q ? this.inventory.movements({ productId: q.productId, pageSize: 20 }).pipe(map((p) => ({ id: q.productId, items: p.items }))) : of(null)),
  );
  protected readonly movements = computed(() => {
    const v = this.movementQuery.value();
    return v && v.id === this.productId() ? v.items : [];
  });
  protected readonly movementsLoading = this.movementQuery.loading;
  protected readonly movementsError = this.movementQuery.error;
  protected readonly purchasePrice = computed(() => latestPurchasePrice(this.movements()));

  private readonly posQuery = createQuery(
    () => (this.productId() && this.access().posPrice ? this.productId() : null),
    (id) => (id ? this.pos.price(id) : of(null)),
  );
  protected readonly posPrice = computed(() => {
    const p = this.posQuery.value();
    return p && p.productId === this.productId() ? p : null;
  });
  protected readonly posLoading = this.posQuery.loading;
  protected readonly posError = this.posQuery.error;

  private readonly offersQuery = createQuery(
    () => (this.productId() && this.access().offer ? this.productId() : null),
    (id) => (id ? this.offersApi.list({ productId: id, pageSize: 50 }).pipe(map((p) => ({ id, items: p.items }))) : of(null)),
  );
  protected readonly offers = computed(() => {
    const v = this.offersQuery.value();
    return v && v.id === this.productId() ? v.items : null;
  });
  protected readonly offersLoading = this.offersQuery.loading;
  protected readonly offersError = this.offersQuery.error;
  /** Offer chosen in the select (several offers of one product, e.g. other warehouses); default from pickOffer(). */
  protected readonly chosenOfferId = signal<string | null>(null);
  protected readonly offer = computed<OwnOffer | null>(() => {
    const list = this.offers();
    if (!list) return null;
    const chosen = this.chosenOfferId();
    return list.find((o) => o.id === chosen) ?? pickOffer(list, this.row()?.warehouseId ?? '');
  });
  /** Devices with IMEI are sold one by one at the till, not through marketplace offers (like the entry wizard). */
  protected readonly offerUnsupported = computed(() => {
    const p = this.product();
    return !!p && !!this.categories.value()?.imeiRequired.includes(p.category);
  });

  // ---- forms
  protected form: ProductForm = productForm(EMPTY);
  protected readonly formRevision = signal(0);
  protected readonly productClientErrors = signal<FormErrors>({});
  protected readonly productProblem = signal<ApiProblem | null>(null);
  protected readonly productBusy = signal(false);

  protected offerDraft: OfferForm = offerForm(null, EMPTY);
  protected readonly offerClientErrors = signal<FormErrors>({});
  protected readonly offerProblem = signal<ApiProblem | null>(null);
  protected readonly offerBusy = signal(false);
  protected readonly offerGross = signal<number | null>(null);

  protected posInput = '';
  protected readonly posErrorText = signal<string | null>(null);
  protected readonly posBusy = signal(false);

  constructor() {
    // New row → first tab, fresh selections.
    effect(() => {
      this.productId();
      untracked(() => {
        this.tab.set('images');
        this.discardPrompt.set(false);
        this.chosenOfferId.set(null);
        this.productProblem.set(null);
        this.offerProblem.set(null);
        this.posErrorText.set(null);
      });
    });
    // Loaded/saved product → form (keyed by id + version, so a save resets it to the stored values).
    effect(() => {
      const p = this.product();
      if (!p) return;
      untracked(() => {
        this.form = productForm(p);
        this.productClientErrors.set({});
        this.formRevision.update((n) => n + 1);
      });
    });
    effect(() => {
      const p = this.product();
      const o = this.offer();
      if (!p) return;
      untracked(() => {
        this.offerDraft = offerForm(o, p);
        this.offerClientErrors.set({});
        this.updateOfferGross();
      });
    });
    effect(() => {
      const price = this.posPrice();
      untracked(() => (this.posInput = price?.posPriceMinor ? minorToInput(price.posPriceMinor) : ''));
    });
  }

  protected title(): string {
    const r = this.row();
    return r ? `${r.brand} ${r.productName}` : '';
  }

  protected warehouseLabel(id: string): string {
    const w = this.warehouses().find((x) => x.id === id);
    return w ? `${w.code} – ${w.name}` : id.slice(0, 8);
  }

  // ---- tabs
  protected selectTab(tab: PanelTab): void {
    this.tab.set(tab);
  }

  protected onTabKey(event: KeyboardEvent): void {
    const el = event.currentTarget as HTMLElement;
    const rtl = el.ownerDocument.documentElement.dir === 'rtl';
    const next = nextTab(this.access().tabs, this.tab(), event.key, rtl);
    if (!next) return;
    event.preventDefault();
    this.tab.set(next);
    el.ownerDocument.getElementById(`panel-tab-${next}`)?.focus();
  }

  protected tabLabel(tab: PanelTab): string {
    if (tab === 'prices') return this.i18n.t(this.access().offer ? 'inventory.panel.tabs.pricesOffer' : 'inventory.panel.tabs.prices');
    return this.i18n.t(`inventory.panel.tabs.${tab}`);
  }

  /** Closing with unsaved product data asks first (inline, Esc/✕ only emit; the dialog uses guardClose). */
  protected readonly discardPrompt = signal(false);

  protected close(): void {
    if (this.access().editProduct && this.isProductDirty()) {
      this.discardPrompt.set(true);
      this.tab.set('data');
      return;
    }
    this.discardPrompt.set(false);
    this.closed.emit();
  }

  protected discardAndClose(): void {
    this.discardPrompt.set(false);
    this.closed.emit();
  }

  // ---- product data
  protected categoryAllowed(category: string): boolean {
    const p = this.product();
    return !p || categoryChangeAllowed(p.category, category, this.categories.value());
  }

  protected modelCount(): number {
    return parseModelList(this.form.compatibleModels).length;
  }

  protected productError(field: string): string[] {
    const client = this.productClientErrors()[field];
    if (client) return [this.i18n.t(client, { max: 100 })];
    const problem = this.productProblem();
    if (!problem) return [];
    if (productErrorField(problem.code) === field) return [describeProblem(this.i18n, problem)];
    return fieldErrors(problem, field);
  }

  /** Banner for problems that do not belong to one field (concurrency, permission, network). */
  protected readonly productBanner = computed(() => {
    const problem = this.productProblem();
    if (!problem || productErrorField(problem.code)) return null;
    if (problem.status === 422 && Object.keys(problem.errors).length > 0) return null;
    return problem;
  });

  protected isProductDirty(): boolean {
    const p = this.product();
    return !!p && productDirty(this.form, p);
  }

  protected resetProduct(): void {
    const p = this.product();
    if (!p) return;
    this.form = productForm(p);
    this.productClientErrors.set({});
    this.productProblem.set(null);
    this.formRevision.update((n) => n + 1);
  }

  protected reloadProduct(): void {
    this.productProblem.set(null);
    this.detailQuery.reload();
  }

  protected async saveProduct(): Promise<void> {
    const p = this.product();
    if (!p || !this.access().editProduct) return;
    const errors = validateProductForm(this.form, p, this.categories.value());
    this.productClientErrors.set(errors);
    this.productProblem.set(null);
    if (Object.keys(errors).length) return;
    this.productBusy.set(true);
    try {
      const updated = await firstValueFrom(this.catalog.updateProduct(p.id, productRequest(this.form, p.version)));
      this.detailQuery.set(updated);
      this.toast.success(this.i18n.t('inventory.panel.productSaved'));
      this.changed.emit();
    } catch (e) {
      this.productProblem.set(toApiProblem(e));
    } finally {
      this.productBusy.set(false);
    }
  }

  // ---- POS price
  protected async savePosPrice(clear = false): Promise<void> {
    const id = this.productId();
    if (!id || this.access().posPrice !== 'edit') return;
    const minor = clear ? null : parsePosPrice(this.posInput);
    if (!clear && minor === null) {
      this.posErrorText.set(this.i18n.t('pos.price.invalid'));
      return;
    }
    this.posErrorText.set(null);
    this.posBusy.set(true);
    try {
      const saved = await firstValueFrom(this.pos.setPrice(id, minor));
      this.posQuery.set(saved);
      this.toast.success(this.i18n.t(clear ? 'pos.price.cleared' : 'pos.price.saved'));
    } catch (e) {
      this.posErrorText.set(describeProblem(this.i18n, toApiProblem(e)));
    } finally {
      this.posBusy.set(false);
    }
  }

  // ---- offer
  protected chooseOffer(id: string): void {
    this.chosenOfferId.set(id);
    this.offerProblem.set(null);
  }

  protected updateOfferGross(): void {
    const net = parseMoneyToMinor(this.offerDraft.price);
    this.offerGross.set(net !== null && net > 0 ? grossFromNetMinor(net) : null);
  }

  protected statusChoices(o: OwnOffer): string[] {
    return offerStatusChoices(o);
  }

  protected offerError(field: string): string[] {
    const client = this.offerClientErrors()[field];
    if (client) return [this.i18n.t(client)];
    const problem = this.offerProblem();
    if (!problem) return [];
    const fieldOf: Record<string, string> = { 'offer.price': 'price', 'offer.sku_taken': 'supplierSku', 'offer.sku_required': 'supplierSku',
      'offer.min_quantity': 'minOrderQuantity', 'offer.delivery_days': 'deliveryDays' };
    if (problem.code && fieldOf[problem.code] === field) return [describeProblem(this.i18n, problem)];
    return fieldErrors(problem, field);
  }

  protected readonly offerBanner = computed(() => {
    const problem = this.offerProblem();
    if (!problem) return null;
    if (['offer.price', 'offer.sku_taken', 'offer.sku_required', 'offer.min_quantity', 'offer.delivery_days'].includes(problem.code ?? '')) return null;
    if (problem.status === 422 && Object.keys(problem.errors).length > 0) return null;
    return problem;
  });

  protected async saveOffer(): Promise<void> {
    const p = this.product();
    const row = this.row();
    if (!p || !row || this.access().offer !== 'edit') return;
    const current = this.offer();
    const errors = validateOfferForm(this.offerDraft, !current);
    this.offerClientErrors.set(errors);
    this.offerProblem.set(null);
    if (Object.keys(errors).length) return;
    this.offerBusy.set(true);
    try {
      const saved = current
        ? await firstValueFrom(this.offersApi.update(current.id, offerUpdateRequest(this.offerDraft, current)))
        : await firstValueFrom(this.offersApi.create(offerCreateRequest(this.offerDraft, p.id, row.warehouseId)));
      this.chosenOfferId.set(saved.id);
      this.offersQuery.reload();
      if (this.access().posPrice) this.posQuery.reload(); // the till price may come from the offer
      this.toast.success(this.i18n.t(current ? 'offers.saved' : 'offers.created'));
    } catch (e) {
      this.offerProblem.set(toApiProblem(e));
    } finally {
      this.offerBusy.set(false);
    }
  }

  // ---- stock
  protected adjustRow(r: StockRow): void {
    this.adjust.emit(r);
  }

  protected openMovements(): void {
    const r = this.row();
    if (r) this.showMovements.emit(r);
  }

}

const EMPTY: CatalogProductDetail = {
  id: '',
  brand: '',
  name: '',
  category: '',
  quality: 'NotApplicable',
  mpn: null,
  ean: null,
  modelName: null,
  compatibleModels: [],
  createdByCurrentOrganization: false,
  canEdit: false,
  version: 0,
};
