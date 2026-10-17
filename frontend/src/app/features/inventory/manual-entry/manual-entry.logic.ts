import { CatalogCategories, LookupMatch, ManualEntryRequest, ManualEntryResult, OfferCondition } from '../../../core/api/models';
import { minorToDecimal, minorToInput, parseMoneyToMinor } from '../../../core/format/money';

/**
 * Pure rules of the "Artikel anlegen" wizard (identify → stock → offer → summary). They mirror the API
 * (ManualEntryService / ManualEntryRules); the API stays authoritative and its error codes are mapped below.
 */

export const MAX_IMEIS = 500;
export const MAX_QUANTITY = 1_000_000;
export const MAX_COMPATIBLE_MODELS = 100;
export const MAX_DELIVERY_DAYS = 60;

// ---------------------------------------------------------------- IMEI / GTIN

/** Removes whitespace, dashes and slashes (same as the API's Imei.TryNormalize). */
export function normalizeImei(input: string | null | undefined): string {
  return (input ?? '').replace(/[\s\-/]/g, '');
}

/** 15 digits with a valid Luhn check digit. */
export function isValidImei(input: string | null | undefined): boolean {
  const imei = normalizeImei(input);
  if (!/^\d{15}$/.test(imei)) return false;
  let sum = 0;
  for (let i = 0; i < 14; i++) {
    let digit = imei.charCodeAt(i) - 48;
    if (i % 2 === 1) {
      digit *= 2;
      if (digit > 9) digit -= 9;
    }
    sum += digit;
  }
  return (10 - (sum % 10)) % 10 === imei.charCodeAt(14) - 48;
}

export type ImeiStatus = 'ok' | 'length' | 'checksum' | 'duplicate';

export interface ParsedImei {
  /** 1-based line of the input. */
  line: number;
  raw: string;
  imei: string;
  status: ImeiStatus;
}

export interface ImeiList {
  entries: ParsedImei[];
  /** Valid, distinct IMEIs in input order (what is sent to the API). */
  valid: string[];
  invalid: number;
  duplicates: number;
}

/**
 * One IMEI per line (scanners type the digits + Enter); commas, semicolons and tabs separate as well. Spaces, dashes
 * and slashes inside an IMEI are ignored. The second occurrence of an IMEI is reported as duplicate.
 */
export function parseImeiList(text: string | null | undefined): ImeiList {
  const entries: ParsedImei[] = [];
  const seen = new Set<string>();
  const lines = (text ?? '').split(/\r?\n/);
  lines.forEach((lineText, index) => {
    for (const part of lineText.split(/[,;\t]/)) {
      const raw = part.trim();
      if (!raw) continue;
      const imei = normalizeImei(raw);
      let status: ImeiStatus;
      if (!/^\d{15}$/.test(imei)) status = 'length';
      else if (!isValidImei(imei)) status = 'checksum';
      else if (seen.has(imei)) status = 'duplicate';
      else status = 'ok';
      if (status === 'ok') seen.add(imei);
      entries.push({ line: index + 1, raw, imei, status });
    }
  });
  return {
    entries,
    valid: entries.filter((e) => e.status === 'ok').map((e) => e.imei),
    invalid: entries.filter((e) => e.status === 'length' || e.status === 'checksum').length,
    duplicates: entries.filter((e) => e.status === 'duplicate').length,
  };
}

/** GTIN-8/12/13/14 with a valid check digit (same rule as the API's Gtin.TryNormalize). */
export function isValidGtin(input: string | null | undefined): boolean {
  const digits = (input ?? '').trim().replace(/ /g, '');
  if (![8, 12, 13, 14].includes(digits.length) || !/^\d+$/.test(digits)) return false;
  let sum = 0;
  for (let i = 0; i < digits.length - 1; i++) {
    const digit = digits.charCodeAt(digits.length - 2 - i) - 48;
    sum += i % 2 === 0 ? digit * 3 : digit;
  }
  return (10 - (sum % 10)) % 10 === digits.charCodeAt(digits.length - 1) - 48;
}

/** Compatible models: one per line or comma separated, trimmed, without duplicates (case-insensitive). */
export function parseModelList(text: string | null | undefined): string[] {
  const out: string[] = [];
  const seen = new Set<string>();
  for (const part of (text ?? '').split(/[\r\n,;]+/)) {
    const value = part.trim();
    if (!value || seen.has(value.toLowerCase())) continue;
    seen.add(value.toLowerCase());
    out.push(value);
  }
  return out;
}

