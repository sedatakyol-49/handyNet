import {
  CatalogCategories,
  CreateOfferRequest,
  ModuleName,
  OfferCondition,
  OwnOffer,
  Permission,
  CatalogProductDetail,
  StockMovement,
  UpdateOfferRequest,
  UpdateProductRequest,
} from '../../../core/api/models';
import { minorToDecimal, minorToInput, parseMoneyToMinor } from '../../../core/format/money';
import { MAX_COMPATIBLE_MODELS, MAX_DELIVERY_DAYS, isValidGtin, parseModelList, parseWholeNumber } from '../manual-entry/manual-entry.logic';

/**
 * Pure rules of the Lager product panel (one stock row → product data, images, prices/offer, stock). The API stays
 * authoritative; these only decide what is shown and validate before sending.
 */

export type PanelTab = 'images' | 'data' | 'prices' | 'stock';

export interface AccessContext {
  hasModule(module: ModuleName): boolean;
  can(permission: Permission): boolean;
}

export interface PanelAccess {
  tabs: PanelTab[];
  /** Upload/reorder/remove images (creating organization with ProductImport + imports.manage, like the API). */
  manageImages: boolean;
  /** Change brand, name, category, … (same rule as images). */
  editProduct: boolean;
  /**
   * Why images and data are read-only: `foreign` = the product belongs to the shared catalog of another organization;
   * `permission` = own product, but the module ProductImport or the permission imports.manage is missing; null = editable
   * (or the product is not loaded yet).
   */
  readOnlyReason: 'foreign' | 'permission' | null;
  /** Own POS price: module Pos; view with pos.sell, edit with pos.manage. */
  posPrice: 'edit' | 'view' | null;
  /** Own marketplace offer: module B2BSales; view always, create/edit with offers.manage. */
  offer: 'edit' | 'view' | null;
  /** "Korrigieren" (inventory.manage). */
  adjustStock: boolean;
}

export function panelAccess(product: Pick<CatalogProductDetail, 'createdByCurrentOrganization' | 'canEdit'> | null, ctx: AccessContext): PanelAccess {
  const mayManageCatalog = ctx.hasModule('ProductImport') && ctx.can('imports.manage');
  const editable = !!product && product.canEdit && product.createdByCurrentOrganization && mayManageCatalog;
  const readOnlyReason = !product || editable ? null : product.createdByCurrentOrganization ? 'permission' : 'foreign';
  const pos = ctx.hasModule('Pos') && (ctx.can('pos.sell') || ctx.can('pos.manage')) ? (ctx.can('pos.manage') ? 'edit' : 'view') : null;
  const offer = ctx.hasModule('B2BSales') ? (ctx.can('offers.manage') ? 'edit' : 'view') : null;
  return {
    // Purchase prices (EK from goods receipts) are always part of "Preise", so all four tabs exist; the blocks inside
    // depend on the modules/permissions.
    tabs: ['images', 'data', 'prices', 'stock'],
    manageImages: editable,
    editProduct: editable,
    readOnlyReason,
    posPrice: pos,
    offer,
    adjustStock: ctx.can('inventory.manage'),
  };
}

/** Arrow keys / Home / End move between tabs (mirrored for RTL like the visual order). */
export function nextTab(tabs: PanelTab[], current: PanelTab, key: string, rtl = false): PanelTab | null {
  const i = tabs.indexOf(current);
  if (i < 0 || tabs.length === 0) return null;
  const forward = rtl ? 'ArrowLeft' : 'ArrowRight';
  const backward = rtl ? 'ArrowRight' : 'ArrowLeft';
  if (key === 'Home') return tabs[0];
  if (key === 'End') return tabs[tabs.length - 1];
  if (key === forward) return tabs[(i + 1) % tabs.length];
  if (key === backward) return tabs[(i - 1 + tabs.length) % tabs.length];
  return null;
}

// ---------------------------------------------------------------- product data

export interface ProductForm {
  brand: string;
  name: string;
  category: string;
  quality: string;
  ean: string;
  mpn: string;
  modelName: string;
  /** One model per line (or comma separated). */
  compatibleModels: string;
}

export type FormErrors = Partial<Record<string, string>>;

export function productForm(p: CatalogProductDetail): ProductForm {
  return {
    brand: p.brand,
    name: p.name,
    category: p.category,
    quality: p.quality,
    ean: p.ean ?? '',
    mpn: p.mpn ?? '',
    modelName: p.modelName ?? '',
    compatibleModels: (p.compatibleModels ?? []).join('\n'),
  };
}

