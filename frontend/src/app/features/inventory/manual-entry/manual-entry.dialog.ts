import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, computed, effect, inject, input, output, signal, untracked, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom, of } from 'rxjs';
import { ApiProblem, fieldErrors, toApiProblem } from '../../../core/api/api-problem';
import { CatalogApi, InventoryApi } from '../../../core/api/handynet-api';
import { LookupMatch, ManualEntryResult, OFFER_CONDITIONS } from '../../../core/api/models';
import { SessionStore } from '../../../core/auth/session.store';
import { grossFromNetMinor, minorToInput, parseMoneyToMinor } from '../../../core/format/money';
import { I18nService } from '../../../core/i18n/i18n.service';
import { describeProblem } from '../../../core/ui-state/toast.service';
import { createQuery } from '../../../shared/data/query';
import { ProductImageManagerComponent } from '../../../shared/product-images/product-image-manager';
import { UI } from '../../../shared/ui';
import {
  EntryAttempt,
  EntryContext,
  EntryDraft,
  EntryStep,
  StepErrors,
  attemptFor,
  buildEntryRequest,
  effectiveStockMode,
  emptyDraft,
  emptyNewProduct,
  emptyOffer,
  entrySteps,
  firstInvalidStep,
  imeiPolicy,
  keepAttemptAfter,
  newEntryKey,
  offerPossible,
  parseImeiList,
  parseModelList,
  stepForProblem,
  validateStep,
} from './manual-entry.logic';

const LOOKUP_DEBOUNCE_MS = 300;

/** Server field error keys (normalized, lower case) per wizard field. */
const SERVER_FIELDS: Record<string, string> = {
  brand: 'newproduct.brand',
  name: 'newproduct.name',
  category: 'newproduct.category',
  quality: 'newproduct.quality',
  ean: 'newproduct.ean',
  mpn: 'newproduct.mpn',
  identifier: 'newproduct.identifier',
  modelName: 'newproduct.modelname',
  compatibleModels: 'newproduct.compatiblemodels',
  quantity: 'quantity',
  imeis: 'imeis',
  purchasePrice: 'purchaseprice',
  supplierSku: 'offer.suppliersku',
  price: 'offer.price',
  minOrderQuantity: 'offer.minorderquantity',
  deliveryDays: 'offer.deliverydays',
};

/**
 * "Artikel anlegen" in Lager: identify (scan/lookup or new product) → stock (warehouse, quantity or IMEIs, EK) →
 * optional offer → summary. One idempotent POST /api/inventory/items; a retry of the same request reuses its key.
 */