// ---------------------------------------------------------------- draft + policy

export type StockMode = 'quantity' | 'imei';
export type ImeiPolicy = 'required' | 'optional' | 'none';
export type EntryStep = 'identify' | 'stock' | 'offer' | 'summary';

export interface NewProductDraft {
  brand: string;
  name: string;
  category: string;
  quality: string;
  ean: string;
  mpn: string;
  modelName: string;
  /** Textarea: one per line or comma separated. */
  compatibleModels: string;
}

export interface OfferDraft {
  enabled: boolean;
  supplierSku: string;
  /** Net VK as typed ("12,50"). */
  price: string;
  minOrderQuantity: string;
  deliveryDays: string;
  condition: OfferCondition;
  publish: boolean;
}

export interface EntryDraft {
  /** 'existing' = a lookup match was chosen, 'new' = create a product. */
  productMode: 'existing' | 'new' | null;
  match: LookupMatch | null;
  newProduct: NewProductDraft;
  warehouseId: string;
  stockMode: StockMode;
  quantity: string;
  imeiText: string;
  /** Net EK per unit as typed, optional. */
  purchasePrice: string;
  offer: OfferDraft;
}

export function emptyNewProduct(prefill: Partial<NewProductDraft> = {}): NewProductDraft {
  return { brand: '', name: '', category: '', quality: 'NotApplicable', ean: '', mpn: '', modelName: '', compatibleModels: '', ...prefill };
}

export function emptyOffer(): OfferDraft {
  return { enabled: false, supplierSku: '', price: '', minOrderQuantity: '1', deliveryDays: '2', condition: 'New', publish: true };
}

export function emptyDraft(warehouseId = ''): EntryDraft {
  return {
    productMode: null,
    match: null,
    newProduct: emptyNewProduct(),
    warehouseId,
    stockMode: 'quantity',
    quantity: '1',
    imeiText: '',
    purchasePrice: '',
    offer: emptyOffer(),
  };
}

/** Whether units must, may or must not be entered by IMEI for the chosen/new product. */
export function imeiPolicy(draft: Pick<EntryDraft, 'productMode' | 'match' | 'newProduct'>, categories: CatalogCategories | null): ImeiPolicy {
  if (draft.productMode === 'existing' && draft.match) {
    if (draft.match.imeiRequired) return 'required';
    return draft.match.imeiAllowed ? 'optional' : 'none';
  }
  if (draft.productMode === 'new') {
    const category = draft.newProduct.category;
    if (!category || !categories) return 'none';
    if (categories.imeiRequired.includes(category)) return 'required';
    return categories.imeiAllowed.includes(category) ? 'optional' : 'none';
  }
  return 'none';
}

/** The stock mode that applies given the policy (a required policy forces IMEI, none forces quantity). */
export function effectiveStockMode(mode: StockMode, policy: ImeiPolicy): StockMode {
  if (policy === 'required') return 'imei';
  if (policy === 'none') return 'quantity';
  return mode;
}

export interface EntryContext {
  categories: CatalogCategories | null;
  canCreateProduct: boolean;
  canCreateOffer: boolean;
}

/**
 * The API refuses offers for serialized (IMEI) stock, so the offer step exists only when the organization may
 * create offers and the entry is not stocked by IMEI.
 */
export function offerPossible(draft: EntryDraft, ctx: EntryContext): boolean {
  if (!ctx.canCreateOffer) return false;
  const policy = imeiPolicy(draft, ctx.categories);
  if (policy === 'required') return false;
  return effectiveStockMode(draft.stockMode, policy) !== 'imei';
}

export function entrySteps(draft: EntryDraft, ctx: EntryContext): EntryStep[] {
  return offerPossible(draft, ctx) ? ['identify', 'stock', 'offer', 'summary'] : ['identify', 'stock', 'summary'];
}

// ---------------------------------------------------------------- validation (values are i18n keys)

export type StepErrors = Record<string, string>;

