import { CatalogCategories, LookupMatch, ManualEntryRequest, OfferCondition } from '../../../core/api/models';
import { minorToDecimal, parseMoneyToMinor } from '../../../core/format/money';

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
