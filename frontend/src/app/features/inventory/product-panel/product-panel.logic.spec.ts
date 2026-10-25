import { CatalogCategories, ModuleName, OwnOffer, Permission, CatalogProductDetail, StockMovement } from '../../../core/api/models';
import {
  AccessContext,
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
  suggestSku,
  validateOfferForm,
  validateProductForm,
} from './product-panel.logic';

function ctx(modules: ModuleName[], permissions: Permission[]): AccessContext {
  return { hasModule: (m) => modules.includes(m), can: (p) => permissions.includes(p) };
}

const OWNER_ALL = ctx(['Inventory', 'ProductImport', 'B2BSales', 'Pos'], [
  'inventory.manage', 'imports.manage', 'offers.manage', 'pos.sell', 'pos.manage',
] as Permission[]);

const product: CatalogProductDetail = {
  id: '0190aaaa-bbbb-7ccc-8ddd-eeeeffff0000',
  brand: 'Apple',
  name: 'iPhone 13 Display',
  category: 'Display',
  quality: 'ServicePack',
  mpn: '661-1',
  ean: '4006381333931',
  modelName: 'iPhone 13',
  compatibleModels: ['iPhone 13', 'iPhone 13 Pro'],
  createdByCurrentOrganization: true,
  canEdit: true,
  version: 42,
};

const categories: CatalogCategories = {
  categories: ['Smartphone', 'Tablet', 'Wearable', 'Display', 'Battery', 'Accessory'],
  qualities: ['NotApplicable', 'Original', 'ServicePack'],
  imeiRequired: ['Smartphone'],
  imeiAllowed: ['Smartphone', 'Tablet', 'Wearable'],
};

function offer(over: Partial<OwnOffer>): OwnOffer {
  return {
    id: 'o1', productId: product.id, brand: 'Apple', productName: 'x', ean: null, mpn: null, quality: 'ServicePack', warehouseId: 'w1',
    supplierSku: 'SKU-1', priceMinor: 4990, currency: 'EUR', minOrderQuantity: 1, deliveryDays: 2, condition: 'New', status: 'Published',
    onHand: 3, reserved: 0, updatedAt: '2026-10-01T00:00:00Z', moderation: { blocked: false, blockedBy: null, reason: null, blockedAt: null },
    ...over,
  } as OwnOffer;
}

describe('panel access (sections by ownership, modules, permissions)', () => {
  it('owner with ProductImport + imports.manage manages images and data and sees every block', () => {
    const a = panelAccess(product, OWNER_ALL);
    expect(a.tabs).toEqual(['images', 'data', 'prices', 'stock']);
    expect(a.manageImages).toBe(true);
    expect(a.editProduct).toBe(true);
    expect(a.readOnlyReason).toBeNull();
    expect(a.posPrice).toBe('edit');
    expect(a.offer).toBe('edit');
    expect(a.adjustStock).toBe(true);
  });

  it('a product of another organization is read-only with the "foreign" hint, own prices stay editable', () => {
    const a = panelAccess({ createdByCurrentOrganization: false, canEdit: false }, OWNER_ALL);
    expect(a.manageImages).toBe(false);
    expect(a.editProduct).toBe(false);
    expect(a.readOnlyReason).toBe('foreign');
    expect(a.posPrice).toBe('edit');
    expect(a.offer).toBe('edit');
  });

  it('own product without the module ProductImport or imports.manage is read-only with the "permission" hint', () => {
    const noModule = ctx(['Inventory'], ['inventory.manage', 'imports.manage'] as Permission[]);
    expect(panelAccess(product, noModule).readOnlyReason).toBe('permission');
    expect(panelAccess(product, noModule).manageImages).toBe(false);
    const noPermission = ctx(['Inventory', 'ProductImport'], ['inventory.manage'] as Permission[]);
    expect(panelAccess(product, noPermission).editProduct).toBe(false);
    // The server flag alone is not enough either (e.g. stale membership in the UI).
    expect(panelAccess({ ...product, canEdit: false }, OWNER_ALL).readOnlyReason).toBe('permission');
  });

  it('POS and offer blocks follow their modules and permissions', () => {
    const staff = ctx(['Inventory', 'Pos', 'B2BSales'], ['pos.sell'] as Permission[]);
    const a = panelAccess(product, staff);
    expect(a.posPrice).toBe('view');
    expect(a.offer).toBe('view');
    expect(a.adjustStock).toBe(false);
    const plain = panelAccess(product, ctx(['Inventory'], ['inventory.manage', 'pos.manage', 'offers.manage'] as Permission[]));
    expect(plain.posPrice).toBeNull();
    expect(plain.offer).toBeNull();
  });

  it('nothing is editable before the product is loaded', () => {
    const a = panelAccess(null, OWNER_ALL);
    expect(a.manageImages).toBe(false);
    expect(a.editProduct).toBe(false);
    expect(a.readOnlyReason).toBeNull();
  });

  it('tab keyboard navigation wraps and mirrors for RTL', () => {
    const tabs = panelAccess(product, OWNER_ALL).tabs;
    expect(nextTab(tabs, 'images', 'ArrowRight')).toBe('data');
    expect(nextTab(tabs, 'images', 'ArrowLeft')).toBe('stock');
    expect(nextTab(tabs, 'images', 'ArrowLeft', true)).toBe('data');
    expect(nextTab(tabs, 'data', 'End')).toBe('stock');
    expect(nextTab(tabs, 'data', 'Home')).toBe('images');
    expect(nextTab(tabs, 'data', 'a')).toBeNull();
  });
});