export function validateIdentify(draft: EntryDraft, ctx: EntryContext): StepErrors {
  const errors: StepErrors = {};
  if (draft.productMode === 'existing') {
    if (!draft.match) errors['product'] = 'inventory.entry.v.chooseProduct';
    return errors;
  }
  if (draft.productMode !== 'new') {
    errors['product'] = 'inventory.entry.v.chooseProduct';
    return errors;
  }
  if (!ctx.canCreateProduct) {
    errors['product'] = 'inventory.entry.v.noProductCreation';
    return errors;
  }
  const p = draft.newProduct;
  if (!p.brand.trim()) errors['brand'] = 'inventory.entry.v.required';
  else if (p.brand.trim().length > 100) errors['brand'] = 'inventory.entry.v.tooLong';
  if (!p.name.trim()) errors['name'] = 'inventory.entry.v.required';
  else if (p.name.trim().length > 300) errors['name'] = 'inventory.entry.v.tooLong';
  if (!p.category) errors['category'] = 'inventory.entry.v.required';
  else if (ctx.categories && !ctx.categories.categories.includes(p.category)) errors['category'] = 'inventory.entry.v.required';
  if (p.ean.trim() && !isValidGtin(p.ean)) errors['ean'] = 'inventory.entry.v.ean';
  if (p.mpn.trim().length > 100) errors['mpn'] = 'inventory.entry.v.tooLong';
  if (!p.ean.trim() && !p.mpn.trim()) errors['identifier'] = 'inventory.entry.v.identifier';
  if (p.modelName.trim().length > 200) errors['modelName'] = 'inventory.entry.v.tooLong';
  const models = parseModelList(p.compatibleModels);
  if (models.length > MAX_COMPATIBLE_MODELS) errors['compatibleModels'] = 'inventory.entry.v.tooManyModels';
  else if (models.some((m) => m.length > 200)) errors['compatibleModels'] = 'inventory.entry.v.tooLong';
  return errors;
}

/** Whole number in [min, max] typed as text; null when invalid. */
export function parseWholeNumber(input: string | number | null | undefined, min: number, max: number): number | null {
  const s = String(input ?? '').trim();
  if (!/^\d+$/.test(s)) return null;
  const n = Number(s);
  return n >= min && n <= max ? n : null;
}

export function validateStock(draft: EntryDraft, ctx: EntryContext): StepErrors {
  const errors: StepErrors = {};
  const mode = effectiveStockMode(draft.stockMode, imeiPolicy(draft, ctx.categories));
  if (mode === 'imei') {
    const list = parseImeiList(draft.imeiText);
    if (list.entries.length === 0) errors['imeis'] = 'inventory.entry.v.imeiMissing';
    else if (list.invalid > 0) errors['imeis'] = 'inventory.entry.v.imeiInvalid';
    else if (list.duplicates > 0) errors['imeis'] = 'inventory.entry.v.imeiDuplicate';
    else if (list.valid.length > MAX_IMEIS) errors['imeis'] = 'inventory.entry.v.imeiTooMany';
  } else if (parseWholeNumber(draft.quantity, 0, MAX_QUANTITY) === null) {
    errors['quantity'] = 'inventory.entry.v.quantity';
  }
  if (draft.purchasePrice.trim()) {
    const minor = parseMoneyToMinor(draft.purchasePrice);
    if (minor === null || minor < 0) errors['purchasePrice'] = 'inventory.entry.v.money';
  }
  return errors;
}

export function validateOffer(draft: EntryDraft): StepErrors {
  const errors: StepErrors = {};
  const o = draft.offer;
  if (!o.enabled) return errors;
  if (!o.supplierSku.trim()) errors['supplierSku'] = 'inventory.entry.v.required';
  else if (o.supplierSku.trim().length > 100) errors['supplierSku'] = 'inventory.entry.v.tooLong';
  const price = parseMoneyToMinor(o.price);
  if (price === null || price <= 0) errors['price'] = 'inventory.entry.v.price';
  if (parseWholeNumber(o.minOrderQuantity, 1, MAX_QUANTITY) === null) errors['minOrderQuantity'] = 'inventory.entry.v.moq';
  if (parseWholeNumber(o.deliveryDays, 0, MAX_DELIVERY_DAYS) === null) errors['deliveryDays'] = 'inventory.entry.v.deliveryDays';
  return errors;
}

export function validateStep(step: EntryStep, draft: EntryDraft, ctx: EntryContext): StepErrors {
  switch (step) {
    case 'identify':
      return validateIdentify(draft, ctx);
    case 'stock':
      return validateStock(draft, ctx);
    case 'offer':
      return offerPossible(draft, ctx) ? validateOffer(draft) : {};
    case 'summary':
      return {};
  }
}

