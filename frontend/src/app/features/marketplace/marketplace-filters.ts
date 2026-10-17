import { MarketplaceQuery } from '../../core/api/handynet-api';
import { MarketplaceSort } from '../../core/api/models';
import { minorToDecimalString, parseMoneyToMinor } from '../../core/format/money';

/**
 * Marketplace filter state. It lives in the URL (shareable, back button) and is mapped to the API query here; the
 * mapping is pure so it can be unit-tested.
 */
export interface MarketFilters {
  search: string;
  categories: string[];
  brands: string[];
  qualities: string[];
  conditions: string[];
  /** Seller organization ids. */
  sellers: string[];
  /** Net VK range in cents. */
  priceMinMinor: number | null;
  priceMaxMinor: number | null;
  maxDeliveryDays: number | null;
  compatibleModel: string;
  hasImages: boolean;
  inStockOnly: boolean;
  sort: MarketplaceSort;
  page: number;
}

export const SORTS: MarketplaceSort[] = ['price', 'price_desc', 'delivery', 'name'];
export const MAX_DELIVERY_DAYS = 60;

export const DEFAULT_FILTERS: MarketFilters = {
  search: '',
  categories: [],
  brands: [],
  qualities: [],
  conditions: [],
  sellers: [],
  priceMinMinor: null,
  priceMaxMinor: null,
  maxDeliveryDays: null,
  compatibleModel: '',
  hasImages: false,
  inStockOnly: true,
  sort: 'price',
  page: 1,
};

/** URL query parameter names (short, stable). `offer` is used by the detail modal and is not a filter. */
export const URL_KEYS = {
  search: 'q',
  categories: 'category',
  brands: 'brand',
  qualities: 'quality',
  conditions: 'condition',
  sellers: 'sellerId',
  priceMin: 'min',
  priceMax: 'max',
  maxDeliveryDays: 'days',
  compatibleModel: 'model',
  hasImages: 'images',
  inStockOnly: 'stock',
  sort: 'sort',
  page: 'page',
} as const;

/** Minimal ParamMap shape (Angular's ParamMap satisfies it). */
export interface ParamSource {
  get(name: string): string | null;
  getAll(name: string): string[];
}

function list(values: string[]): string[] {
  const out: string[] = [];
  for (const v of values) {
    const t = v.trim();
    if (t && !out.includes(t)) out.push(t);
  }
  return out;
}

function money(value: string | null): number | null {
  if (value === null || value.trim() === '') return null;
  const minor = parseMoneyToMinor(value);
  return minor !== null && minor >= 0 ? minor : null;
}

function wholeNumber(value: string | null, min: number, max: number): number | null {
  if (value === null || !/^\d+$/.test(value.trim())) return null;
  const n = Number(value.trim());
  return n >= min && n <= max ? n : null;
}

/** URL → filters. Unknown or invalid values fall back to the defaults (a hand-edited URL never breaks the page). */
export function filtersFromParams(p: ParamSource): MarketFilters {
  const sort = p.get(URL_KEYS.sort) as MarketplaceSort | null;
  return {
    search: (p.get(URL_KEYS.search) ?? '').trim().slice(0, 200),
    categories: list(p.getAll(URL_KEYS.categories)),
    brands: list(p.getAll(URL_KEYS.brands)).slice(0, 50),
    qualities: list(p.getAll(URL_KEYS.qualities)),
    conditions: list(p.getAll(URL_KEYS.conditions)),
    sellers: list(p.getAll(URL_KEYS.sellers)).filter((s) => /^[0-9a-f-]{36}$/i.test(s)).slice(0, 50),
    priceMinMinor: money(p.get(URL_KEYS.priceMin)),
    priceMaxMinor: money(p.get(URL_KEYS.priceMax)),
    maxDeliveryDays: wholeNumber(p.get(URL_KEYS.maxDeliveryDays), 0, MAX_DELIVERY_DAYS),
    compatibleModel: (p.get(URL_KEYS.compatibleModel) ?? '').trim().slice(0, 200),
    hasImages: p.get(URL_KEYS.hasImages) === '1',
    inStockOnly: p.get(URL_KEYS.inStockOnly) !== '0',
    sort: sort && SORTS.includes(sort) ? sort : 'price',
    page: wholeNumber(p.get(URL_KEYS.page), 1, 100_000) ?? 1,
  };
}

/**
 * Filters → URL query params. Every filter key is present; defaults are `null` so that a navigation with
 * `queryParamsHandling: 'merge'` removes them (and keeps unrelated params such as `offer`).
 */
export function filtersToParams(f: MarketFilters): Record<string, string | string[] | null> {
  const arr = (v: string[]) => (v.length ? v : null);
  return {
    [URL_KEYS.search]: f.search.trim() || null,
    [URL_KEYS.categories]: arr(f.categories),
    [URL_KEYS.brands]: arr(f.brands),
    [URL_KEYS.qualities]: arr(f.qualities),
    [URL_KEYS.conditions]: arr(f.conditions),
    [URL_KEYS.sellers]: arr(f.sellers),
    [URL_KEYS.priceMin]: f.priceMinMinor === null ? null : minorToDecimalString(f.priceMinMinor),
    [URL_KEYS.priceMax]: f.priceMaxMinor === null ? null : minorToDecimalString(f.priceMaxMinor),
    [URL_KEYS.maxDeliveryDays]: f.maxDeliveryDays === null ? null : String(f.maxDeliveryDays),
    [URL_KEYS.compatibleModel]: f.compatibleModel.trim() || null,
    [URL_KEYS.hasImages]: f.hasImages ? '1' : null,
    [URL_KEYS.inStockOnly]: f.inStockOnly ? null : '0',
    [URL_KEYS.sort]: f.sort === 'price' ? null : f.sort,
    [URL_KEYS.page]: f.page > 1 ? String(f.page) : null,
  };
}

