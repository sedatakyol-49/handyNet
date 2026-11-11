import { CreateSaleRequest, PosProduct } from '../../../core/api/models';
import { CartLine } from '../pos.logic';
import { MemoryStore } from './offline-db';
import {
  QueueEntry,
  afterAttempt,
  cachedCategories,
  cachedLookup,
  discardReasonError,
  discardRecord,
  filterCachedCustomers,
  filterCachedProducts,
  isDeviceId,
  isOfflineReference,
  isQueueEntry,
  newDeviceId,
  newEntry,
  nextSequence,
  offlineBlock,
  offlineBody,
  offlineReference,
  reduceCachedStock,
  requeue,
  restoreEntries,
  scopeOf,
  syncOrder,
  syncOutcome,
  toCachedCustomer,
} from './offline-queue.logic';
import { provisionalLayout } from './provisional-receipt';
import { isHealthyAnswer, isUnreachable } from './reachability.service';

const line: CartLine = { key: 'p1', kind: 'Product', productId: 'p1', imei: null, description: 'Displayschutz', quantity: 2, priceInput: '12,50', discountInput: '', available: 5, taxTreatment: 'Standard19', note: ' für Auto ' };
const body: CreateSaleRequest = { registerId: 'r1', lines: [{ productId: 'p1', quantity: 2, unitPriceMinor: 1250, discountMinor: null }], payments: [{ method: 'Cash', amountMinor: 5000 }] };

function entry(over: Partial<QueueEntry> = {}, sequence = 1): QueueEntry {
  return {
    ...newEntry({
      id: 'key-' + sequence, organizationId: 'o1', registerId: 'r1', registerName: 'Theke', sequence, deviceId: '7K2M', recordedAt: `2026-11-11T10:0${sequence}:00.000Z`, body,
      lines: [line], payments: body.payments, tenderedCashMinor: 5000, changeMinor: 2500, roundingMinor: 0, cashierName: 'Murat',
    }),
    ...over,
  };
}

describe('offline reference numbering', () => {
  it('builds OFF-<device>-<n> that the API accepts', () => {
    expect(offlineReference('7K2M', 12)).toBe('OFF-7K2M-12');
    expect(isOfflineReference(offlineReference('7K2M', 123456))).toBe(true);
    expect(isOfflineReference('OFF 1')).toBe(false);
    expect(isOfflineReference('x'.repeat(41))).toBe(false);
  });

  it('device ids are 4 unambiguous characters', () => {
    const id = newDeviceId();
    expect(isDeviceId(id)).toBe(true);
    expect(id).not.toMatch(/[01ILO]/);
    expect(newDeviceId(() => 0)).toBe('AAAA');
    expect(newDeviceId(() => 0.999999)).toBe('9999');
    expect(isDeviceId('ab12')).toBe(false);
    expect(isDeviceId(null)).toBe(false);
  });

  it('the next number is above everything the device still knows (clearing one store never reuses a number)', () => {
    expect(nextSequence()).toBe(1);
    expect(nextSequence(3, null, 7, undefined)).toBe(8);
    expect(nextSequence(NaN, -4, 1.5)).toBe(1);
  });

  it('the store allocates numbers atomically per register and remembers them', async () => {
    const store = new MemoryStore();
    const a = await store.add(scopeOf('o1', 'r1'), 0, (n) => entry({}, n));
    const b = await store.add(scopeOf('o1', 'r1'), 0, (n) => entry({ id: 'k2' }, n));
    const other = await store.add(scopeOf('o1', 'r2'), 0, (n) => entry({ id: 'k3', registerId: 'r2', scope: scopeOf('o1', 'r2') }, n));
    expect([a.reference, b.reference, other.reference]).toEqual(['OFF-7K2M-1', 'OFF-7K2M-2', 'OFF-7K2M-1']);
    // localStorage knew a higher number (IndexedDB was cleared): it wins
    const c = await store.add(scopeOf('o1', 'r1'), 40, (n) => entry({ id: 'k4' }, n));
    expect(c.reference).toBe('OFF-7K2M-41');
    expect((await store.entries('o1')).length).toBe(4);
    expect(await store.entries('o2')).toEqual([]);
  });
});