describe('product data form', () => {
  it('round-trips the product and detects changes', () => {
    const form = productForm(product);
    expect(form.compatibleModels).toBe('iPhone 13\niPhone 13 Pro');
    expect(productDirty(form, product)).toBe(false);
    expect(productDirty({ ...form, name: ' iPhone 13 Display ' }, product)).toBe(false); // trimmed
    expect(productDirty({ ...form, name: 'iPhone 13 Display OLED' }, product)).toBe(true);
  });

  it('builds the PUT body with the version and normalized values', () => {
    const body = productRequest({ ...productForm(product), ean: '4006 3813 3393 1', mpn: '  ', compatibleModels: 'A, B\nb\n' }, product.version);
    expect(body).toEqual({
      brand: 'Apple', name: 'iPhone 13 Display', category: 'Display', quality: 'ServicePack', ean: '4006381333931', mpn: null,
      modelName: 'iPhone 13', compatibleModels: ['A', 'B'], version: 42,
    });
  });

  it('validates like the API', () => {
    const form = productForm(product);
    expect(validateProductForm(form, product, categories)).toEqual({});
    expect(validateProductForm({ ...form, brand: ' ' }, product, categories)['brand']).toBe('inventory.entry.v.required');
    expect(validateProductForm({ ...form, ean: '4006381333932' }, product, categories)['ean']).toBe('inventory.entry.v.ean');
    expect(validateProductForm({ ...form, ean: '', mpn: '' }, product, categories)['identifier']).toBe('inventory.entry.v.identifier');
    expect(validateProductForm({ ...form, category: 'Smartphone' }, product, categories)['category']).toBe('inventory.panel.v.categoryStockMode');
    expect(validateProductForm({ ...form, category: 'Battery' }, product, categories)).toEqual({});
  });

  it('category changes keep the stock mode (mirrors product.category_stock_mode)', () => {
    expect(categoryChangeAllowed('Display', 'Battery', categories)).toBe(true);
    expect(categoryChangeAllowed('Tablet', 'Wearable', categories)).toBe(true);
    expect(categoryChangeAllowed('Smartphone', 'Tablet', categories)).toBe(false);
    expect(categoryChangeAllowed('Display', 'Smartphone', categories)).toBe(false);
    expect(categoryChangeAllowed('Display', 'Smartphone', null)).toBe(true); // unknown → the API decides
  });

  it('maps API codes to fields', () => {
    expect(productErrorField('product.ean_taken')).toBe('ean');
    expect(productErrorField('product.identity_taken')).toBe('mpn');
    expect(productErrorField('product.category_stock_mode')).toBe('category');
    expect(productErrorField('concurrency')).toBeNull();
  });
});