/** First step (in wizard order) with validation errors, or null when the entry can be submitted. */
export function firstInvalidStep(draft: EntryDraft, ctx: EntryContext): EntryStep | null {
  for (const step of entrySteps(draft, ctx)) {
    if (Object.keys(validateStep(step, draft, ctx)).length > 0) return step;
  }
  return null;
}

// ---------------------------------------------------------------- request

function blankToNull(value: string): string | null {
  const v = value.trim();
  return v ? v : null;
}

/** Builds the API request from a valid draft (call firstInvalidStep first). */
export function buildEntryRequest(draft: EntryDraft, ctx: EntryContext): ManualEntryRequest {
  const policy = imeiPolicy(draft, ctx.categories);
  const mode = effectiveStockMode(draft.stockMode, policy);
  const request: ManualEntryRequest = { warehouseId: draft.warehouseId || null };

  if (draft.productMode === 'existing' && draft.match) {
    request.productId = draft.match.product.id;
  } else {
    const p = draft.newProduct;
    const models = parseModelList(p.compatibleModels);
    request.newProduct = {
      brand: p.brand.trim(),
      name: p.name.trim(),
      category: p.category,
      quality: p.quality || null,
      ean: blankToNull(p.ean.replace(/ /g, '')),
      mpn: blankToNull(p.mpn),
      modelName: blankToNull(p.modelName),
      compatibleModels: models.length ? models : null,
    };
  }

  if (mode === 'imei') {
    request.imeis = parseImeiList(draft.imeiText).valid;
  } else {
    request.quantity = parseWholeNumber(draft.quantity, 0, MAX_QUANTITY) ?? 0;
  }

  const ek = draft.purchasePrice.trim() ? parseMoneyToMinor(draft.purchasePrice) : null;
  if (ek !== null) request.purchasePrice = minorToDecimal(ek);

  if (offerPossible(draft, ctx) && draft.offer.enabled) {
    const o = draft.offer;
    request.offer = {
      supplierSku: o.supplierSku.trim(),
      price: minorToDecimal(parseMoneyToMinor(o.price) ?? 0),
      minOrderQuantity: parseWholeNumber(o.minOrderQuantity, 1, MAX_QUANTITY) ?? 1,
      deliveryDays: parseWholeNumber(o.deliveryDays, 0, MAX_DELIVERY_DAYS) ?? 2,
      condition: o.condition,
      publish: o.publish,
    };
  }
  return request;
}

// ---------------------------------------------------------------- idempotency

export interface EntryAttempt {
  key: string;
  /** Canonical JSON of the request the key was created for. */
  body: string;
}

/**
 * A retry of the same request reuses the pending key (so the API replays instead of receiving stock twice); a changed
 * request gets a new key (the API would answer entry.idempotency_key_reused otherwise).
 */
export function attemptFor(pending: EntryAttempt | null, body: string, newKey: () => string): EntryAttempt {
  return pending && pending.body === body ? pending : { key: newKey(), body };
}

/** After a failed request: keep the key only when the outcome is unknown (network error, 5xx). */
export function keepAttemptAfter(status: number): boolean {
  return status === 0 || status >= 500;
}

export function newEntryKey(): string {
  const c = globalThis.crypto;
  if (c && typeof c.randomUUID === 'function') return c.randomUUID();
  return 'entry-' + Date.now().toString(36) + '-' + Math.random().toString(36).slice(2);
}

// ---------------------------------------------------------------- API errors