describe('offline queue entries', () => {
  it('a new entry carries the request with the offline record, the key and what the provisional receipt needs', () => {
    const e = entry();
    expect(e).toMatchObject({ id: 'key-1', scope: 'o1.r1', reference: 'OFF-7K2M-1', status: 'queued', attempts: 0, error: null, totalMinor: 2500, changeMinor: 2500, cashierName: 'Murat' });
    expect(e.body).toEqual({ ...body, offline: { recordedAt: '2026-11-11T10:01:00.000Z', reference: 'OFF-7K2M-1' } });
    expect(e.lines).toEqual([{ description: 'Displayschutz', quantity: 2, unitPriceMinor: 1250, discountMinor: 0, lineGrossMinor: 2500, imei: null, note: 'für Auto' }]);
    expect(e.payments).toEqual([{ method: 'Cash', amountMinor: 5000, tenderedMinor: 5000 }]);
    expect(offlineBody(body, 't', 'ref').offline).toEqual({ recordedAt: 't', reference: 'ref' });
    expect(body).not.toHaveProperty('offline'); // the online body is not touched
  });

  it('survives a JSON round trip (IndexedDB, restart) and drops damaged rows', () => {
    const stored = JSON.parse(JSON.stringify([entry({}, 2), entry({ status: 'syncing' }, 1), { id: 'broken' }, null]));
    const restored = restoreEntries(stored);
    expect(restored.map((e) => [e.reference, e.status])).toEqual([
      ['OFF-7K2M-1', 'queued'], // a sync interrupted by the restart waits again; the same key makes that safe
      ['OFF-7K2M-2', 'queued'],
    ]);
    expect(isQueueEntry(stored[0])).toBe(true);
    expect(isQueueEntry({ ...stored[0], status: 'done' })).toBe(false);
  });

  it('syncs oldest first, only waiting entries, optionally for one register', () => {
    const list = [entry({}, 3), entry({ status: 'attention' }, 1), entry({}, 2), entry({ registerId: 'r2' }, 4)];
    expect(syncOrder(list).map((e) => e.sequence)).toEqual([2, 3, 4]);
    expect(syncOrder(list, 'r1').map((e) => e.sequence)).toEqual([2, 3]);
  });
});

describe('offline sync state machine', () => {
  it('classifies the answer of the API', () => {
    expect(syncOutcome(200, null, false)).toBe('synced');
    expect(syncOutcome(201, null, false)).toBe('synced');
    expect([0, 500, 502, 503, 401, 408, 429].map((s) => syncOutcome(s, null, false))).toEqual(Array(7).fill('retryLater'));
    expect(syncOutcome(409, 'stock.insufficient', false)).toBe('attention');
    expect(syncOutcome(409, 'pos.no_open_session', true)).toBe('attention');
    expect(syncOutcome(409, 'pos.offline_not_enabled', false)).toBe('attention');
    expect(syncOutcome(403, 'permission.denied', false)).toBe('attention');
    // the key was used by the first, online attempt of this sale: replay that request instead of giving up
    expect(syncOutcome(422, 'pos.idempotency_key_reused', true)).toBe('replayOnline');
    expect(syncOutcome(422, 'pos.idempotency_key_reused', false)).toBe('attention');
  });

  it('a refused entry needs attention with the server\'s reason; an unreachable API leaves it waiting', () => {
    const error = { status: 409, code: 'stock.insufficient', message: 'Nicht genug Bestand.' };
    const refused = afterAttempt(entry({ status: 'syncing' }), 'attention', '2026-11-11T11:00:00Z', error);
    expect(refused).toMatchObject({ status: 'attention', attempts: 1, lastAttemptAt: '2026-11-11T11:00:00Z', error });
    const waiting = afterAttempt(entry({ status: 'syncing' }), 'retryLater', 'now', { status: 0, code: null, message: 'offline' });
    expect(waiting).toMatchObject({ status: 'queued', attempts: 1 });
    expect(requeue(refused)).toMatchObject({ status: 'queued', error: null, attempts: 1 });
  });

  it('knows an unreachable API from an answer', () => {
    expect([0, 502, 503, 504].map(isUnreachable)).toEqual([true, true, true, true]);
    expect([200, 400, 401, 409, 500].map(isUnreachable)).toEqual([false, false, false, false, false]);
    expect(isHealthyAnswer(200, 'text/plain', 'Healthy')).toBe(true);
    expect(isHealthyAnswer(200, 'text/html; charset=utf-8', '<html>')).toBe(false); // app shell or captive portal
    expect(isHealthyAnswer(200, 'text/plain', 'login required')).toBe(false);
    expect(isHealthyAnswer(503, 'text/plain', 'Unhealthy')).toBe(false);
  });
});

describe('offline rules of the till', () => {
  const ok = { offlineMode: true, sessionOpen: true, persistent: true, methods: ['Cash', 'Card'] };

  it('sells offline only with offline mode, an open session, persistent storage and cash/card', () => {
    expect(offlineBlock(ok)).toBeNull();
    expect(offlineBlock({ ...ok, offlineMode: false })).toBe('notEnabled');
    expect(offlineBlock({ ...ok, sessionOpen: false })).toBe('noSession');
    expect(offlineBlock({ ...ok, persistent: false })).toBe('noStorage');
    expect(offlineBlock({ ...ok, methods: ['Cash', 'GiftCard'] })).toBe('payment');
    expect(offlineBlock({ ...ok, cardUnreported: true })).toBe('cardUnreported');
  });

  it('discarding needs a reason and produces a record with who and when', () => {
    expect(discardReasonError('  ')).toBe('required');
    expect(discardReasonError('ab')).toBe('required');
    expect(discardReasonError('x'.repeat(501))).toBe('tooLong');
    expect(discardReasonError('Doppelt erfasst')).toBeNull();
    const record = discardRecord(entry(), ' Doppelt erfasst ', 'Sabine', '2026-11-11T12:00:00Z');
    expect(record).toMatchObject({ kind: 'handynet-offline-sale-discarded', reason: 'Doppelt erfasst', discardedBy: 'Sabine', discardedAt: '2026-11-11T12:00:00Z' });
    expect(record.entry.reference).toBe('OFF-7K2M-1');
  });
});

