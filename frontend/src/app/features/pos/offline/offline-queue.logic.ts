import { CreateSaleRequest, Paged, PosCategoryCount, PosCustomer, PosLookup, PosPaymentMethod, PosProduct, SalePayment } from '../../../core/api/models';
import { CartLine, lineAmounts, lookupFromProduct } from '../pos.logic';

/**
 * Offline mode of the till (pure rules): sales recorded while the API is unreachable wait in a queue on the device and
 * are booked on reconnect with their original Idempotency-Key. The receipt gets its number and its TSE signature only
 * then — what the customer gets before is a provisional receipt.
 */

// ---------------------------------------------------------------- reference numbering

/** Unambiguous characters for the device id (no 0/O, 1/I/L). */
const DEVICE_ALPHABET = 'ABCDEFGHJKMNPQRSTUVWXYZ23456789';

/** A 4-character id of this browser profile, part of every offline reference. */
export function newDeviceId(random: () => number = Math.random): string {
  let id = '';
  for (let i = 0; i < 4; i++) id += DEVICE_ALPHABET[Math.floor(random() * DEVICE_ALPHABET.length) % DEVICE_ALPHABET.length];
  return id;
}

export function isDeviceId(value: unknown): value is string {
  return typeof value === 'string' && /^[A-Z0-9]{4}$/.test(value);
}

/** `OFF-<device>-<running number>`, e.g. OFF-7K2M-12 (API rule: 1–40 characters of A-Z a-z 0-9 -). */
export function offlineReference(deviceId: string, sequence: number): string {
  return `OFF-${deviceId}-${sequence}`;
}

export function isOfflineReference(value: string): boolean {
  return /^[A-Za-z0-9-]{1,40}$/.test(value);
}

/**
 * The next running number: one more than the highest number this device knows from any of its stores (IndexedDB,
 * localStorage, entries still queued) — so clearing one store never reuses a number.
 */
export function nextSequence(...known: (number | null | undefined)[]): number {
  let max = 0;
  for (const n of known) if (typeof n === 'number' && Number.isSafeInteger(n) && n > max) max = n;
  return max + 1;
}

// ---------------------------------------------------------------- queue entries

/** queued = waits for the connection; syncing = being sent; attention = the API refused it, a person has to decide. */
export type QueueStatus = 'queued' | 'syncing' | 'attention';

export interface QueueLine {
  description: string;
  quantity: number;
  unitPriceMinor: number;
  discountMinor: number;
  lineGrossMinor: number;
  imei: string | null;
  note: string | null;
}

export interface QueuePayment {
  method: PosPaymentMethod;
  amountMinor: number;
  tenderedMinor: number | null;
}

export interface QueueError {
  status: number;
  code: string | null;
  message: string;
}

export interface QueueEntry {
  /** Also the Idempotency-Key of the sale request. */
  id: string;
  /** `<organizationId>.<registerId>`. */
  scope: string;
  organizationId: string;
  registerId: string;
  registerName: string | null;
  sequence: number;
  reference: string;
  /** ISO instant of the sale on the device. */
  recordedAt: string;
  /** The request exactly as it will be sent (with `offline`). */
  body: CreateSaleRequest;
  /**
   * The same sale as it was first sent online with this key, when that request's outcome is unknown (network error).
   * If the API answers that the key belongs to another request, the online request did arrive: it is replayed.
   */
  onlineBody?: string | null;
  status: QueueStatus;
  attempts: number;
  lastAttemptAt: string | null;
  error: QueueError | null;
  /** What the provisional receipt and the queue page show (no customer data). */
  lines: QueueLine[];
  payments: QueuePayment[];
  totalMinor: number;
  changeMinor: number;
  roundingMinor: number;
  /** Who recorded the sale on the device (the receipt is booked under whoever is signed in when it syncs). */
  cashierName: string | null;
}

export function scopeOf(organizationId: string, registerId: string): string {
  return `${organizationId}.${registerId}`;
}

/** The sale request of an offline sale: the online body plus when and under which reference it was recorded. */
export function offlineBody(body: CreateSaleRequest, recordedAt: string, reference: string): CreateSaleRequest {
  return { ...body, offline: { recordedAt, reference } };
}

export interface NewEntryInput {
  id: string;
  organizationId: string;
  registerId: string;
  registerName: string | null;
  sequence: number;
  deviceId: string;
  recordedAt: string;
  body: CreateSaleRequest;
  onlineBody?: string | null;
  lines: readonly CartLine[];
  payments: readonly SalePayment[];
  tenderedCashMinor: number;
  changeMinor: number;
  roundingMinor: number;
  cashierName: string | null;
}

