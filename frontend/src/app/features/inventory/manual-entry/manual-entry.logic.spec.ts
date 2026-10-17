import { CatalogCategories, LookupMatch } from '../../../core/api/models';
import {
  EntryContext,
  EntryDraft,
  attemptFor,
  buildEntryRequest,
  emptyDraft,
  emptyNewProduct,
  entrySteps,
  firstInvalidStep,
  imeiPolicy,
  isValidGtin,
  isValidImei,
  keepAttemptAfter,
  parseImeiList,
  parseModelList,
  stepForProblem,
  validateIdentify,
  validateOffer,
  validateStock,
} from './manual-entry.logic';

const VALID_IMEI = '490154203237518';
const VALID_IMEI_2 = '356938035643809';

const categories: CatalogCategories = {
  categories: ['Smartphone', 'Tablet', 'Display', 'Accessory'],
  qualities: ['NotApplicable', 'Original'],
  imeiRequired: ['Smartphone'],
  imeiAllowed: ['Smartphone', 'Tablet'],
};

const ctx: EntryContext = { categories, canCreateProduct: true, canCreateOffer: true };

function match(overrides: Partial<LookupMatch> = {}): LookupMatch {
  return {
    product: { id: 'p1', brand: 'Apple', name: 'Display iPhone 13', category: 'Display', quality: 'Original', mpn: 'A2633', ean: null, modelName: null, compatibleModels: [] },
    matchedBy: 'Mpn',
    imageCount: 0,
    primaryThumbnailUrl: null,
    canManageImages: false,
    imeiRequired: false,
    imeiAllowed: false,
    ownOnHand: 0,
    ownAvailable: 0,
    ownSerializedUnits: 0,
    ownOffers: [],
    suggestedSku: 'A2633',
    ...overrides,
  };
}

function newDraft(patch: Partial<EntryDraft['newProduct']> = {}): EntryDraft {
  const d = emptyDraft('wh1');
  d.productMode = 'new';
  d.newProduct = emptyNewProduct({ brand: 'Apple', name: 'Display iPhone 13', category: 'Display', ean: '4006381333931', ...patch });
  return d;
}

describe('IMEI parsing', () => {
  it('checks 15 digits and the Luhn check digit', () => {
    expect(isValidImei(VALID_IMEI)).toBe(true);
    expect(isValidImei('49-015420-323751-8')).toBe(true);
    expect(isValidImei('490154203237519')).toBe(false);
    expect(isValidImei('49015420323751')).toBe(false);
  });

  it('reports invalid lines and duplicates and keeps valid IMEIs in input order', () => {
    const list = parseImeiList(`${VALID_IMEI}\n\n 1234 \n${VALID_IMEI_2}\n490154203237519\n${VALID_IMEI}`);
    expect(list.valid).toEqual([VALID_IMEI, VALID_IMEI_2]);
    expect(list.invalid).toBe(2);
    expect(list.duplicates).toBe(1);
    expect(list.entries.map((e) => [e.line, e.status])).toEqual([
      [1, 'ok'],
      [3, 'length'],
      [4, 'ok'],
      [5, 'checksum'],
      [6, 'duplicate'],
    ]);
  });

  it('accepts comma/semicolon separated scanner input', () => {
    expect(parseImeiList(`${VALID_IMEI};${VALID_IMEI_2}`).valid).toHaveLength(2);
  });
});

describe('GTIN and model list', () => {
  it('validates the GTIN check digit for 8/12/13/14 digits', () => {
    expect(isValidGtin('4006381333931')).toBe(true);
    expect(isValidGtin('4006381333932')).toBe(false);
    expect(isValidGtin('96385074')).toBe(true);
    expect(isValidGtin('123')).toBe(false);
  });

  it('splits compatible models by line/comma without duplicates', () => {
    expect(parseModelList('iPhone 13\niPhone 13 Pro, iphone 13 ,')).toEqual(['iPhone 13', 'iPhone 13 Pro']);
  });
});

describe('IMEI policy and steps', () => {
  it('derives the policy from the new product category', () => {
    expect(imeiPolicy(newDraft({ category: 'Smartphone' }), categories)).toBe('required');
    expect(imeiPolicy(newDraft({ category: 'Tablet' }), categories)).toBe('optional');
    expect(imeiPolicy(newDraft({ category: 'Display' }), categories)).toBe('none');
  });

  it('derives the policy from the chosen match', () => {
    const d = emptyDraft();
    d.productMode = 'existing';
    d.match = match({ imeiRequired: true, imeiAllowed: true });
    expect(imeiPolicy(d, categories)).toBe('required');
  });

  it('hides the offer step without offer permission or for IMEI stock', () => {
    expect(entrySteps(newDraft(), ctx)).toEqual(['identify', 'stock', 'offer', 'summary']);
    expect(entrySteps(newDraft(), { ...ctx, canCreateOffer: false })).toEqual(['identify', 'stock', 'summary']);
    expect(entrySteps(newDraft({ category: 'Smartphone' }), ctx)).toEqual(['identify', 'stock', 'summary']);
    const tablet = newDraft({ category: 'Tablet' });
    tablet.stockMode = 'imei';
    expect(entrySteps(tablet, ctx)).not.toContain('offer');
  });
});

