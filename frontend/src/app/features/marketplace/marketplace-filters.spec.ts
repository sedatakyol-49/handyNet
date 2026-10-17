import { convertToParamMap } from '@angular/router';
import { grossFromNetMinor, minorToDecimalString } from '../../core/format/money';
import { moveItem, checkImageFile } from '../../shared/product-images/image-rules';
import {
  DEFAULT_FILTERS,
  activeChips,
  filtersFromParams,
  filtersToParams,
  priceRangeInvalid,
  removeChip,
  toApiQuery,
  toggleValue,
  withFilters,
} from './marketplace-filters';
import { quantityProblem } from './order-quantity';

const SELLER = '0192aa3e-1111-7000-8000-000000000001';

/** Router-like round trip: null params are dropped, arrays become repeated params. */
function roundTrip(params: Record<string, string | string[] | null>) {
  const clean: Record<string, string | string[]> = {};
  for (const [k, v] of Object.entries(params)) if (v !== null) clean[k] = v;
  return filtersFromParams(convertToParamMap(clean));
}

describe('marketplace filters ↔ URL', () => {
  it('defaults: empty URL, in stock only, sort by price', () => {
    expect(filtersFromParams(convertToParamMap({}))).toEqual(DEFAULT_FILTERS);
    expect(Object.values(filtersToParams(DEFAULT_FILTERS)).every((v) => v === null)).toBe(true);
  });

  it('reads repeated params, prices, days and flags', () => {
    const f = filtersFromParams(
      convertToParamMap({
        q: ' display ',
        category: ['Display', 'Battery', 'Display'],
        brand: ['Apple', 'Samsung'],
        sellerId: [SELLER, 'not-a-guid'],
        min: '10.5',
        max: '20,00',
        days: '3',
        model: 'iPhone 13',
        images: '1',
        stock: '0',
        sort: 'delivery',
        page: '3',
      }),
    );
    expect(f).toEqual({
      search: 'display',
      categories: ['Display', 'Battery'],
      brands: ['Apple', 'Samsung'],
      qualities: [],
      conditions: [],
      sellers: [SELLER],
      priceMinMinor: 1050,
      priceMaxMinor: 2000,
      maxDeliveryDays: 3,
      compatibleModel: 'iPhone 13',
      hasImages: true,
      inStockOnly: false,
      sort: 'delivery',
      page: 3,
    });
  });

  it('falls back to defaults for invalid values', () => {
    const f = filtersFromParams(convertToParamMap({ min: 'abc', days: '99', sort: 'random', page: '0' }));
    expect(f.priceMinMinor).toBeNull();
    expect(f.maxDeliveryDays).toBeNull();
    expect(f.sort).toBe('price');
    expect(f.page).toBe(1);
  });

  it('round-trips through URL params', () => {
    const f = withFilters(DEFAULT_FILTERS, {
      search: 'akku',
      brands: ['Apple'],
      conditions: ['New', 'Used'],
      sellers: [SELLER],
      priceMinMinor: 999,
      priceMaxMinor: 10000,
      maxDeliveryDays: 0,
      hasImages: true,
      inStockOnly: false,
      sort: 'name',
    });
    const params = filtersToParams(f);
    expect(params['min']).toBe('9.99');
    expect(params['max']).toBe('100.00');
    expect(params['days']).toBe('0');
    expect(roundTrip(params)).toEqual(f);
    expect(roundTrip(filtersToParams({ ...f, page: 4 })).page).toBe(4);
  });

  it('keeps the seller (Verkäufer) filter in the URL as repeated sellerId and sends it as sellerId', () => {
    const other = '0192aa3e-2222-7000-8000-000000000002';
    const f = withFilters(DEFAULT_FILTERS, { sellers: [SELLER, other] });
    expect(filtersToParams(f)['sellerId']).toEqual([SELLER, other]);
    expect(roundTrip(filtersToParams(f)).sellers).toEqual([SELLER, other]);
    expect(toApiQuery(f, 50).sellerId).toEqual([SELLER, other]);
    expect(activeChips(f)).toEqual([
      { kind: 'seller', value: SELLER },
      { kind: 'seller', value: other },
    ]);
    expect(removeChip(f, { kind: 'seller', value: SELLER }).sellers).toEqual([other]);
    expect(filtersFromParams(convertToParamMap({ seller: SELLER })).sellers).toEqual([]); // old/unknown key ignored
  });

  it('every change except paging resets to page 1', () => {
    const f = { ...DEFAULT_FILTERS, page: 5 };
    expect(withFilters(f, { brands: ['Apple'] }).page).toBe(1);
    expect(withFilters(f, { page: 7 }).page).toBe(7);
  });
});