export function newEntry(i: NewEntryInput): QueueEntry {
  const reference = offlineReference(i.deviceId, i.sequence);
  const lines: QueueLine[] = i.lines.map((l) => {
    const a = lineAmounts(l);
    return { description: l.description, quantity: l.quantity, unitPriceMinor: a.unitPriceMinor ?? 0, discountMinor: a.discountMinor, lineGrossMinor: a.grossMinor ?? 0, imei: l.imei, note: l.note?.trim() || null };
  });
  return {
    id: i.id,
    scope: scopeOf(i.organizationId, i.registerId),
    organizationId: i.organizationId,
    registerId: i.registerId,
    registerName: i.registerName,
    sequence: i.sequence,
    reference,
    recordedAt: i.recordedAt,
    body: offlineBody(i.body, i.recordedAt, reference),
    onlineBody: i.onlineBody ?? null,
    status: 'queued',
    attempts: 0,
    lastAttemptAt: null,
    error: null,
    lines,
    payments: i.payments.map((p) => ({ method: p.method, amountMinor: p.amountMinor, tenderedMinor: p.method === 'Cash' ? i.tenderedCashMinor : null })),
    totalMinor: lines.reduce((sum, l) => sum + l.lineGrossMinor, 0),
    changeMinor: i.changeMinor,
    roundingMinor: i.roundingMinor,
    cashierName: i.cashierName,
  };
}

/** Defensive check of what comes back from IndexedDB. */
export function isQueueEntry(v: unknown): v is QueueEntry {
  if (!v || typeof v !== 'object') return false;
  const e = v as QueueEntry;
  return (
    typeof e.id === 'string' &&
    typeof e.scope === 'string' &&
    typeof e.registerId === 'string' &&
    typeof e.organizationId === 'string' &&
    Number.isSafeInteger(e.sequence) &&
    typeof e.reference === 'string' &&
    typeof e.recordedAt === 'string' &&
    !!e.body &&
    typeof e.body === 'object' &&
    Array.isArray(e.body.lines) &&
    (e.status === 'queued' || e.status === 'syncing' || e.status === 'attention') &&
    Array.isArray(e.lines) &&
    Array.isArray(e.payments)
  );
}

/** Entries as loaded after a (re)start: a sync that was interrupted mid-request simply waits again (same key → safe). */
export function restoreEntries(raw: readonly unknown[]): QueueEntry[] {
  return raw
    .filter(isQueueEntry)
    .map((e) => (e.status === 'syncing' ? { ...e, status: 'queued' as const } : e))
    .sort(bySequence);
}

export function bySequence(a: QueueEntry, b: QueueEntry): number {
  return a.recordedAt === b.recordedAt ? a.sequence - b.sequence : a.recordedAt < b.recordedAt ? -1 : 1;
}

/** The entries a sync run sends, oldest first. Entries that need attention are only sent after "retry". */
export function syncOrder(entries: readonly QueueEntry[], registerId: string | null = null): QueueEntry[] {
  return entries.filter((e) => e.status === 'queued' && (!registerId || e.registerId === registerId)).sort(bySequence);
}

// ---------------------------------------------------------------- sync state machine

/**
 * What an answer to a sync request means:
 *   synced      – 2xx: booked (or replayed), the entry leaves the queue;
 *   retryLater  – outcome unknown or temporary (no connection, 5xx, 401 while the session refreshes, 408, 429):
 *                 the entry stays queued and the run stops — the next entries would fail the same way;
 *   replayOnline – the key was already used by the first, online attempt of this sale: send that body again;
 *   attention   – the API refused the sale (stock, closed session, rights, …): a person has to decide.
 */
export type SyncOutcome = 'synced' | 'retryLater' | 'replayOnline' | 'attention';

export function syncOutcome(status: number, code: string | null, hasOnlineBody: boolean): SyncOutcome {
  if (status >= 200 && status < 300) return 'synced';
  if (status <= 0 || status >= 500 || status === 401 || status === 408 || status === 429) return 'retryLater';
  if (code === 'pos.idempotency_key_reused' && hasOnlineBody) return 'replayOnline';
  return 'attention';
}

export function afterAttempt(entry: QueueEntry, outcome: Exclude<SyncOutcome, 'synced' | 'replayOnline'>, now: string, error: QueueError | null): QueueEntry {
  return {
    ...entry,
    status: outcome === 'attention' ? 'attention' : 'queued',
    attempts: entry.attempts + 1,
    lastAttemptAt: now,
    error: outcome === 'attention' ? error : (error ?? entry.error),
  };
}

/** "Retry" on an entry that needs attention: it waits for the next sync run again. */
export function requeue(entry: QueueEntry): QueueEntry {
  return { ...entry, status: 'queued', error: null };
}

// ---------------------------------------------------------------- may the till sell offline?

export type OfflineBlock = 'notEnabled' | 'noSession' | 'noStorage' | 'payment' | 'cardUnreported';

/**
 * Offline sales need the register's offline mode, a cash session that was open when the till last reached the API,
 * storage that survives a restart, and only cash or card payments.
 */