/** Step to return to for an API error code or field error key, so the user sees the field to fix. */
export function stepForProblem(code: string | null, fieldKeys: string[]): EntryStep {
  const byCode: Record<string, EntryStep> = {
    'entry.product_creation_not_allowed': 'identify',
    'entry.identifier_conflict': 'identify',
    'product.not_found': 'identify',
    'product.invalid_ean': 'identify',
    'product.identifier_conflict': 'identify',
    'product.incomplete': 'identify',
    'product.identifier_required': 'identify',
    'product.field_too_long': 'identify',
    'product.model_too_long': 'identify',
    'product.too_many_models': 'identify',
    'entry.imei_not_allowed': 'stock',
    'entry.imei_required': 'stock',
    'entry.quantity_mismatch': 'stock',
    'entry.purchase_price': 'stock',
    'unit.imei_in_stock': 'stock',
    'warehouse.not_found': 'stock',
    'stock.quantity': 'stock',
    'entry.serialized_offer_unsupported': 'offer',
    'entry.offers_not_allowed': 'offer',
    'entry.sku_conflict': 'offer',
    'offer.sku_taken': 'offer',
    'offer.price': 'offer',
    'offer.min_quantity': 'offer',
    'offer.delivery_days': 'offer',
    'offer.sku_required': 'offer',
    'offer.archived': 'offer',
  };
  if (code && byCode[code]) return byCode[code];
  const keys = fieldKeys.map((k) => k.toLowerCase());
  if (keys.some((k) => k.startsWith('newproduct') || k === 'product' || k === 'productid')) return 'identify';
  if (keys.some((k) => k.startsWith('imeis') || k === 'quantity' || k === 'purchaseprice' || k === 'warehouseid')) return 'stock';
  if (keys.some((k) => k.startsWith('offer'))) return 'offer';
  return 'summary';
}

// ---------------------------------------------------------------- multi-select (several existing products at once)

/**
 * Selection of lookup matches. It survives new searches (search "akku", tick 2, search "display", tick 1 → 3) and is
 * keyed by product id; the stored match is the snapshot of the search it was ticked in.
 */
export function isSelected(selected: readonly LookupMatch[], productId: string): boolean {
  return selected.some((m) => m.product.id === productId);
}

export function toggleSelection(selected: readonly LookupMatch[], match: LookupMatch): LookupMatch[] {
  return isSelected(selected, match.product.id) ? selected.filter((m) => m.product.id !== match.product.id) : [...selected, match];
}

/** Adds the match unless it is already selected (auto-selection of a single exact EAN match). */
export function addToSelection(selected: readonly LookupMatch[], match: LookupMatch): LookupMatch[] {
  return isSelected(selected, match.product.id) ? [...selected] : [...selected, match];
}

export function removeFromSelection(selected: readonly LookupMatch[], productId: string): LookupMatch[] {
  return selected.filter((m) => m.product.id !== productId);
}

/** Steps of the bulk flow (2+ selected products): one table for all rows, then review + save. */
export type BulkStep = 'rows' | 'review';
export type WizardStep = EntryStep | BulkStep;
export const BULK_STEPS: WizardStep[] = ['identify', 'rows', 'review'];

/** At most this many rows are sent at the same time. */
export const BULK_CONCURRENCY = 3;

export type RowStatus = 'pending' | 'saving' | 'saved' | 'error';

/** One product of the bulk table: its own stock input, EK, offer and send state. Mutable (bound with ngModel). */
export interface BulkRow {
  /** Product id (one row per product). */
  id: string;
  match: LookupMatch;
  stockMode: StockMode;
  quantity: string;
  imeiText: string;
  purchasePrice: string;
  offer: OfferDraft;
  /**
   * Idempotency-Key of this row: created with the row and sent with every (re)send of it, also after the row was
   * edited. A failed request is not recorded by the API, so the key stays usable; if a send with unknown outcome did
   * succeed, the resend is replayed (same data) or refused with entry.idempotency_key_reused (changed data) instead of
   * booking the stock twice.
   */
  key: string;
  status: RowStatus;
  result: ManualEntryResult | null;
  errorCode: string | null;
  /** Translated message of the last failed send. */
  error: string | null;
}

/** Offer prefill of an existing product: its own offer if there is one, else the suggested SKU and 1 / 2 days. */
export function offerPrefill(match: LookupMatch, enabled = false): OfferDraft {
  const existing = match.ownOffers[0];
  if (!existing) return { ...emptyOffer(), enabled, supplierSku: match.suggestedSku };
  return {
    enabled,
    supplierSku: existing.supplierSku,
    price: minorToInput(existing.priceMinor),
    minOrderQuantity: String(existing.minOrderQuantity),
    deliveryDays: String(existing.deliveryDays),
    condition: existing.condition as OfferCondition,
    publish: existing.status === 'Published' || existing.status === 'Draft',
  };
}

export function newBulkRow(match: LookupMatch, newKey: () => string): BulkRow {
  return {
    id: match.product.id,
    match,
    stockMode: match.imeiRequired ? 'imei' : 'quantity',
    quantity: '1',
    imeiText: '',
    purchasePrice: '',
    offer: offerPrefill(match),
    key: newKey(),
    status: 'pending',
    result: null,
    errorCode: null,
    error: null,
  };
}