describe('offer', () => {
  it('suggests the SKU like the API (MPN, EAN, HN-id)', () => {
    expect(suggestSku(product)).toBe('661-1');
    expect(suggestSku({ ...product, mpn: null })).toBe('4006381333931');
    expect(suggestSku({ ...product, mpn: null, ean: null })).toBe('HN-0190AAAABB');
  });

  it('picks the offer of the row warehouse, else any active one, else an archived one of the warehouse', () => {
    const a = offer({ id: 'a', warehouseId: 'w2' });
    const b = offer({ id: 'b', warehouseId: 'w1' });
    const archived = offer({ id: 'c', warehouseId: 'w1', status: 'Archived' });
    expect(pickOffer([a, b], 'w1')?.id).toBe('b');
    expect(pickOffer([archived, a], 'w1')?.id).toBe('a');
    expect(pickOffer([archived], 'w1')?.id).toBe('c');
    expect(pickOffer([], 'w1')).toBeNull();
  });

  it('prefills, validates and builds requests in cents-safe decimals', () => {
    const existing = offer({ priceMinor: 4990, minOrderQuantity: 2, deliveryDays: 3 });
    const form = offerForm(existing, product);
    expect(form).toMatchObject({ sku: 'SKU-1', price: '49,90', minOrderQuantity: '2', deliveryDays: '3', status: '' });
    expect(offerUpdateRequest({ ...form, price: '59,95' }, existing)).toEqual({
      price: 59.95, minOrderQuantity: 2, deliveryDays: 3, condition: 'New', status: null,
    });
    expect(offerUpdateRequest({ ...form, status: 'Paused' }, existing).status).toBe('Paused');
    expect(validateOfferForm({ ...form, price: '0' }, false)['price']).toBe('inventory.entry.v.price');
    expect(validateOfferForm({ ...form, minOrderQuantity: '0' }, false)['minOrderQuantity']).toBe('inventory.entry.v.moq');
    expect(validateOfferForm({ ...form, deliveryDays: '61' }, false)['deliveryDays']).toBe('inventory.entry.v.deliveryDays');

    const fresh = offerForm(null, product);
    expect(fresh).toMatchObject({ sku: '661-1', price: '', publish: true });
    expect(validateOfferForm({ ...fresh, sku: '' }, true)['supplierSku']).toBe('inventory.entry.v.required');
    expect(offerCreateRequest({ ...fresh, price: '1.234,50' }, product.id, 'w1')).toEqual({
      productId: product.id, warehouseId: 'w1', supplierSku: '661-1', price: 1234.5, minOrderQuantity: 1, deliveryDays: 2, condition: 'New', publish: true,
    });
    expect(offerStatusChoices(existing)).toEqual(['Paused', 'Archived']);
  });
});

describe('POS price, purchase price, totals', () => {
  it('parses the gross POS price to cents', () => {
    expect(parsePosPrice('24,90')).toBe(2490);
    expect(parsePosPrice('0')).toBeNull();
    expect(parsePosPrice('abc')).toBeNull();
  });

  it('takes the newest goods receipt with a purchase price', () => {
    const m = (id: number, unitCostMinor: number | null): StockMovement => ({
      id, warehouseId: 'w1', productId: product.id, quantity: 1, onHandAfter: 1, type: 'Receipt', reference: null,
      occurredAt: `2026-10-0${id}T00:00:00Z`, unitCostMinor,
    });
    expect(latestPurchasePrice([m(3, null), m(2, 1250), m(1, 990)])).toEqual({ unitCostMinor: 1250, occurredAt: '2026-10-02T00:00:00Z', warehouseId: 'w1' });
    expect(latestPurchasePrice([m(1, null)])).toBeNull();
  });

  it('sums stock over warehouses', () => {
    expect(stockTotals([{ onHand: 3, reserved: 1, available: 2 }, { onHand: 5, reserved: 0, available: 5 }])).toEqual({ onHand: 8, reserved: 1, available: 7 });
  });
});
