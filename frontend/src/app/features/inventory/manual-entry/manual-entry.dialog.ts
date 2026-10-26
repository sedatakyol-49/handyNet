import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom, of } from 'rxjs';
import { ApiProblem, fieldErrors, toApiProblem } from '../../../core/api/api-problem';
import { CatalogApi, InventoryApi } from '../../../core/api/handynet-api';
import { LookupMatch, ManualEntryResult, OFFER_CONDITIONS } from '../../../core/api/models';
import { SessionStore } from '../../../core/auth/session.store';
import { grossFromNetMinor, parseMoneyToMinor } from '../../../core/format/money';
import { I18nService } from '../../../core/i18n/i18n.service';
import { describeProblem } from '../../../core/ui-state/toast.service';
import { createQuery } from '../../../shared/data/query';
import { ProductImageManagerComponent } from '../../../shared/product-images/product-image-manager';
import { UI } from '../../../shared/ui';
import {
  BULK_STEPS,
  BulkRow,
  EntryAttempt,
  EntryContext,
  EntryDraft,
  EntryStep,
  StepErrors,
  WizardStep,
  addToSelection,
  attemptFor,
  buildEntryRequest,
  bulkSummary,
  crossRowImeiDuplicates,
  effectiveStockMode,
  emptyDraft,
  emptyNewProduct,
  emptyOffer,
  entrySteps,
  firstInvalidStep,
  hasUnsavedRows,
  imeiPolicy,
  isSelected,
  keepAttemptAfter,
  newEntryKey,
  offerPossible,
  offerPrefill,
  parseImeiList,
  parseModelList,
  removeFromSelection,
  rowOfferPossible,
  rowPolicy,
  rowStockMode,
  rowsToSend,
  runBulk,
  setQuantityForAll,
  stepForProblem,
  imeiProblems,
  imeiProblemKey,
  syncRows,
  toggleSelection,
  validateRows,
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
 * "Artikel anlegen" in Lager.
 * - One product (one ticked match or a new product): identify → stock (warehouse, quantity or IMEIs, EK) → optional
 *   offer → summary. One idempotent POST /api/inventory/items; a retry of the same request reuses its key.
 * - Several ticked matches (the selection survives new searches): identify → one table with a row per product (shared
 *   warehouse, quantity or IMEIs, EK, optional offer) → review + save. Every row is its own POST with its own stable
 *   Idempotency-Key (≤ 3 in flight); "Fehlgeschlagene erneut senden" resends only rows that are not saved.
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
  private readonly injector = inject(Injector);

  readonly open = input(false);
  readonly closed = output<void>();
  /** Emits after every successful entry / bulk run with saved rows (the Lager page reloads its stock). */
  readonly saved = output<ManualEntryResult>();

  private readonly scanInput = viewChild<ElementRef<HTMLInputElement>>('scanInput');
  private readonly stepHeading = viewChild<ElementRef<HTMLElement>>('stepHeading');
  private readonly closeKeep = viewChild('closeKeep', { read: ElementRef<HTMLButtonElement> });
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

  /** Ticked lookup matches (kept across searches). One → single flow, two or more → bulk table. */
  protected readonly selection = signal<LookupMatch[]>([]);

  /** Mutable draft bound with ngModel; `touched` is bumped after programmatic changes so the view re-renders. */
  protected draft: EntryDraft = emptyDraft();
  private readonly touched = signal(0);
  protected readonly step = signal<WizardStep | 'done'>('identify');
  protected readonly errors = signal<StepErrors>({});
  protected readonly problem = signal<ApiProblem | null>(null);
  protected readonly problemText = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly result = signal<ManualEntryResult | null>(null);
  /** Result of the previous entry after "Speichern & nächster Artikel". */
  protected readonly lastSaved = signal<ManualEntryResult | null>(null);
  /** Saved rows of the previous bulk run after "Weitere Artikel erfassen". */
  protected readonly lastBulkSaved = signal<number | null>(null);
  protected readonly imagesFor = signal<string | null>(null);
  private offerTouched = false;
  private pending: EntryAttempt | null = null;

  /** Bulk table rows (mutable, bound with ngModel). */
  protected rows: BulkRow[] = [];
  protected readonly rowErrors = signal<Map<string, StepErrors>>(new Map());
  /** True once a bulk run was sent (the review shows the per-row outcome and the summary). */
  protected readonly bulkRan = signal(false);
  protected allQuantity = '';
  /** "Ungespeicherte Zeilen verwerfen?" before closing. */
  protected readonly confirmClose = signal(false);

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
    // A single exact EAN match is ticked automatically (scanner flow; scanning several EANs builds a selection).
    effect(() => {
      const r = this.lookupResult();
      untracked(() => {
        if (r && this.step() === 'identify' && this.draft.productMode !== 'new' && r.matches.length === 1 && r.matches[0].matchedBy === 'Ean') {
          this.setSelection(addToSelection(this.selection(), r.matches[0]));
        }
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

  /** Bulk flow: in its steps, or on step 1 with two or more ticked products. */
  protected bulk(): boolean {
    this.touched();
    const s = this.step();
    if (s === 'rows' || s === 'review') return true;
    return this.draft.productMode !== 'new' && this.selection().length >= 2;
  }

  protected steps(): WizardStep[] {
    return this.bulk() ? BULK_STEPS : entrySteps(this.draft, this.ctx());
  }

  protected stepIndex(): number {
    const s = this.step();
    return s === 'done' ? this.steps().length : this.steps().indexOf(s);
  }

  /** Step buttons are disabled where going there makes no sense (back to the search after rows were saved). */
  protected stepDisabled(s: WizardStep): boolean {
    return this.busy() || (this.bulk() && s === 'identify' && this.summary().saved > 0);
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
    return this.grossOf(this.draft.offer.price);
  }

  protected grossOf(price: string): number | null {
    const net = parseMoneyToMinor(price);
    return net !== null && net > 0 ? grossFromNetMinor(net) : null;
  }

  protected ekMinor(): number | null {
    return this.draft.purchasePrice.trim() ? parseMoneyToMinor(this.draft.purchasePrice) : null;
  }

  protected moneyMinor(value: string): number | null {
    return value.trim() ? parseMoneyToMinor(value) : null;
  }

  protected priceMinor(): number | null {
    return parseMoneyToMinor(this.draft.offer.price);
  }

  protected warehouseLabel(id: string): string {
    const w = (this.warehouses.value() ?? []).find((x) => x.id === id);
    return w ? `${w.code} – ${w.name}` : this.i18n.t('inventory.entry.defaultWarehouseAuto');
  }

  protected productLabel(): string {
    if (this.draft.productMode === 'existing' && this.draft.match) return `${this.draft.match.product.brand} ${this.draft.match.product.name}`;
    return `${this.draft.newProduct.brand} ${this.draft.newProduct.name}`.trim();
  }

  protected matchLabel(m: LookupMatch): string {
    return `${m.product.brand} ${m.product.name}`;
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

  // ---------------------------------------------------------------- step 1: identify + selection

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
    // The selection is kept: ticked products of earlier searches stay ticked.
    if (q !== this.lookupQuery()) this.lookupQuery.set(q);
    else this.lookup.reload();
    this.touch();
  }

  protected isSelected(match: LookupMatch): boolean {
    return isSelected(this.selection(), match.product.id);
  }

  protected toggle(match: LookupMatch): void {
    if (this.draft.productMode === 'new') return;
    this.setSelection(toggleSelection(this.selection(), match));
  }

  /** Click anywhere on a match row toggles it (except on its own controls). */
  protected onMatchClick(event: MouseEvent, match: LookupMatch): void {
    const target = event.target as HTMLElement | null;
    if (target?.closest('button, a, input, label, textarea, select')) return;
    this.toggle(match);
  }

  protected unselect(productId: string): void {
    this.setSelection(removeFromSelection(this.selection(), productId));
    // Keep the focus in the tray (next chip) or fall back to the search field.
    setTimeout(() => {
      const chip = document.querySelector<HTMLElement>('[data-entry-dialog] [data-testid="entry-chip-remove"]');
      (chip ?? this.scanInput()?.nativeElement)?.focus();
    });
  }

  protected clearSelection(): void {
    this.setSelection([]);
    this.focusScan();
  }

  private setSelection(next: LookupMatch[]): void {
    this.selection.set(next);
    // One ticked product = the single flow with that match.
    if (this.draft.productMode !== 'new') {
      if (next.length === 1) {
        const changed = this.draft.match?.product.id !== next[0].product.id;
        this.draft.productMode = 'existing';
        this.draft.match = next[0];
        if (changed) this.prefillOffer();
      } else {
        this.draft.productMode = null;
        this.draft.match = null;
      }
    }
    this.errors.set({});
    this.touch();
  }

  protected startNewProduct(): void {
    if (this.selection().length) return;
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

  // ---------------------------------------------------------------- offer prefill (single flow)

  private prefillOffer(): void {
    if (this.draft.productMode === 'existing' && this.draft.match) {
      this.draft.offer = offerPrefill(this.draft.match, this.draft.offer.enabled);
      return;
    }
    const sku = this.draft.newProduct.mpn.trim() || this.draft.newProduct.ean.trim();
    this.draft.offer = { ...emptyOffer(), enabled: this.draft.offer.enabled, supplierSku: sku };
  }

  // ---------------------------------------------------------------- bulk rows

  protected rowPolicy(row: BulkRow) {
    return rowPolicy(row);
  }

  protected rowMode(row: BulkRow) {
    this.touched();
    return rowStockMode(row);
  }

  protected rowOfferPossible(row: BulkRow): boolean {
    return rowOfferPossible(row, this.ctx());
  }

  protected rowImeis(row: BulkRow) {
    this.touched();
    return parseImeiList(row.imeiText);
  }

  /** IMEIs of this row that are also entered in another row (live). */
  protected rowImeiClashes(row: BulkRow): string[] {
    this.touched();
    return crossRowImeiDuplicates(this.rows).get(row.id) ?? [];
  }

  protected rowError(row: BulkRow, field: string): string | null {
    const key = this.rowErrors().get(row.id)?.[field];
    return key ? this.i18n.t(key, { max: 500 }) : null;
  }

  protected statusClass(status: BulkRow['status']): string {
    switch (status) {
      case 'saved':
        return 'bg-emerald-50 text-emerald-800 ring-emerald-200';
      case 'error':
        return 'bg-red-50 text-red-800 ring-red-200';
      case 'saving':
        return 'bg-blue-50 text-blue-800 ring-blue-200';
      default:
        return 'bg-slate-100 text-slate-700 ring-slate-300';
    }
  }

  protected rowLocked(row: BulkRow): boolean {
    return row.status === 'saved' || row.status === 'saving';
  }

  protected summary() {
    this.touched();
    return bulkSummary(this.rows);
  }

  protected toSendCount(): number {
    this.touched();
    return rowsToSend(this.rows).length;
  }

  protected rowsInError(): BulkRow[] {
    this.touched();
    return this.rows.filter((r) => r.status === 'error');
  }

  /** Live re-validation after the first failed check, so fixed fields lose their message while typing. */
  protected onRowInput(): void {
    if (this.rowErrors().size) this.rowErrors.set(validateRows(this.rows, this.ctx()));
    this.touch();
  }

  protected applyQuantityToAll(): void {
    const q = this.allQuantity.trim();
    if (!q) return;
    setQuantityForAll(this.rows, q);
    this.onRowInput();
  }

  protected removeRow(row: BulkRow): void {
    if (this.rowLocked(row)) return;
    const index = this.rows.indexOf(row);
    this.rows = this.rows.filter((r) => r !== row);
    this.selection.set(removeFromSelection(this.selection(), row.id));
    const errors = new Map(this.rowErrors());
    errors.delete(row.id);
    this.rowErrors.set(errors);
    this.touch();
    if (!this.rows.length) {
      this.setSelection([]);
      this.enter('identify');
      return;
    }
    this.focusRow(this.rows[Math.min(index, this.rows.length - 1)]);
  }

  /** From the review back to a failed row to fix it. */
  protected editRow(row: BulkRow): void {
    this.enter('rows', `[data-row-id="${row.id}"] [data-row-focus]`);
  }

  // ---------------------------------------------------------------- navigation

  protected goTo(step: WizardStep): void {
    if (this.stepDisabled(step)) return;
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
    if (previous && !this.stepDisabled(previous)) this.enter(previous);
  }

  private check(step: WizardStep): boolean {
    if (this.bulk()) {
      if (step === 'identify') return this.selection().length >= 2 || this.rows.length > 0;
      if (step === 'rows') {
        const errors = validateRows(this.rows, this.ctx());
        this.rowErrors.set(errors);
        if (errors.size) {
          if (this.step() !== 'rows') this.step.set('rows');
          this.touch();
          this.focusFirstError();
          return false;
        }
      }
      return true;
    }
    const errors = validateStep(step as EntryStep, this.draft, this.ctx());
    if (Object.keys(errors).length) {
      if (this.step() !== step) this.step.set(step);
      this.errors.set(errors);
      this.focusFirstError();
      return false;
    }
    return true;
  }

  private enter(step: WizardStep, focusSelector?: string): void {
    if (step === 'rows' && this.step() === 'identify') {
      this.rows = syncRows(this.rows, this.selection(), newEntryKey);
      this.rowErrors.set(new Map());
    }
    if (step === 'stock' && this.draft.productMode === 'new' && !this.draft.offer.supplierSku) this.prefillOffer();
    if (step === 'offer' && !this.offerTouched) {
      this.draft.offer.enabled = true;
      this.offerTouched = true;
    }
    this.errors.set({});
    this.step.set(step);
    this.touch();
    afterNextRender(
      () => {
        const target = focusSelector ? document.querySelector<HTMLElement>(focusSelector) : null;
        if (target) target.focus();
        else if (step === 'identify') this.focusScan();
        else this.stepHeading()?.nativeElement.focus();
      },
      { injector: this.injector },
    );
  }

  private focusRow(row: BulkRow): void {
    afterNextRender(() => document.querySelector<HTMLElement>(`[data-row-id="${row.id}"] [data-row-focus]`)?.focus(), { injector: this.injector });
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

  // ---------------------------------------------------------------- submit (single)

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
        this.lastBulkSaved.set(null);
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
    if (problem.code === 'imei.blacklisted') {
      const imei = raw instanceof HttpErrorResponse ? (raw.error?.imei as string | undefined) : undefined;
      return imei ? this.i18n.t('inventory.entry.imeiBlacklisted', { imei }) : this.i18n.t('inventory.entry.imeiBlacklistedAny');
    }
    if (problem.code === 'entry.imei_conflicts') {
      const list = imeiProblems(raw instanceof HttpErrorResponse ? raw.error : null)
        .map((p) => `${p.imei} (${this.i18n.t(imeiProblemKey(p.code))})`)
        .join(', ');
      return this.i18n.t('inventory.entry.imeiConflicts', { list });
    }
    if (problem.code === 'validation.failed') return this.i18n.t('inventory.entry.validationFailed');
    if (problem.status === 0 || problem.status >= 500) return `${describeProblem(this.i18n, problem)} ${this.i18n.t('inventory.entry.retryHint')}`;
    return describeProblem(this.i18n, problem);
  }

  // ---------------------------------------------------------------- submit (bulk)

  /** Sends every row that is not saved yet (first run: all; later: the failed ones), each with its own key. */
  protected async saveRows(): Promise<void> {
    if (this.busy()) return;
    const ctx = this.ctx();
    if (!this.check('rows')) return;
    if (!rowsToSend(this.rows).length) return;
    this.busy.set(true);
    this.bulkRan.set(true);
    const last = await runBulk(this.rows, {
      warehouseId: this.draft.warehouseId,
      ctx,
      send: (body, key) => firstValueFrom(this.inventory.createItem(body, key)),
      describe: (e) => {
        const problem = toApiProblem(e);
        return { code: problem.code, text: this.describe(problem, e) };
      },
      onChange: () => this.touch(),
    });
    this.busy.set(false);
    if (last) this.saved.emit(last);
    this.touch();
    setTimeout(() => this.stepHeading()?.nativeElement.focus());
  }

  /** "Weitere Artikel erfassen": new selection in the same warehouse, focus in the search field. */
  protected moreItems(): void {
    this.lastBulkSaved.set(this.summary().saved);
    this.lastSaved.set(null);
    this.reset(false);
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
      this.lastBulkSaved.set(null);
    }
    this.offerTouched = false;
    this.pending = null;
    this.selection.set([]);
    this.rows = [];
    this.rowErrors.set(new Map());
    this.bulkRan.set(false);
    this.allQuantity = '';
    this.confirmClose.set(false);
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
    this.lastBulkSaved.set(null);
    this.reset(false);
  }

  /** Closing with bulk rows that are not saved (or still sending) asks first. */
  protected needsCloseConfirm(): boolean {
    this.touched();
    const s = this.step();
    return (s === 'rows' || s === 'review') && (this.busy() || hasUnsavedRows(this.rows));
  }

  protected close(): void {
    if (!this.open()) return;
    if (this.needsCloseConfirm()) {
      this.confirmClose.set(true);
      afterNextRender(() => this.closeKeep()?.nativeElement.focus(), { injector: this.injector });
      return;
    }
    this.closeNow();
  }

  protected keepEditing(): void {
    this.confirmClose.set(false);
    setTimeout(() => this.stepHeading()?.nativeElement.focus());
  }

  protected closeNow(): void {
    clearTimeout(this.lookupTimer);
    this.confirmClose.set(false);
    this.rows = [];
    this.closed.emit();
  }
}