/**
 * Rows for the current selection, in selection order: rows of products that stay selected keep their input, status
 * and key; newly selected products get a new row; unsaved rows of deselected products are dropped. Saved rows are
 * always kept (they are part of the run's summary).
 */
export function syncRows(rows: readonly BulkRow[], selected: readonly LookupMatch[], newKey: () => string): BulkRow[] {
  const byId = new Map(rows.map((r) => [r.id, r]));
  const out: BulkRow[] = rows.filter((r) => r.status === 'saved' && !isSelected(selected, r.id));
  for (const m of selected) out.push(byId.get(m.product.id) ?? newBulkRow(m, newKey));
  return out;
}

function rowAsDraft(row: BulkRow, warehouseId: string): EntryDraft {
  return {
    productMode: 'existing',
    match: row.match,
    newProduct: emptyNewProduct(),
    warehouseId,
    stockMode: row.stockMode,
    quantity: row.quantity,
    imeiText: row.imeiText,
    purchasePrice: row.purchasePrice,
    offer: row.offer,
  };
}

export function rowPolicy(row: BulkRow): ImeiPolicy {
  return imeiPolicy({ productMode: 'existing', match: row.match, newProduct: emptyNewProduct() }, null);
}

export function rowStockMode(row: BulkRow): StockMode {
  return effectiveStockMode(row.stockMode, rowPolicy(row));
}

/** Same rule as the single entry: offers only with the permission and never for IMEI stock. */
export function rowOfferPossible(row: BulkRow, ctx: EntryContext): boolean {
  return offerPossible(rowAsDraft(row, ''), ctx);
}

/** IMEIs (valid ones) that appear in more than one row, per row id. */
export function crossRowImeiDuplicates(rows: readonly BulkRow[]): Map<string, string[]> {
  const owners = new Map<string, Set<string>>();
  for (const row of rows) {
    if (rowStockMode(row) !== 'imei') continue;
    for (const imei of parseImeiList(row.imeiText).valid) {
      const set = owners.get(imei) ?? new Set<string>();
      set.add(row.id);
      owners.set(imei, set);
    }
  }
  const out = new Map<string, string[]>();
  for (const [imei, ids] of owners) {
    if (ids.size < 2) continue;
    for (const id of ids) out.set(id, [...(out.get(id) ?? []), imei]);
  }
  return out;
}

function skuKey(sku: string): string {
  return sku.trim().toUpperCase();
}

/**
 * Validation of one row (values are i18n keys): stock (quantity or IMEIs), EK and, when enabled and possible, the
 * offer. Cross-row rules: an IMEI may appear in one row only, an offer SKU in one row only. A row must receive stock
 * or carry an offer, otherwise it would save nothing.
 */
export function validateRow(row: BulkRow, rows: readonly BulkRow[], ctx: EntryContext): StepErrors {
  const draft = rowAsDraft(row, '');
  const errors: StepErrors = { ...validateStock(draft, ctx) };
  const offer = rowOfferPossible(row, ctx) && row.offer.enabled;
  if (offer) Object.assign(errors, validateOffer(draft));
  const mode = rowStockMode(row);
  if (mode === 'quantity' && !errors['quantity'] && parseWholeNumber(row.quantity, 0, MAX_QUANTITY) === 0 && !offer) {
    errors['quantity'] = 'inventory.entry.bulk.v.nothing';
  }
  if (mode === 'imei' && !errors['imeis'] && (crossRowImeiDuplicates(rows).get(row.id)?.length ?? 0) > 0) {
    errors['imeis'] = 'inventory.entry.bulk.v.imeiOtherRow';
  }
  if (offer && !errors['supplierSku']) {
    const key = skuKey(row.offer.supplierSku);
    const clash = rows.some((r) => r.id !== row.id && r.offer.enabled && rowOfferPossible(r, ctx) && skuKey(r.offer.supplierSku) === key);
    if (clash) errors['supplierSku'] = 'inventory.entry.bulk.v.skuOtherRow';
  }
  return errors;
}

