import { PosPaymentMethod, ReceiptKind, SaleQuery } from '../../core/api/models';
import { parseMoneyToMinor } from '../../core/format/money';

/**
 * Filter bar of the order history (`GET /api/pos/sales`). Kept as typed text so the inputs stay as entered; mapped to
 * the API query (cents, ISO instants with an exclusive end) and to/from the page URL (shareable, survives reloads).
 */
export type SalesScope = 'session' | 'register' | 'all';

export interface SalesFilter {
  /** null = default (current session if one is open, else this register). */
  scope: SalesScope | null;
  number: string;
  /** Local calendar days `YYYY-MM-DD`, both inclusive in the UI. */
  from: string;
  to: string;
  /** Gross receipt total, signed (returns are negative), German or English decimal. */
  minTotal: string;
  maxTotal: string;
  cashierId: string;
  customerId: string;
  /** Display only (the API filters by id). */
  customerName: string;
  paymentMethod: '' | PosPaymentMethod;
  kind: '' | ReceiptKind;
}

export const EMPTY_SALES_FILTER: SalesFilter = {
  scope: null,
  number: '',
  from: '',
  to: '',
  minTotal: '',
  maxTotal: '',
  cashierId: '',
  customerId: '',
  customerName: '',
  paymentMethod: '',
  kind: '',
};

export type SalesFilterField = 'number' | 'from' | 'to' | 'minTotal' | 'maxTotal';
export type SalesFilterErrors = Partial<Record<SalesFilterField | 'dateRange' | 'amountRange', true>>;

export interface SalesQueryResult {
  query: SaleQuery;
  errors: SalesFilterErrors;
  valid: boolean;
}

const DATE = /^(\d{4})-(\d{2})-(\d{2})$/;
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const PAYMENT_METHODS: readonly PosPaymentMethod[] = ['Cash', 'Card'];
const KINDS: readonly ReceiptKind[] = ['Sale', 'Storno', 'Return'];
const SCOPES: readonly SalesScope[] = ['session', 'register', 'all'];

/** Start of a local calendar day; null for invalid dates (e.g. 2026-02-30). */
export function localDayStart(day: string, addDays = 0): Date | null {
  const m = DATE.exec(day.trim());
  if (!m) return null;
  const [y, mo, d] = [Number(m[1]), Number(m[2]), Number(m[3])];
  const date = new Date(y, mo - 1, d);
  if (date.getFullYear() !== y || date.getMonth() !== mo - 1 || date.getDate() !== d) return null;
  if (addDays) date.setDate(date.getDate() + addDays);
  return date;
}

export function effectiveScope(f: Pick<SalesFilter, 'scope'>, hasOpenSession: boolean): SalesScope {
  return f.scope ?? (hasOpenSession ? 'session' : 'register');
}

/**
 * Filter → API query. `to` is the day after the chosen end day (the API's `to` is exclusive). Invalid inputs are
 * reported per field and left out of the query.
 */
export function filterToQuery(f: SalesFilter, ctx: { registerId: string; sessionId: string | null }): SalesQueryResult {
  const errors: SalesFilterErrors = {};
  const query: SaleQuery = {};
  const scope = effectiveScope(f, !!ctx.sessionId);
  if (scope === 'session' && ctx.sessionId) query.sessionId = ctx.sessionId;
  else if (scope !== 'all') query.registerId = ctx.registerId;

  const number = f.number.trim();
  if (number) {
    if (/^\d{1,12}$/.test(number) && Number(number) > 0) query.number = Number(number);
    else errors.number = true;
  }

  const from = f.from.trim() ? localDayStart(f.from) : null;
  const to = f.to.trim() ? localDayStart(f.to, 1) : null;
  if (f.from.trim() && !from) errors.from = true;
  if (f.to.trim() && !to) errors.to = true;
  if (from && to && from.getTime() >= to.getTime()) errors.dateRange = true;
  else {
    if (from) query.from = from.toISOString();
    if (to) query.to = to.toISOString();
  }

  const min = f.minTotal.trim() ? parseMoneyToMinor(f.minTotal) : null;
  const max = f.maxTotal.trim() ? parseMoneyToMinor(f.maxTotal) : null;
  if (f.minTotal.trim() && min === null) errors.minTotal = true;
  if (f.maxTotal.trim() && max === null) errors.maxTotal = true;
  if (min !== null && max !== null && min > max) errors.amountRange = true;
  else {
    if (min !== null) query.minTotalMinor = min;
    if (max !== null) query.maxTotalMinor = max;
  }

  if (f.cashierId && GUID.test(f.cashierId)) query.cashierId = f.cashierId;
  if (f.customerId && GUID.test(f.customerId)) query.customerId = f.customerId;
  if (f.paymentMethod) query.paymentMethod = f.paymentMethod;
  if (f.kind) query.kind = f.kind;
  return { query, errors, valid: Object.keys(errors).length === 0 };
}

/** Number of filters besides the scope (for the "Filter (n)" badge). */
export function activeFilterCount(f: SalesFilter): number {
  return [f.number, f.from, f.to, f.minTotal, f.maxTotal, f.cashierId, f.customerId, f.paymentMethod, f.kind].filter((v) => v.trim() !== '').length;
}

/** URL query params of the receipts page (null removes a param). */
export function filterToParams(f: SalesFilter): Record<string, string | null> {
  const v = (s: string) => (s.trim() ? s.trim() : null);
  return {
    scope: f.scope,
    nr: v(f.number),
    von: v(f.from),
    bis: v(f.to),
    min: v(f.minTotal),
    max: v(f.maxTotal),
    kassierer: v(f.cashierId),
    kunde: v(f.customerId),
    kundeName: f.customerId ? v(f.customerName) : null,
    zahlart: v(f.paymentMethod),
    art: v(f.kind),
  };
}

/** Tolerant parse of the page URL (unknown or malformed values are dropped). */
export function filterFromParams(p: Readonly<Record<string, string | null | undefined>>): SalesFilter {
  const s = (k: string, max = 40) => (p[k] ?? '').toString().slice(0, max);
  const scope = s('scope') as SalesScope;
  const method = s('zahlart') as PosPaymentMethod;
  const kind = s('art') as ReceiptKind;
  const cashier = s('kassierer');
  const customer = s('kunde');
  return {
    scope: SCOPES.includes(scope) ? scope : null,
    number: s('nr', 12),
    from: DATE.test(s('von')) ? s('von') : '',
    to: DATE.test(s('bis')) ? s('bis') : '',
    minTotal: s('min', 16),
    maxTotal: s('max', 16),
    cashierId: GUID.test(cashier) ? cashier : '',
    customerId: GUID.test(customer) ? customer : '',
    customerName: GUID.test(customer) ? s('kundeName', 200) : '',
    paymentMethod: PAYMENT_METHODS.includes(method) ? method : '',
    kind: KINDS.includes(kind) ? kind : '',
  };
}