describe('step validation', () => {
  it('requires a choice or a complete new product', () => {
    expect(validateIdentify(emptyDraft(), ctx)).toEqual({ product: 'inventory.entry.v.chooseProduct' });
    expect(validateIdentify(newDraft(), ctx)).toEqual({});
    expect(validateIdentify(newDraft(), { ...ctx, canCreateProduct: false })).toEqual({ product: 'inventory.entry.v.noProductCreation' });
    const errors = validateIdentify(newDraft({ brand: ' ', category: '', ean: '4006381333932' }), ctx);
    expect(Object.keys(errors).sort()).toEqual(['brand', 'category', 'ean']);
    expect(validateIdentify(newDraft({ ean: '', mpn: '' }), ctx)).toEqual({ identifier: 'inventory.entry.v.identifier' });
  });

  it('validates quantity, IMEIs and EK', () => {
    const d = newDraft();
    d.quantity = '2,5';
    d.purchasePrice = 'abc';
    expect(validateStock(d, ctx)).toEqual({ quantity: 'inventory.entry.v.quantity', purchasePrice: 'inventory.entry.v.money' });
    const phone = newDraft({ category: 'Smartphone' });
    expect(validateStock(phone, ctx)).toEqual({ imeis: 'inventory.entry.v.imeiMissing' });
    phone.imeiText = `${VALID_IMEI}\n${VALID_IMEI}`;
    expect(validateStock(phone, ctx)).toEqual({ imeis: 'inventory.entry.v.imeiDuplicate' });
    phone.imeiText = `${VALID_IMEI}\n490154203237519`;
    expect(validateStock(phone, ctx)).toEqual({ imeis: 'inventory.entry.v.imeiInvalid' });
  });

  it('validates the offer only when enabled', () => {
    const d = newDraft();
    expect(validateOffer(d)).toEqual({});
    d.offer = { ...d.offer, enabled: true, supplierSku: '', price: '0', minOrderQuantity: '0', deliveryDays: '61' };
    expect(Object.keys(validateOffer(d)).sort()).toEqual(['deliveryDays', 'minOrderQuantity', 'price', 'supplierSku']);
  });

  it('finds the first invalid step', () => {
    const d = newDraft();
    d.offer = { ...d.offer, enabled: true, supplierSku: 'SKU-1', price: '' };
    expect(firstInvalidStep(d, ctx)).toBe('offer');
    d.quantity = '-1';
    expect(firstInvalidStep(d, ctx)).toBe('stock');
    d.offer.price = '19,90';
    d.quantity = '5';
    expect(firstInvalidStep(d, ctx)).toBeNull();
  });
});

describe('request', () => {
  it('builds a new-product request with quantity, EK and offer in cents-safe decimals', () => {
    const d = newDraft({ compatibleModels: 'iPhone 13\niPhone 13 Pro', mpn: ' ' });
    d.quantity = '5';
    d.purchasePrice = '8,10';
    d.offer = { enabled: true, supplierSku: ' SKU-1 ', price: '19,90', minOrderQuantity: '2', deliveryDays: '1', condition: 'New', publish: true };
    expect(buildEntryRequest(d, ctx)).toEqual({
      warehouseId: 'wh1',
      newProduct: {
        brand: 'Apple',
        name: 'Display iPhone 13',
        category: 'Display',
        quality: 'NotApplicable',
        ean: '4006381333931',
        mpn: null,
        modelName: null,
        compatibleModels: ['iPhone 13', 'iPhone 13 Pro'],
      },
      quantity: 5,
      purchasePrice: 8.1,
      offer: { supplierSku: 'SKU-1', price: 19.9, minOrderQuantity: 2, deliveryDays: 1, condition: 'New', publish: true },
    });
  });

  it('sends IMEIs instead of a quantity and never an offer for IMEI stock', () => {
    const d = emptyDraft('');
    d.productMode = 'existing';
    d.match = match({ imeiRequired: true, imeiAllowed: true });
    d.imeiText = `${VALID_IMEI}\n${VALID_IMEI_2}`;
    d.offer = { ...d.offer, enabled: true, supplierSku: 'X', price: '10' };
    expect(buildEntryRequest(d, ctx)).toEqual({ warehouseId: null, productId: 'p1', imeis: [VALID_IMEI, VALID_IMEI_2] });
  });
});

describe('idempotency key', () => {
  it('reuses the pending key for the same request and creates a new one for a changed request', () => {
    let n = 0;
    const key = () => `k${++n}`;
    const first = attemptFor(null, '{"q":1}', key);
    expect(first.key).toBe('k1');
    expect(attemptFor(first, '{"q":1}', key)).toBe(first);
    expect(attemptFor(first, '{"q":2}', key).key).toBe('k2');
  });

  it('keeps the key only when the outcome is unknown', () => {
    expect(keepAttemptAfter(0)).toBe(true);
    expect(keepAttemptAfter(503)).toBe(true);
    expect(keepAttemptAfter(409)).toBe(false);
    expect(keepAttemptAfter(422)).toBe(false);
  });
});

describe('API error → step', () => {
  it('maps codes and field keys to the step to fix', () => {
    expect(stepForProblem('unit.imei_in_stock', [])).toBe('stock');
    expect(stepForProblem('entry.sku_conflict', [])).toBe('offer');
    expect(stepForProblem('entry.identifier_conflict', [])).toBe('identify');
    expect(stepForProblem('validation.failed', ['newproduct.brand'])).toBe('identify');
    expect(stepForProblem('validation.failed', ['imeis.0'])).toBe('stock');
    expect(stepForProblem('validation.failed', ['offer.price'])).toBe('offer');
    expect(stepForProblem(null, [])).toBe('summary');
  });
});