/** True when min and max are both set and min > max (the API rejects it; the UI shows an error instead). */
export function priceRangeInvalid(f: Pick<MarketFilters, 'priceMinMinor' | 'priceMaxMinor'>): boolean {
  return f.priceMinMinor !== null && f.priceMaxMinor !== null && f.priceMinMinor > f.priceMaxMinor;
}

/** Filters → API query. Without page/sort/pageSize for the facets call (see MarketplaceApi.facets). */
export function toApiQuery(f: MarketFilters, pageSize: number): MarketplaceQuery {
  const invalidRange = priceRangeInvalid(f);
  return {
    search: f.search.trim() || undefined,
    category: f.categories,
    brand: f.brands,
    quality: f.qualities,
    condition: f.conditions,
    sellerId: f.sellers,
    priceMin: f.priceMinMinor === null || invalidRange ? undefined : minorToDecimalString(f.priceMinMinor),
    priceMax: f.priceMaxMinor === null || invalidRange ? undefined : minorToDecimalString(f.priceMaxMinor),
    maxDeliveryDays: f.maxDeliveryDays,
    compatibleModel: f.compatibleModel.trim() || undefined,
    hasImages: f.hasImages || undefined,
    inStockOnly: f.inStockOnly || undefined,
    sort: f.sort,
    page: f.page,
    pageSize,
  };
}

/** Applies a change; any change other than the page itself goes back to page 1. */
export function withFilters(f: MarketFilters, patch: Partial<MarketFilters>): MarketFilters {
  return { ...f, ...patch, page: patch.page ?? 1 };
}

export type ChipKind =
  | 'search'
  | 'category'
  | 'brand'
  | 'quality'
  | 'condition'
  | 'seller'
  | 'priceMin'
  | 'priceMax'
  | 'maxDeliveryDays'
  | 'compatibleModel'
  | 'hasImages'
  | 'allStock'
  | 'sort';

export interface FilterChip {
  kind: ChipKind;
  /** The raw value (category enum, brand, seller id, cents …); labels are resolved by the component. */
  value: string;
}

/** Active (non-default) filters in display order. "Nur verfügbare" is the default, so turning it off is a chip. */
export function activeChips(f: MarketFilters): FilterChip[] {
  const chips: FilterChip[] = [];
  if (f.search.trim()) chips.push({ kind: 'search', value: f.search.trim() });
  for (const v of f.categories) chips.push({ kind: 'category', value: v });
  for (const v of f.brands) chips.push({ kind: 'brand', value: v });
  for (const v of f.qualities) chips.push({ kind: 'quality', value: v });
  for (const v of f.conditions) chips.push({ kind: 'condition', value: v });
  for (const v of f.sellers) chips.push({ kind: 'seller', value: v });
  if (f.priceMinMinor !== null) chips.push({ kind: 'priceMin', value: String(f.priceMinMinor) });
  if (f.priceMaxMinor !== null) chips.push({ kind: 'priceMax', value: String(f.priceMaxMinor) });
  if (f.maxDeliveryDays !== null) chips.push({ kind: 'maxDeliveryDays', value: String(f.maxDeliveryDays) });
  if (f.compatibleModel.trim()) chips.push({ kind: 'compatibleModel', value: f.compatibleModel.trim() });
  if (f.hasImages) chips.push({ kind: 'hasImages', value: '1' });
  if (!f.inStockOnly) chips.push({ kind: 'allStock', value: '0' });
  return chips;
}

export function removeChip(f: MarketFilters, chip: FilterChip): MarketFilters {
  const without = (values: string[]) => values.filter((v) => v !== chip.value);
  switch (chip.kind) {
    case 'search':
      return withFilters(f, { search: '' });
    case 'category':
      return withFilters(f, { categories: without(f.categories) });
    case 'brand':
      return withFilters(f, { brands: without(f.brands) });
    case 'quality':
      return withFilters(f, { qualities: without(f.qualities) });
    case 'condition':
      return withFilters(f, { conditions: without(f.conditions) });
    case 'seller':
      return withFilters(f, { sellers: without(f.sellers) });
    case 'priceMin':
      return withFilters(f, { priceMinMinor: null });
    case 'priceMax':
      return withFilters(f, { priceMaxMinor: null });
    case 'maxDeliveryDays':
      return withFilters(f, { maxDeliveryDays: null });
    case 'compatibleModel':
      return withFilters(f, { compatibleModel: '' });
    case 'hasImages':
      return withFilters(f, { hasImages: false });
    case 'allStock':
      return withFilters(f, { inStockOnly: true });
    case 'sort':
      return withFilters(f, { sort: 'price' });
  }
}

/** Reset keeps nothing but the defaults (sort included). */
export function resetFilters(): MarketFilters {
  return { ...DEFAULT_FILTERS };
}

/** Toggles a value in a multi-select list. */
export function toggleValue(values: string[], value: string, on: boolean): string[] {
  if (on) return values.includes(value) ? values : [...values, value];
  return values.filter((v) => v !== value);
}