/**
 * Mirrors the API rule `product.category_stock_mode`: the category may only change to one that is stocked the same
 * way (IMEI required / IMEI allowed / quantity only).
 */
export function categoryChangeAllowed(from: string, to: string, categories: CatalogCategories | null): boolean {
  if (from === to || !categories) return true;
  const required = (c: string) => categories.imeiRequired.includes(c);
  const allowed = (c: string) => categories.imeiAllowed.includes(c);
  return required(from) === required(to) && allowed(from) === allowed(to);
}

/** Client checks with the same limits as the API (keys under inventory.entry.v / inventory.panel.v). */
export function validateProductForm(form: ProductForm, original: CatalogProductDetail, categories: CatalogCategories | null): FormErrors {
  const errors: FormErrors = {};
  if (!form.brand.trim()) errors['brand'] = 'inventory.entry.v.required';
  else if (form.brand.trim().length > 100) errors['brand'] = 'inventory.entry.v.tooLong';
  if (!form.name.trim()) errors['name'] = 'inventory.entry.v.required';
  else if (form.name.trim().length > 300) errors['name'] = 'inventory.entry.v.tooLong';
  if (!form.category || (categories && !categories.categories.includes(form.category))) errors['category'] = 'inventory.entry.v.required';
  else if (!categoryChangeAllowed(original.category, form.category, categories)) errors['category'] = 'inventory.panel.v.categoryStockMode';
  if (form.ean.trim() && !isValidGtin(form.ean)) errors['ean'] = 'inventory.entry.v.ean';
  if (form.mpn.trim().length > 100) errors['mpn'] = 'inventory.entry.v.tooLong';
  if (!form.ean.trim() && !form.mpn.trim()) errors['identifier'] = 'inventory.entry.v.identifier';
  if (form.modelName.trim().length > 200) errors['modelName'] = 'inventory.entry.v.tooLong';
  const models = parseModelList(form.compatibleModels);
  if (models.length > MAX_COMPATIBLE_MODELS) errors['compatibleModels'] = 'inventory.entry.v.tooManyModels';
  else if (models.some((m) => m.length > 200)) errors['compatibleModels'] = 'inventory.entry.v.tooLong';
  return errors;
}

export function productRequest(form: ProductForm, version: number): UpdateProductRequest {
  const models = parseModelList(form.compatibleModels);
  const text = (s: string) => (s.trim() ? s.trim() : null);
  return {
    brand: form.brand.trim(),
    name: form.name.trim(),
    category: form.category,
    quality: form.quality || 'NotApplicable',
    ean: text(form.ean.replace(/ /g, '')),
    mpn: text(form.mpn),
    modelName: text(form.modelName),
    compatibleModels: models.length ? models : null,
    version,
  };
}

/** Whether the form differs from the loaded product (Save enabled, "discard changes?"). */
export function productDirty(form: ProductForm, p: CatalogProductDetail): boolean {
  const a = productRequest(form, p.version);
  const b = productRequest(productForm(p), p.version);
  return JSON.stringify(a) !== JSON.stringify(b);
}

/** API error code → form field it belongs to (so the message appears next to the input). */
export function productErrorField(code: string | null): string | null {
  switch (code) {
    case 'product.ean_taken':
    case 'product.invalid_ean':
      return 'ean';
    case 'product.identity_taken':
      return 'mpn';
    case 'product.identifier_required':
      return 'identifier';
    case 'product.category_stock_mode':
      return 'category';
    case 'product.model_too_long':
    case 'product.too_many_models':
      return 'compatibleModels';
    default:
      return null;
  }
}

// ---------------------------------------------------------------- offer

export interface OfferForm {
  sku: string;
  price: string;
  minOrderQuantity: string;
  deliveryDays: string;
  condition: OfferCondition;
  /** Edit: '' keeps the current status. Create: ignored (see publish). */
  status: '' | 'Published' | 'Paused' | 'Archived';
  publish: boolean;
}

/** Same rule as the API's manual entry suggestion: MPN, else EAN, else HN-<id prefix>. */
export function suggestSku(p: Pick<CatalogProductDetail, 'id' | 'mpn' | 'ean'>): string {
  if (p.mpn) return p.mpn;
  if (p.ean) return p.ean;
  return `HN-${p.id.replace(/-/g, '').slice(0, 10).toUpperCase()}`;
}

/**
 * The offer the panel edits: a non-archived offer from the row's warehouse, else any non-archived one, else an archived
 * one of the row's warehouse (it can be re-published). Null = no offer yet (create).
 */