describe('marketplace filters → API query', () => {
  it('maps lists and cents to decimal strings without float formatting', () => {
    const q = toApiQuery(withFilters(DEFAULT_FILTERS, { categories: ['Display'], priceMinMinor: 1, priceMaxMinor: 123456 }), 50);
    expect(q.category).toEqual(['Display']);
    expect(q.priceMin).toBe('0.01');
    expect(q.priceMax).toBe('1234.56');
    expect(q.inStockOnly).toBe(true);
    expect(q.hasImages).toBeUndefined();
    expect(q.pageSize).toBe(50);
  });

  it('drops an inverted price range instead of sending a request the API rejects', () => {
    const f = withFilters(DEFAULT_FILTERS, { priceMinMinor: 5000, priceMaxMinor: 1000 });
    expect(priceRangeInvalid(f)).toBe(true);
    const q = toApiQuery(f, 50);
    expect(q.priceMin).toBeUndefined();
    expect(q.priceMax).toBeUndefined();
  });
});

describe('active filter chips', () => {
  it('lists non-default filters and removes them one by one', () => {
    const f = withFilters(DEFAULT_FILTERS, { brands: ['Apple', 'Samsung'], hasImages: true, inStockOnly: false, priceMaxMinor: 500 });
    const chips = activeChips(f);
    expect(chips.map((c) => `${c.kind}:${c.value}`)).toEqual(['brand:Apple', 'brand:Samsung', 'priceMax:500', 'hasImages:1', 'allStock:0']);
    const after = removeChip(f, chips[0]);
    expect(after.brands).toEqual(['Samsung']);
    expect(removeChip(f, chips[4]).inStockOnly).toBe(true);
    expect(activeChips(DEFAULT_FILTERS)).toEqual([]);
  });

  it('toggles multi-select values', () => {
    expect(toggleValue(['a'], 'b', true)).toEqual(['a', 'b']);
    expect(toggleValue(['a', 'b'], 'a', false)).toEqual(['b']);
    expect(toggleValue(['a'], 'a', true)).toEqual(['a']);
  });
});

describe('money and quantities in the detail modal', () => {
  it('computes gross with 19 % rounded half away from zero in integer cents', () => {
    expect(grossFromNetMinor(1000)).toBe(1190);
    expect(grossFromNetMinor(1)).toBe(1); // 0.19 → 0
    expect(grossFromNetMinor(50)).toBe(60); // 9.5 → 10
    expect(grossFromNetMinor(1990)).toBe(2368); // 378.1 → 378
    expect(minorToDecimalString(5)).toBe('0.05');
  });

  it('checks quantity against MOQ, availability and the cart', () => {
    const l = { minOrderQuantity: 2, available: 5 };
    expect(quantityProblem(l, 1, 0)?.key).toBe('marketplace.qtyBelowMinimum');
    expect(quantityProblem(l, 1, 2)).toBeNull();
    expect(quantityProblem(l, 4, 2)?.key).toBe('marketplace.qtyAboveAvailable');
    expect(quantityProblem(l, Number.NaN, 0)?.key).toBe('marketplace.qtyInvalid');
  });
});

describe('image rules', () => {
  it('moves images and checks files', () => {
    expect(moveItem(['a', 'b', 'c'], 2, 0)).toEqual(['c', 'a', 'b']);
    expect(moveItem(['a', 'b', 'c'], 0, 9)).toEqual(['b', 'c', 'a']);
    expect(checkImageFile({ type: 'image/gif', size: 10 })).toBe('type');
    expect(checkImageFile({ type: 'image/png', size: 6 * 1024 * 1024 })).toBe('size');
    expect(checkImageFile({ type: 'image/webp', size: 10 })).toBeNull();
  });
});