@Component({
  selector: 'hn-manual-entry-dialog',
  imports: [FormsModule, ...UI, ProductImageManagerComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './manual-entry.dialog.html',
})
export class ManualEntryDialogComponent {
  private readonly inventory = inject(InventoryApi);
  protected readonly catalog = inject(CatalogApi);
  private readonly i18n = inject(I18nService);
  private readonly session = inject(SessionStore);

  readonly open = input(false);
  readonly closed = output<void>();
  /** Emits after every successful entry (the Lager page reloads its stock). */
  readonly saved = output<ManualEntryResult>();

  private readonly scanInput = viewChild<ElementRef<HTMLInputElement>>('scanInput');
  private readonly stepHeading = viewChild<ElementRef<HTMLElement>>('stepHeading');
  private lookupTimer: ReturnType<typeof setTimeout> | undefined;

  protected readonly conditions = OFFER_CONDITIONS;
  protected readonly categories = createQuery(
    () => this.open(),
    (load) => (load ? this.catalog.categories() : of(null)),
  );
  protected readonly warehouses = createQuery(
    () => this.open(),
    (load) => (load ? this.inventory.warehouses() : of([])),
  );

  protected readonly scanText = signal('');
  protected readonly lookupQuery = signal('');
  protected readonly lookup = createQuery(
    () => this.lookupQuery(),
    (q) => (q ? this.inventory.lookupItem(q) : of(null)),
  );
  /** Lookup result for the current query only (createQuery keeps the previous value while loading). */
  protected readonly lookupResult = computed(() => {
    const r = this.lookup.value();
    return r && r.query === this.lookupQuery().trim() ? r : null;
  });

  /** Mutable draft bound with ngModel; `touched` is bumped after programmatic changes so the view re-renders. */
  protected draft: EntryDraft = emptyDraft();
  private readonly touched = signal(0);
  protected readonly step = signal<EntryStep | 'done'>('identify');
  protected readonly errors = signal<StepErrors>({});
  protected readonly problem = signal<ApiProblem | null>(null);
  protected readonly problemText = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly result = signal<ManualEntryResult | null>(null);
  /** Result of the previous entry after "Speichern & nächster Artikel". */
  protected readonly lastSaved = signal<ManualEntryResult | null>(null);
  protected readonly imagesFor = signal<string | null>(null);
  private offerTouched = false;
  private pending: EntryAttempt | null = null;

  constructor() {
    effect(() => {
      if (this.open()) untracked(() => this.reset(true));
    });
    // Default warehouse once the list is there.
    effect(() => {
      const list = this.warehouses.value() ?? [];
      untracked(() => {
        if (!this.draft.warehouseId && list.length) {
          this.draft.warehouseId = (list.find((w) => w.isDefault) ?? list[0]).id;
          this.touch();
        }
      });
    });
    // A single exact EAN match is selected automatically (scanner flow).
    effect(() => {
      const r = this.lookupResult();
      untracked(() => {
        if (r && this.draft.productMode === null && r.matches.length === 1 && r.matches[0].matchedBy === 'Ean') this.choose(r.matches[0]);
      });
    });
    inject(DestroyRef).onDestroy(() => clearTimeout(this.lookupTimer));
  }

  // ---------------------------------------------------------------- context

  protected ctx(): EntryContext {
    this.touched();
    const r = this.lookupResult() ?? this.lookup.value();
    return {
      categories: this.categories.value(),
      canCreateProduct: r ? r.canCreateProduct : this.session.hasModule('ProductImport') && this.session.can('imports.manage'),
      canCreateOffer: r ? r.canCreateOffer : this.session.hasModule('B2BSales') && this.session.can('offers.manage'),
    };
  }

  protected steps(): EntryStep[] {
    return entrySteps(this.draft, this.ctx());
  }

  protected stepIndex(): number {
    const s = this.step();
    return s === 'done' ? this.steps().length : this.steps().indexOf(s);
  }

  protected policy() {
    return imeiPolicy(this.draft, this.ctx().categories);
  }

  protected stockMode() {
    return effectiveStockMode(this.draft.stockMode, this.policy());
  }

  protected imeiList() {
    this.touched();
    return parseImeiList(this.draft.imeiText);
  }

  protected models(): string[] {
    return parseModelList(this.draft.newProduct.compatibleModels);
  }

  protected offerPossible(): boolean {
    return offerPossible(this.draft, this.ctx());
  }

  protected grossPreview(): number | null {
    const net = parseMoneyToMinor(this.draft.offer.price);
    return net !== null && net > 0 ? grossFromNetMinor(net) : null;
  }

  protected ekMinor(): number | null {
    return this.draft.purchasePrice.trim() ? parseMoneyToMinor(this.draft.purchasePrice) : null;
  }

  protected priceMinor(): number | null {
    return parseMoneyToMinor(this.draft.offer.price);
  }

  protected warehouseLabel(id: string): string {
    const w = (this.warehouses.value() ?? []).find((x) => x.id === id);
    return w ? `${w.code} – ${w.name}` : '–';
  }

  protected productLabel(): string {
    if (this.draft.productMode === 'existing' && this.draft.match) return `${this.draft.match.product.brand} ${this.draft.match.product.name}`;
    return `${this.draft.newProduct.brand} ${this.draft.newProduct.name}`.trim();
  }

  /** Existing own offer of the chosen product (the entry updates it when the SKU matches). */
  protected existingOffer() {
    return this.draft.productMode === 'existing' ? (this.draft.match?.ownOffers[0] ?? null) : null;
  }

  // ---------------------------------------------------------------- errors

  protected fieldError(field: string): string | null {
    const key = this.errors()[field];
    if (key) return this.i18n.t(key, { max: field === 'compatibleModels' ? 100 : 500 });
    const server = fieldErrors(this.problem(), SERVER_FIELDS[field] ?? field);
    return server.length ? server.join(' ') : null;
  }

  protected touch(): void {
    this.touched.update((v) => v + 1);
  }

  // ---------------------------------------------------------------- step 1: identify

  protected onScanInput(value: string): void {
    this.scanText.set(value);
    clearTimeout(this.lookupTimer);
    if (value.trim().length < 2) return;
    this.lookupTimer = setTimeout(() => this.lookupNow(), LOOKUP_DEBOUNCE_MS);
  }

  protected lookupNow(): void {
    clearTimeout(this.lookupTimer);
    const q = this.scanText().trim();
    if (!q) return;
    if (q !== this.lookupQuery()) {
      // New search: forget the previous choice unless the user is creating a product.
      if (this.draft.productMode === 'existing') this.draft.productMode = null;
      this.draft.match = null;
      this.lookupQuery.set(q);
    } else {
      this.lookup.reload();
    }
    this.touch();
  }

  protected choose(match: LookupMatch): void {
    this.draft.productMode = 'existing';
    this.draft.match = match;
    this.errors.set({});
    this.prefillOffer();
    this.touch();
  }

  protected isChosen(match: LookupMatch): boolean {
    this.touched();
    return this.draft.productMode === 'existing' && this.draft.match?.product.id === match.product.id;
  }

  protected startNewProduct(): void {
    const r = this.lookupResult();
    const q = this.lookupQuery().trim();
    const hint = r?.newProduct;
    const isIdentifier = !!hint && (!!hint.ean || !!hint.mpn);
    this.draft.productMode = 'new';
    this.draft.match = null;
    this.draft.newProduct = emptyNewProduct({
      ean: hint?.ean ?? '',
      mpn: hint?.mpn ?? '',
      name: !isIdentifier && q && !r?.looksLikeImei ? q : '',
    });
    this.errors.set({});
    this.prefillOffer();
    this.touch();
    setTimeout(() => document.getElementById('entry-brand')?.focus());
  }

  protected cancelNewProduct(): void {
    this.draft.productMode = null;
    this.errors.set({});
    this.touch();
    this.focusScan();
  }

  protected onCategoryChange(): void {
    this.touch();
  }

  protected toggleImages(match: LookupMatch): void {
    this.imagesFor.set(this.imagesFor() === match.product.id ? null : match.product.id);
  }

  // ---------------------------------------------------------------- offer prefill

  private prefillOffer(): void {
    const existing = this.existingOffer();
    const sku = this.draft.match?.suggestedSku ?? (this.draft.newProduct.mpn.trim() || this.draft.newProduct.ean.trim());
    this.draft.offer = existing
      ? {
          enabled: this.draft.offer.enabled,
          supplierSku: existing.supplierSku,
          price: minorToInput(existing.priceMinor),
          minOrderQuantity: String(existing.minOrderQuantity),
          deliveryDays: String(existing.deliveryDays),
          condition: existing.condition,
          publish: existing.status === 'Published' || existing.status === 'Draft',
        }
      : { ...emptyOffer(), enabled: this.draft.offer.enabled, supplierSku: sku };
  }

  // ---------------------------------------------------------------- navigation

  protected goTo(step: EntryStep): void {
    const steps = this.steps();
    const target = steps.indexOf(step);
    if (target < 0) return;
    // Forward only through valid steps.
    for (let i = 0; i < target; i++) {
      if (!this.check(steps[i])) return;
    }
    this.enter(step);
  }

  protected next(): void {
    const s = this.step();
    if (s === 'done') return;
    if (!this.check(s)) return;
    const steps = this.steps();
    const following = steps[steps.indexOf(s) + 1];
    if (following) this.enter(following);
  }

  protected back(): void {
    const s = this.step();
    if (s === 'done') return;
    const steps = this.steps();
    const previous = steps[steps.indexOf(s) - 1];
    if (previous) this.enter(previous);
  }

  private check(step: EntryStep): boolean {
    const errors = validateStep(step, this.draft, this.ctx());
    if (Object.keys(errors).length) {
      if (this.step() !== step) this.step.set(step);
      this.errors.set(errors);
      this.focusFirstError();
      return false;
    }
    return true;
  }

  private enter(step: EntryStep): void {
    if (step === 'stock' && this.draft.productMode === 'new' && !this.draft.offer.supplierSku) this.prefillOffer();
    if (step === 'offer' && !this.offerTouched) {
      this.draft.offer.enabled = true;
      this.offerTouched = true;
    }
    this.errors.set({});
    this.step.set(step);
    this.touch();
    setTimeout(() => {
      if (step === 'identify') this.focusScan();
      else this.stepHeading()?.nativeElement.focus();
    });
  }

  private focusFirstError(): void {
    setTimeout(() => (document.querySelector('[data-entry-dialog] [aria-invalid="true"]') as HTMLElement | null)?.focus());
  }

  protected focusScan(): void {
    setTimeout(() => {
      const el = this.scanInput()?.nativeElement;
      el?.focus();
      el?.select();
    });
  }

  // ---------------------------------------------------------------- submit

  protected async submit(andNext: boolean): Promise<void> {
    if (this.busy()) return;
    const ctx = this.ctx();
    const invalid = firstInvalidStep(this.draft, ctx);
    if (invalid) {
      this.step.set(invalid);
      this.check(invalid);
      return;
    }
    const body = buildEntryRequest(this.draft, ctx);
    const attempt = attemptFor(this.pending, JSON.stringify(body), newEntryKey);
    this.pending = attempt;
    this.busy.set(true);
    this.problem.set(null);
    this.problemText.set(null);
    try {
      const result = await firstValueFrom(this.inventory.createItem(body, attempt.key));
      this.pending = null;
      this.saved.emit(result);
      if (andNext) {
        this.lastSaved.set(result);
        this.reset(false);
      } else {
        this.result.set(result);
        this.step.set('done');
        setTimeout(() => this.stepHeading()?.nativeElement.focus());
      }
    } catch (e) {
      const problem = toApiProblem(e);
      if (!keepAttemptAfter(problem.status)) this.pending = null;
      this.problem.set(problem);
      this.problemText.set(this.describe(problem, e));
      const target = stepForProblem(problem.code, Object.keys(problem.errors));
      if (target !== 'summary' && this.steps().includes(target)) this.step.set(target);
    } finally {
      this.busy.set(false);
    }
  }

  /** True while a failed attempt with unknown outcome is pending (the retry reuses its key). */
  protected retryPending(): boolean {
    return !!this.pending && !!this.problem() && keepAttemptAfter(this.problem()!.status);
  }

  private describe(problem: ApiProblem, raw: unknown): string {
    if (problem.code === 'unit.imei_in_stock') {
      const imei = raw instanceof HttpErrorResponse ? (raw.error?.imei as string | undefined) : undefined;
      return imei ? this.i18n.t('inventory.entry.imeiInStock', { imei }) : this.i18n.t('inventory.entry.imeiInStockAny');
    }
    if (problem.code === 'validation.failed') return this.i18n.t('inventory.entry.validationFailed');
    if (problem.status === 0 || problem.status >= 500) return `${describeProblem(this.i18n, problem)} ${this.i18n.t('inventory.entry.retryHint')}`;
    return describeProblem(this.i18n, problem);
  }

  // ---------------------------------------------------------------- reset / close

  /** Fresh draft; keeps the warehouse (fast entry of several items into the same Lager). */
  private reset(full: boolean): void {
    const warehouse = full ? '' : this.draft.warehouseId;
    this.draft = emptyDraft(warehouse);
    if (full) {
      const list = this.warehouses.value() ?? [];
      if (list.length) this.draft.warehouseId = (list.find((w) => w.isDefault) ?? list[0]).id;
      this.lastSaved.set(null);
    }
    this.offerTouched = false;
    this.pending = null;
    this.scanText.set('');
    this.lookupQuery.set('');
    this.errors.set({});
    this.problem.set(null);
    this.problemText.set(null);
    this.result.set(null);
    this.imagesFor.set(null);
    this.step.set('identify');
    this.touch();
    this.focusScan();
  }

  protected nextItem(): void {
    this.lastSaved.set(this.result());
    this.reset(false);
  }

  protected close(): void {
    clearTimeout(this.lookupTimer);
    this.closed.emit();
  }
}