export function offlineBlock(o: { offlineMode: boolean; sessionOpen: boolean; persistent: boolean; methods: readonly string[]; cardUnreported?: boolean }): OfflineBlock | null {
  if (!o.offlineMode) return 'notEnabled';
  if (!o.sessionOpen) return 'noSession';
  if (!o.persistent) return 'noStorage';
  if (o.methods.some((m) => m !== 'Cash' && m !== 'Card')) return 'payment';
  if (o.cardUnreported) return 'cardUnreported';
  return null;
}

// ---------------------------------------------------------------- export before discarding

export interface DiscardRecord {
  kind: 'handynet-offline-sale-discarded';
  discardedAt: string;
  discardedBy: string | null;
  reason: string;
  entry: QueueEntry;
}

/** What is saved as a file before an entry is removed from the device (the sale itself never reached the API). */
export function discardRecord(entry: QueueEntry, reason: string, by: string | null, now: string): DiscardRecord {
  return { kind: 'handynet-offline-sale-discarded', discardedAt: now, discardedBy: by, reason: reason.trim(), entry };
}

export function discardReasonError(reason: string): 'required' | 'tooLong' | null {
  const text = reason.trim();
  return text.length < 3 ? 'required' : text.length > 500 ? 'tooLong' : null;
}

// ---------------------------------------------------------------- cached register data

/** What the product grid needs offline (the API's own search runs on the server). */
export function filterCachedProducts(
  all: readonly PosProduct[],
  q: { search?: string | null; category?: string | null; includeOutOfStock?: boolean; page?: number; pageSize?: number },
): Paged<PosProduct> {
  const words = (q.search ?? '').toLowerCase().split(/\s+/).filter(Boolean);
  const matches = all.filter((p) => {
    if (!q.includeOutOfStock && p.available <= 0) return false;
    if (q.category && p.category !== q.category) return false;
    const text = `${p.brand} ${p.name} ${p.ean ?? ''} ${p.mpn ?? ''}`.toLowerCase();
    return words.every((w) => text.includes(w));
  });
  const pageSize = Math.max(1, q.pageSize ?? 48);
  const page = Math.max(1, q.page ?? 1);
  return { items: matches.slice((page - 1) * pageSize, page * pageSize), page, pageSize, totalCount: matches.length };
}

export function cachedCategories(all: readonly PosProduct[]): PosCategoryCount[] {
  const counts = new Map<string, number>();
  for (const p of all) if (p.available > 0) counts.set(p.category, (counts.get(p.category) ?? 0) + 1);
  return [...counts].map(([category, productCount]) => ({ category, productCount })).sort((a, b) => a.category.localeCompare(b.category));
}

/** EAN scan offline: the product from the cached grid. Devices (IMEI) are not cached — null. */
export function cachedLookup(all: readonly PosProduct[], code: string): PosLookup | null {
  const p = all.find((x) => x.ean === code.trim() && !x.serialized);
  return p ? lookupFromProduct(p) : null;
}

/** After an offline sale: the cached stock of the sold products goes down (the API decides for real at sync). */
export function reduceCachedStock(all: readonly PosProduct[], sold: readonly { productId?: string | null; quantity: number }[]): PosProduct[] {
  const byProduct = new Map<string, number>();
  for (const s of sold) if (s.productId) byProduct.set(s.productId, (byProduct.get(s.productId) ?? 0) + s.quantity);
  return all.map((p) => {
    const q = byProduct.get(p.productId);
    return q ? { ...p, onHand: p.onHand - q, available: p.available - q } : p;
  });
}

/** The customer fields kept on the device for the picker (no email, phone or address). */
export interface CachedCustomer {
  id: string;
  name: string;
  company: string | null;
  city: string | null;
}

export function toCachedCustomer(c: Pick<PosCustomer, 'id' | 'name' | 'company' | 'city'>): CachedCustomer {
  return { id: c.id, name: c.name, company: c.company ?? null, city: c.city ?? null };
}

export function filterCachedCustomers(all: readonly CachedCustomer[], search: string | null | undefined, page = 1, pageSize = 20): Paged<PosCustomer> {
  const words = (search ?? '').toLowerCase().split(/\s+/).filter(Boolean);
  const matches = all.filter((c) => words.every((w) => `${c.name} ${c.company ?? ''} ${c.city ?? ''}`.toLowerCase().includes(w)));
  const items = matches.slice((page - 1) * pageSize, page * pageSize).map(
    (c): PosCustomer => ({ id: c.id, name: c.name, company: c.company, email: null, phone: null, street: null, postalCode: null, city: c.city, countryCode: null, vatId: null, createdAt: '', updatedAt: '' }),
  );
  return { items, page, pageSize, totalCount: matches.length };
}

/** The figures of a settled payment the offline path needs (subset of `settlePayments()`). */
export interface PaymentLinesSettlementLike {
  payments: SalePayment[];
  cashMinor: number;
  changeMinor: number;
  roundingMinor?: number;
}