export function pickOffer(offers: OwnOffer[], warehouseId: string): OwnOffer | null {
  const active = offers.filter((o) => o.status !== 'Archived');
  return (
    active.find((o) => o.warehouseId === warehouseId) ??
    active[0] ??
    offers.find((o) => o.warehouseId === warehouseId) ??
    null
  );
}

export function offerForm(offer: OwnOffer | null, product: Pick<CatalogProductDetail, 'id' | 'mpn' | 'ean'>): OfferForm {
  if (!offer) {
    return { sku: suggestSku(product), price: '', minOrderQuantity: '1', deliveryDays: '2', condition: 'New', status: '', publish: true };
  }
  return {
    sku: offer.supplierSku,
    price: minorToInput(offer.priceMinor),
    minOrderQuantity: String(offer.minOrderQuantity),
    deliveryDays: String(offer.deliveryDays),
    condition: offer.condition,
    status: '',
    publish: offer.status === 'Published',
  };
}

export function validateOfferForm(form: OfferForm, creating: boolean): FormErrors {
  const errors: FormErrors = {};
  if (creating && !form.sku.trim()) errors['supplierSku'] = 'inventory.entry.v.required';
  else if (form.sku.trim().length > 100) errors['supplierSku'] = 'inventory.entry.v.tooLong';
  const price = parseMoneyToMinor(form.price);
  if (price === null || price <= 0) errors['price'] = 'inventory.entry.v.price';
  if (parseWholeNumber(form.minOrderQuantity, 1, 1_000_000) === null) errors['minOrderQuantity'] = 'inventory.entry.v.moq';
  if (parseWholeNumber(form.deliveryDays, 0, MAX_DELIVERY_DAYS) === null) errors['deliveryDays'] = 'inventory.entry.v.deliveryDays';
  return errors;
}

/** Offer prices go to the API as decimals (EUR); parsed to cents first, so no float input reaches it. */
export function offerUpdateRequest(form: OfferForm, current: OwnOffer): UpdateOfferRequest {
  return {
    price: minorToDecimal(parseMoneyToMinor(form.price) ?? 0),
    minOrderQuantity: Number(form.minOrderQuantity),
    deliveryDays: Number(form.deliveryDays),
    condition: form.condition,
    status: form.status === '' || form.status === current.status ? null : form.status,
  };
}

export function offerCreateRequest(form: OfferForm, productId: string, warehouseId: string): CreateOfferRequest {
  return {
    productId,
    warehouseId,
    supplierSku: form.sku.trim(),
    price: minorToDecimal(parseMoneyToMinor(form.price) ?? 0),
    minOrderQuantity: Number(form.minOrderQuantity),
    deliveryDays: Number(form.deliveryDays),
    condition: form.condition,
    publish: form.publish,
  };
}

/** Status choices when editing: the current one is kept by ''. Draft offers can only be published (or kept). */
export function offerStatusChoices(current: OwnOffer): ('Published' | 'Paused' | 'Archived')[] {
  const all: ('Published' | 'Paused' | 'Archived')[] = ['Published', 'Paused', 'Archived'];
  return all.filter((s) => s !== current.status);
}

// ---------------------------------------------------------------- POS price and purchase price

/** POS price input (gross, e.g. "24,90") → cents; null when invalid or ≤ 0. */
export function parsePosPrice(input: string): number | null {
  const minor = parseMoneyToMinor(input);
  return minor !== null && minor > 0 ? minor : null;
}

export interface PurchasePrice {
  unitCostMinor: number;
  occurredAt: string;
  warehouseId: string;
}

/** Latest goods receipt with a purchase price (movements come newest first from the API). */
export function latestPurchasePrice(movements: StockMovement[]): PurchasePrice | null {
  const hit = movements.find((m) => m.unitCostMinor !== null && m.unitCostMinor !== undefined);
  return hit ? { unitCostMinor: hit.unitCostMinor!, occurredAt: hit.occurredAt, warehouseId: hit.warehouseId } : null;
}

export interface StockTotals {
  onHand: number;
  reserved: number;
  available: number;
}

export function stockTotals(rows: { onHand: number; reserved: number; available: number }[]): StockTotals {
  return rows.reduce((t, r) => ({ onHand: t.onHand + r.onHand, reserved: t.reserved + r.reserved, available: t.available + r.available }), {
    onHand: 0,
    reserved: 0,
    available: 0,
  });
}
