import {
  EMPTY_SALES_FILTER,
  SalesFilter,
  activeFilterCount,
  effectiveScope,
  filterFromParams,
  filterToParams,
  filterToQuery,
  localDayStart,
} from './sales-filter';

const ctx = { registerId: 'r1', sessionId: 's1' };
const CUSTOMER = '0193a2b4-5c6d-7e8f-9a0b-1c2d3e4f5a6b';
const f = (over: Partial<SalesFilter> = {}): SalesFilter => ({ ...EMPTY_SALES_FILTER, ...over });

describe('order history filter ↔ query', () => {
  it('uses the open session by default, else the register; "all" sends neither', () => {
    expect(filterToQuery(f(), ctx).query).toEqual({ sessionId: 's1' });
    expect(filterToQuery(f(), { registerId: 'r1', sessionId: null }).query).toEqual({ registerId: 'r1' });
    expect(filterToQuery(f({ scope: 'register' }), ctx).query).toEqual({ registerId: 'r1' });
    expect(filterToQuery(f({ scope: 'all' }), ctx).query).toEqual({});
    // "Current session" without an open session falls back to the register.
    expect(filterToQuery(f({ scope: 'session' }), { registerId: 'r1', sessionId: null }).query).toEqual({ registerId: 'r1' });
    expect(effectiveScope(f(), false)).toBe('register');
  });

  it('maps local calendar days to instants with an exclusive end (the day after "to")', () => {
    const { query, valid } = filterToQuery(f({ scope: 'all', from: '2026-10-01', to: '2026-10-17' }), ctx);
    expect(valid).toBe(true);
    expect(query.from).toBe(new Date(2026, 9, 1).toISOString());
    expect(query.to).toBe(new Date(2026, 9, 18).toISOString());
    // A single day.
    const one = filterToQuery(f({ scope: 'all', from: '2026-12-31', to: '2026-12-31' }), ctx).query;
    expect(one.to).toBe(new Date(2027, 0, 1).toISOString());
  });

  it('reports invalid days and ranges and leaves them out of the query', () => {
    expect(localDayStart('2026-02-30')).toBeNull();
    const bad = filterToQuery(f({ scope: 'all', from: '2026-02-30' }), ctx);
    expect(bad.errors).toEqual({ from: true });
    expect(bad.query.from).toBeUndefined();
    const reversed = filterToQuery(f({ scope: 'all', from: '2026-10-17', to: '2026-10-16' }), ctx);
    expect(reversed.errors).toEqual({ dateRange: true });
    expect(reversed.query).toEqual({});
  });

  it('parses signed amounts to cents and checks the range', () => {
    expect(filterToQuery(f({ scope: 'all', minTotal: '-12,50', maxTotal: '1.234,56' }), ctx).query).toEqual({ minTotalMinor: -1250, maxTotalMinor: 123_456 });
    expect(filterToQuery(f({ scope: 'all', minTotal: '20', maxTotal: '10' }), ctx).errors).toEqual({ amountRange: true });
    expect(filterToQuery(f({ scope: 'all', maxTotal: '12,345' }), ctx).errors).toEqual({ maxTotal: true });
  });

  it('maps number, cashier, customer, payment method and kind', () => {
    const { query, valid } = filterToQuery(
      f({ scope: 'all', number: ' 42 ', cashierId: CUSTOMER, customerId: CUSTOMER, paymentMethod: 'Card', kind: 'Return' }),
      ctx,
    );
    expect(valid).toBe(true);
    expect(query).toEqual({ number: 42, cashierId: CUSTOMER, customerId: CUSTOMER, paymentMethod: 'Card', kind: 'Return' });
    expect(filterToQuery(f({ number: '0' }), ctx).errors).toEqual({ number: true });
    expect(filterToQuery(f({ number: '12a' }), ctx).errors).toEqual({ number: true });
    expect(filterToQuery(f({ customerId: 'not-a-guid' }), ctx).query.customerId).toBeUndefined();
  });

  it('round-trips through URL params and drops malformed values', () => {
    const filter = f({ scope: 'all', number: '7', from: '2026-10-01', to: '2026-10-02', minTotal: '5', cashierId: CUSTOMER, customerId: CUSTOMER, customerName: 'Erika', paymentMethod: 'Cash', kind: 'Sale' });
    const params = filterToParams(filter);
    expect(params).toMatchObject({ scope: 'all', nr: '7', von: '2026-10-01', bis: '2026-10-02', min: '5', max: null, zahlart: 'Cash', art: 'Sale', kundeName: 'Erika' });
    const back = filterFromParams(Object.fromEntries(Object.entries(params).filter(([, v]) => v !== null)) as Record<string, string>);
    expect(back).toEqual(filter);
    expect(filterFromParams({ scope: 'x', von: '01.10.2026', zahlart: 'Bitcoin', art: 'Gift', kassierer: '1', kunde: 'abc', kundeName: 'Eve' })).toEqual(EMPTY_SALES_FILTER);
    // The customer name is only kept with a customer id.
    expect(filterToParams(f({ customerName: 'Erika' }))['kundeName']).toBeNull();
  });

  it('counts active filters (the scope is not a filter)', () => {
    expect(activeFilterCount(f({ scope: 'all' }))).toBe(0);
    expect(activeFilterCount(f({ number: '1', kind: 'Sale', customerId: CUSTOMER }))).toBe(3);
  });
});
