import { HttpErrorResponse } from '@angular/common/http';
import { toApiProblem } from '../../../core/api/api-problem';
import { CatalogCategories, LookupMatch, ManualEntryRequest, ManualEntryResult } from '../../../core/api/models';
import {
  BulkRow,
  EntryContext,
  EntryDraft,
  addToSelection,
  attemptFor,
  buildEntryRequest,
  buildRowRequest,
  bulkSummary,
  crossRowImeiDuplicates,
  emptyDraft,
  emptyNewProduct,
  emptyOffer,
  hasUnsavedRows,
  isSelected,
  removeFromSelection,
  rowOfferPossible,
  rowsToSend,
  runBulk,
  setQuantityForAll,
  syncRows,
  toggleSelection,
  validateRow,
  validateRows,
  entrySteps,
  firstInvalidStep,
  imeiPolicy,
  isValidGtin,
  isValidImei,
  keepAttemptAfter,
  parseImeiList,
  parseModelList,
  stepForProblem,
  imeiProblems,
  imeiProblemKey,
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

// ---------------------------------------------------------------- multi-select / bulk

function product(id: string, name: string, overrides: Partial<LookupMatch> = {}): LookupMatch {
  return match({ product: { ...match().product, id, name, mpn: `MPN-${id}` }, suggestedSku: `MPN-${id}`, ...overrides });
}

const akku1 = product('a1', 'Akku iPhone 12');
const akku2 = product('a2', 'Akku iPhone 13');
const display = product('d1', 'Display iPhone 13');
const phone = product('ph', 'iPhone 13', { imeiRequired: true, imeiAllowed: true });
const phone2 = product('ph2', 'iPhone 14', { imeiRequired: true, imeiAllowed: true });

function keys(): () => string {
  let n = 0;
  return () => `key-${++n}`;
}

function rowsFor(selected: LookupMatch[]): BulkRow[] {
  return syncRows([], selected, keys());
}

function result(id: string, qty: number): ManualEntryResult {
  return { entryId: `e-${id}`, quantityReceived: qty, product: { id }, stock: { onHand: qty } } as unknown as ManualEntryResult;
}

function problem(status: number, code: string): HttpErrorResponse {
  return new HttpErrorResponse({ status, error: { status, code, title: code } });
}

describe('selection across searches', () => {
  it('keeps ticked products of earlier searches and toggles by product id', () => {
    // search "akku": tick 2
    let selected = toggleSelection([], akku1);
    selected = toggleSelection(selected, akku2);
    // search "display": tick 1 – the akku matches are not in this result, they stay selected
    selected = toggleSelection(selected, display);
    expect(selected.map((m) => m.product.id)).toEqual(['a1', 'a2', 'd1']);
    // ticking a match of the same product again (other search snapshot) unticks it
    selected = toggleSelection(selected, product('a2', 'Akku iPhone 13 (other search)'));
    expect(selected.map((m) => m.product.id)).toEqual(['a1', 'd1']);
    expect(isSelected(selected, 'a1')).toBe(true);
    expect(isSelected(selected, 'a2')).toBe(false);
    expect(removeFromSelection(selected, 'a1').map((m) => m.product.id)).toEqual(['d1']);
  });

  it('auto-selection of an exact EAN match never adds a product twice', () => {
    const selected = addToSelection([akku1], display);
    expect(addToSelection(selected, display).map((m) => m.product.id)).toEqual(['a1', 'd1']);
  });

  it('syncs rows with the selection: keeps input and key, adds new, drops deselected unsaved, keeps saved rows', () => {
    const newKey = keys();
    let rows = syncRows([], [akku1, akku2], newKey);
    expect(rows.map((r) => [r.id, r.key])).toEqual([['a1', 'key-1'], ['a2', 'key-2']]);
    rows[0].quantity = '5';
    rows[1].status = 'saved';
    rows = syncRows(rows, [akku1, display], newKey);
    expect(rows.map((r) => [r.id, r.key, r.status])).toEqual([
      ['a2', 'key-2', 'saved'],
      ['a1', 'key-1', 'pending'],
      ['d1', 'key-3', 'pending'],
    ]);
    expect(rows.find((r) => r.id === 'a1')!.quantity).toBe('5');
  });

  it('prefills the row offer from the own offer or the suggested SKU with 1 / 2', () => {
    const [plain] = rowsFor([akku1]);
    expect(plain.offer).toEqual({ ...emptyOffer(), supplierSku: 'MPN-a1' });
    const withOffer = product('o1', 'Akku X', {
      ownOffers: [{ id: 'of', supplierSku: 'OWN-1', priceMinor: 1990, minOrderQuantity: 5, deliveryDays: 3, condition: 'Refurbished', status: 'Paused' }],
    });
    expect(rowsFor([withOffer])[0].offer).toEqual({
      enabled: false, supplierSku: 'OWN-1', price: '19,90', minOrderQuantity: '5', deliveryDays: '3', condition: 'Refurbished', publish: false,
    });
    expect(rowsFor([phone])[0].stockMode).toBe('imei');
  });
});

describe('bulk row validation', () => {
  it('validates quantity and EK per row and requires stock or an offer', () => {
    const rows = rowsFor([akku1, akku2]);
    rows[0].quantity = '2,5';
    rows[0].purchasePrice = 'x';
    rows[1].quantity = '0';
    const errors = validateRows(rows, ctx);
    expect(errors.get('a1')).toEqual({ quantity: 'inventory.entry.v.quantity', purchasePrice: 'inventory.entry.v.money' });
    expect(errors.get('a2')).toEqual({ quantity: 'inventory.entry.bulk.v.nothing' });
    // quantity 0 is fine with an offer (only the offer is saved)
    rows[1].offer = { ...rows[1].offer, enabled: true, price: '9,90' };
    expect(validateRow(rows[1], rows, ctx)).toEqual({});
  });

  it('validates the offer per row only when enabled, and an SKU only once across rows', () => {
    const rows = rowsFor([akku1, akku2]);
    rows[0].offer = { ...rows[0].offer, enabled: true, price: '0', minOrderQuantity: '0', deliveryDays: '61' };
    expect(Object.keys(validateRow(rows[0], rows, ctx)).sort()).toEqual(['deliveryDays', 'minOrderQuantity', 'price']);
    rows[0].offer = { ...rows[0].offer, price: '10', minOrderQuantity: '1', deliveryDays: '2', supplierSku: 'same' };
    rows[1].offer = { ...rows[1].offer, enabled: true, price: '11', supplierSku: ' SAME ' };
    expect(validateRow(rows[0], rows, ctx)).toEqual({ supplierSku: 'inventory.entry.bulk.v.skuOtherRow' });
    expect(validateRow(rows[1], rows, ctx)).toEqual({ supplierSku: 'inventory.entry.bulk.v.skuOtherRow' });
    // without offer permission the offer is neither validated nor sent
    expect(validateRow(rows[0], rows, { ...ctx, canCreateOffer: false })).toEqual({});
    expect(buildRowRequest(rows[0], 'wh1', { ...ctx, canCreateOffer: false })).toEqual({ warehouseId: 'wh1', productId: 'a1', quantity: 1 });
  });

  it('requires IMEIs for serialized rows, never an offer there, and flags IMEIs entered in two rows', () => {
    const rows = rowsFor([phone, phone2, akku1]);
    expect(validateRow(rows[0], rows, ctx)).toEqual({ imeis: 'inventory.entry.v.imeiMissing' });
    rows[0].imeiText = `${VALID_IMEI}\n${VALID_IMEI_2}`;
    rows[1].imeiText = VALID_IMEI_2;
    expect(crossRowImeiDuplicates(rows)).toEqual(new Map([['ph', [VALID_IMEI_2]], ['ph2', [VALID_IMEI_2]]]));
    expect(validateRows(rows, ctx)).toEqual(
      new Map([
        ['ph', { imeis: 'inventory.entry.bulk.v.imeiOtherRow' }],
        ['ph2', { imeis: 'inventory.entry.bulk.v.imeiOtherRow' }],
      ]),
    );
    rows[1].imeiText = '490154203237519'; // Luhn error wins over the cross-row check
    expect(validateRow(rows[1], rows, ctx)).toEqual({ imeis: 'inventory.entry.v.imeiInvalid' });
    rows[1].imeiText = '';
    rows[1].quantity = '3'; // a quantity is ignored for IMEI-only products
    rows[0].offer = { ...rows[0].offer, enabled: true, price: '10' };
    expect(rowOfferPossible(rows[0], ctx)).toBe(false);
    expect(buildRowRequest(rows[0], 'wh1', ctx)).toEqual({ warehouseId: 'wh1', productId: 'ph', imeis: [VALID_IMEI, VALID_IMEI_2] });
  });

  it('does not validate saved rows but still counts their IMEIs', () => {
    const rows = rowsFor([phone, phone2]);
    rows[0].imeiText = VALID_IMEI;
    rows[0].status = 'saved';
    rows[0].quantity = 'garbage';
    rows[1].imeiText = VALID_IMEI;
    expect([...validateRows(rows, ctx).keys()]).toEqual(['ph2']);
  });

  it('"Menge für alle" sets unsaved quantity rows only', () => {
    const rows = rowsFor([akku1, akku2, phone]);
    rows[1].status = 'saved';
    expect(setQuantityForAll(rows, ' 4 ')).toBe(1);
    expect(rows.map((r) => r.quantity)).toEqual(['4', '1', '1']);
  });
});

describe('bulk save run', () => {
  function describeError(e: unknown) {
    const p = toApiProblem(e);
    return { code: p.code, text: `msg:${p.code}` };
  }

  it('sends every row with its own key and retries only failed rows with the same key, also after an edit', async () => {
    const rows = rowsFor([akku1, akku2, phone]);
    rows[0].quantity = '2';
    rows[1].quantity = '3';
    rows[2].imeiText = VALID_IMEI;
    const calls: { productId: string; key: string; body: ManualEntryRequest }[] = [];
    let imeiInStock = true;
    const send = async (body: ManualEntryRequest, key: string) => {
      calls.push({ productId: body.productId!, key, body });
      if (body.productId === 'ph' && imeiInStock) throw problem(409, 'unit.imei_in_stock');
      return result(body.productId!, body.quantity ?? body.imeis!.length);
    };

    const last = await runBulk(rows, { warehouseId: 'wh1', ctx, send, describe: describeError });
    expect(calls.map((c) => [c.productId, c.key])).toEqual([['a1', 'key-1'], ['a2', 'key-2'], ['ph', 'key-3']]);
    expect(new Set(calls.map((c) => c.key)).size).toBe(3);
    expect(rows.map((r) => r.status)).toEqual(['saved', 'saved', 'error']);
    expect(rows[2]).toMatchObject({ errorCode: 'unit.imei_in_stock', error: 'msg:unit.imei_in_stock' });
    expect(bulkSummary(rows)).toEqual({ total: 3, saved: 2, failed: 1 });
    expect(hasUnsavedRows(rows)).toBe(true);
    expect(last?.entryId).toBe('e-a2');

    // fix the failed row and resend: only that row, with its original key
    rows[2].imeiText = VALID_IMEI_2;
    imeiInStock = false;
    calls.length = 0;
    expect(rowsToSend(rows).map((r) => r.id)).toEqual(['ph']);
    await runBulk(rows, { warehouseId: 'wh1', ctx, send, describe: describeError });
    expect(calls.map((c) => [c.productId, c.key])).toEqual([['ph', 'key-3']]);
    expect(calls[0].body.imeis).toEqual([VALID_IMEI_2]);
    expect(rows.map((r) => r.status)).toEqual(['saved', 'saved', 'saved']);
    expect(rows[2].error).toBeNull();
    expect(hasUnsavedRows(rows)).toBe(false);

    // nothing left to send: a further run sends nothing
    calls.length = 0;
    await runBulk(rows, { warehouseId: 'wh1', ctx, send, describe: describeError });
    expect(calls).toEqual([]);
  });

  it('reuses the row key after an unknown outcome (network error / 5xx)', async () => {
    const rows = rowsFor([akku1]);
    const keysSeen: string[] = [];
    let fail = true;
    const send = async (body: ManualEntryRequest, key: string) => {
      keysSeen.push(key);
      if (fail) throw problem(0, 'network');
      return result('a1', 1);
    };
    await runBulk(rows, { warehouseId: 'wh1', ctx, send, describe: describeError });
    fail = false;
    await runBulk(rows, { warehouseId: 'wh1', ctx, send, describe: describeError });
    expect(keysSeen).toEqual(['key-1', 'key-1']);
    expect(rows[0].status).toBe('saved');
  });

  it('keeps at most 3 requests in flight, and 1 without a warehouse', async () => {
    async function maxInFlight(warehouseId: string): Promise<number> {
      const rows = rowsFor([akku1, akku2, display, product('x1', 'X1'), product('x2', 'X2')]);
      let inFlight = 0;
      let max = 0;
      const send = async (body: ManualEntryRequest) => {
        inFlight++;
        max = Math.max(max, inFlight);
        await new Promise((r) => setTimeout(r, 5));
        inFlight--;
        return result(body.productId!, 1);
      };
      await runBulk(rows, { warehouseId, ctx, send, describe: describeError });
      expect(rows.every((r) => r.status === 'saved')).toBe(true);
      return max;
    }
    expect(await maxInFlight('wh1')).toBe(3);
    expect(await maxInFlight('')).toBe(1);
  });
});

describe('API error → step', () => {
  it('maps codes and field keys to the step to fix', () => {
    expect(stepForProblem('unit.imei_in_stock', [])).toBe('stock');
    expect(stepForProblem('imei.blacklisted', [])).toBe('stock');
    expect(stepForProblem('entry.imei_conflicts', [])).toBe('stock');
    expect(stepForProblem('entry.sku_conflict', [])).toBe('offer');
    expect(stepForProblem('entry.identifier_conflict', [])).toBe('identify');
    expect(stepForProblem('validation.failed', ['newproduct.brand'])).toBe('identify');
    expect(stepForProblem('validation.failed', ['imeis.0'])).toBe('stock');
    expect(stepForProblem('validation.failed', ['offer.price'])).toBe('offer');
    expect(stepForProblem(null, [])).toBe('summary');
  });
});

describe('IMEI problems of a refused entry', () => {
  it('reads the per-IMEI problems in order and ignores unknown shapes', () => {
    const body = { code: 'entry.imei_conflicts', imeis: [{ imei: '490154203237518', code: 'imei.blacklisted' }, { imei: '356938035643809', code: 'unit.imei_in_stock' }, { bad: 1 }] };
    expect(imeiProblems(body)).toEqual([
      { imei: '490154203237518', code: 'imei.blacklisted' },
      { imei: '356938035643809', code: 'unit.imei_in_stock' },
    ]);
    expect(imeiProblems({ imeis: 'x' })).toEqual([]);
    expect(imeiProblems(null)).toEqual([]);
  });

  it('names the reason per IMEI', () => {
    expect(imeiProblemKey('imei.blacklisted')).toBe('inventory.entry.problem.blacklisted');
    expect(imeiProblemKey('unit.imei_in_stock')).toBe('inventory.entry.problem.inStock');
    expect(imeiProblemKey('x')).toBe('inventory.entry.problem.other');
  });
});