describe('cached register data', () => {
  const product = (over: Partial<PosProduct>): PosProduct => ({
    productId: 'p1', brand: 'Apple', name: 'Display iPhone 13', category: 'Display', quality: 'ServicePack', ean: '4000000000017', mpn: 'A-1', imageUrl: null, serialized: false,
    onHand: 5, reserved: 0, available: 5, priceMinor: 8990, priceSource: 'PosPrice', posPriceMinor: 8990, ...over,
  });
  const all = [
    product({}),
    product({ productId: 'p2', name: 'Ladekabel USB-C', category: 'Accessory', ean: '4000000000024', mpn: null, available: 0, onHand: 0 }),
    product({ productId: 'p3', brand: 'Samsung', name: 'Galaxy S23', category: 'Smartphone', ean: '4000000000031', serialized: true, available: 1 }),
  ];

  it('filters like the grid: search words, category, stock, paging', () => {
    expect(filterCachedProducts(all, {}).items.map((p) => p.productId)).toEqual(['p1', 'p3']);
    expect(filterCachedProducts(all, { includeOutOfStock: true }).totalCount).toBe(3);
    expect(filterCachedProducts(all, { search: 'apple DISPLAY' }).items.map((p) => p.productId)).toEqual(['p1']);
    expect(filterCachedProducts(all, { search: '4000000000031' }).items.map((p) => p.productId)).toEqual(['p3']);
    expect(filterCachedProducts(all, { category: 'Smartphone' }).items.map((p) => p.productId)).toEqual(['p3']);
    expect(filterCachedProducts(all, { includeOutOfStock: true, page: 2, pageSize: 2 })).toMatchObject({ page: 2, pageSize: 2, totalCount: 3 });
    expect(filterCachedProducts(all, { includeOutOfStock: true, page: 2, pageSize: 2 }).items.map((p) => p.productId)).toEqual(['p3']);
    expect(cachedCategories(all)).toEqual([{ category: 'Display', productCount: 1 }, { category: 'Smartphone', productCount: 1 }]);
  });

  it('finds products by EAN; devices sold by IMEI are not available offline', () => {
    expect(cachedLookup(all, ' 4000000000017 ')).toMatchObject({ kind: 'Product', productId: 'p1', priceMinor: 8990, available: 5 });
    expect(cachedLookup(all, '4000000000031')).toBeNull();
    expect(cachedLookup(all, '356938035643809')).toBeNull();
  });

  it('reduces the cached stock after an offline sale', () => {
    const after = reduceCachedStock(all, [{ productId: 'p1', quantity: 2 }, { productId: 'p1', quantity: 1 }, { productId: null, quantity: 9 }]);
    expect(after[0]).toMatchObject({ available: 2, onHand: 2 });
    expect(after[1]).toBe(all[1]);
  });

  it('keeps only name, company and city of customers on the device', () => {
    const cached = toCachedCustomer({ id: 'c1', name: 'Erika Meier', company: 'Meier GmbH', city: 'Berlin', email: 'e@x.test', phone: '0170' } as never);
    expect(cached).toEqual({ id: 'c1', name: 'Erika Meier', company: 'Meier GmbH', city: 'Berlin' });
    const page = filterCachedCustomers([cached, { id: 'c2', name: 'Ali', company: null, city: null }], 'meier ber');
    expect(page.totalCount).toBe(1);
    expect(page.items[0]).toMatchObject({ id: 'c1', name: 'Erika Meier', email: null, phone: null, street: null });
  });
});

describe('provisional receipt (thermal layout)', () => {
  const t = (key: string, params?: Record<string, string | number | null | undefined>) => key + (params ? ' ' + Object.values(params).join(' ') : '');

  it('says that it is not a till receipt and carries the offline number, never a receipt number or TSE data', () => {
    const layout = provisionalLayout(entry(), { width: 42, codepage: 'Cp858', t, locale: 'de-DE', sellerLines: ['Handyshop Mitte', 'Teststraße 1'] });
    const text = layout.items.map((i) => (i.kind === 'text' ? i.text : i.kind)).join('\n');
    expect(text.indexOf('pos.offline.receipt.title')).toBe(0);
    expect(text).toContain('pos.offline.receipt.notice');
    expect(text).toContain('OFF-7K2M-1');
    expect(text).toContain('Displayschutz');
    expect(text).toContain('25,00');
    expect(text).toContain('pos.receipt.change');
    expect(text).toContain('pos.offline.receipt.footer');
    expect(text).not.toContain('pos.receipt.number');
    expect(text).not.toContain('tse');
    expect(layout.items.some((i) => i.kind === 'qr')).toBe(false);
  });
});