/** Errors of all rows that are still to be sent (saved rows are final), keyed by row id; empty when all are valid. */
export function validateRows(rows: readonly BulkRow[], ctx: EntryContext): Map<string, StepErrors> {
  const out = new Map<string, StepErrors>();
  for (const row of rows) {
    if (row.status === 'saved') continue;
    const errors = validateRow(row, rows, ctx);
    if (Object.keys(errors).length) out.set(row.id, errors);
  }
  return out;
}

/** Request of one row (validate first); the warehouse is shared by all rows. */
export function buildRowRequest(row: BulkRow, warehouseId: string, ctx: EntryContext): ManualEntryRequest {
  return buildEntryRequest(rowAsDraft(row, warehouseId), ctx);
}

/** Rows a (re)run sends: everything not saved yet. Saved rows are never sent again. */
export function rowsToSend(rows: readonly BulkRow[]): BulkRow[] {
  return rows.filter((r) => r.status !== 'saved');
}

export interface BulkSummary {
  total: number;
  saved: number;
  failed: number;
}

export function bulkSummary(rows: readonly BulkRow[]): BulkSummary {
  return {
    total: rows.length,
    saved: rows.filter((r) => r.status === 'saved').length,
    failed: rows.filter((r) => r.status === 'error').length,
  };
}

/** Rows whose input would be lost by closing the dialog. */
export function hasUnsavedRows(rows: readonly BulkRow[]): boolean {
  return rows.some((r) => r.status !== 'saved');
}

/** "Menge für alle": sets the quantity of every unsaved row stocked by quantity; returns how many rows changed. */
export function setQuantityForAll(rows: readonly BulkRow[], quantity: string): number {
  let changed = 0;
  for (const row of rows) {
    if (row.status === 'saved' || rowStockMode(row) !== 'quantity') continue;
    row.quantity = quantity.trim();
    changed++;
  }
  return changed;
}

/**
 * Sends one row with its own key and records the outcome on the row. `send` performs the request; `describe` turns a
 * failure into the translated message. Never throws.
 */
export async function sendRow(
  row: BulkRow,
  body: ManualEntryRequest,
  send: (body: ManualEntryRequest, key: string) => Promise<ManualEntryResult>,
  describe: (error: unknown) => { code: string | null; text: string },
): Promise<void> {
  row.status = 'saving';
  try {
    row.result = await send(body, row.key);
    row.status = 'saved';
    row.error = null;
    row.errorCode = null;
  } catch (e) {
    const d = describe(e);
    row.status = 'error';
    row.errorCode = d.code;
    row.error = d.text;
  }
}

export interface BulkRunOptions {
  warehouseId: string;
  ctx: EntryContext;
  send: (body: ManualEntryRequest, key: string) => Promise<ManualEntryResult>;
  describe: (error: unknown) => { code: string | null; text: string };
  /** Called whenever a row changed its status (re-render). */
  onChange?: () => void;
  concurrency?: number;
}

/**
 * One save run of the bulk table: sends every row that is not saved yet (the first run all rows, later runs the failed
 * ones), each with its own key, at most BULK_CONCURRENCY at a time — one at a time without a warehouse, because the
 * API then creates the default warehouse and parallel requests would race for it. Returns the last saved result.
 */
export async function runBulk(rows: readonly BulkRow[], options: BulkRunOptions): Promise<ManualEntryResult | null> {
  const toSend = rowsToSend(rows);
  for (const row of toSend) {
    row.status = 'pending';
    row.error = null;
    row.errorCode = null;
  }
  options.onChange?.();
  let last: ManualEntryResult | null = null;
  const limit = options.warehouseId ? (options.concurrency ?? BULK_CONCURRENCY) : 1;
  await runLimited(toSend, limit, async (row) => {
    const sending = sendRow(row, buildRowRequest(row, options.warehouseId, options.ctx), options.send, options.describe);
    options.onChange?.();
    await sending;
    if (row.status === 'saved' && row.result) last = row.result;
    options.onChange?.();
  });
  return last;
}

/** Runs `worker` for every item with at most `limit` in flight; resolves when all are done (workers must not throw). */
export async function runLimited<T>(items: readonly T[], limit: number, worker: (item: T) => Promise<void>): Promise<void> {
  let next = 0;
  const lanes = Array.from({ length: Math.max(1, Math.min(limit, items.length)) }, async () => {
    while (next < items.length) {
      const item = items[next++];
      await worker(item);
    }
  });
  await Promise.all(lanes);
}
