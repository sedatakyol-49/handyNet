import { QueueEntry, nextSequence } from './offline-queue.logic';

/**
 * Storage of the offline queue and the cached register data on the device. IndexedDB survives reloads and browser
 * restarts; when it is unavailable (private mode, blocked storage, SSR) a memory store is used and `persistent` is
 * false — the till then refuses to sell offline, because a queued sale would not survive.
 */
export interface OfflineStore {
  readonly persistent: boolean;
  /** Every queued entry of an organization (all its registers on this device). */
  entries(organizationId: string): Promise<unknown[]>;
  /** Allocates the next running number of the scope and stores the entry built with it, in one transaction. */
  add(scope: string, knownSequence: number, build: (sequence: number) => QueueEntry): Promise<QueueEntry>;
  put(entry: QueueEntry): Promise<void>;
  remove(id: string): Promise<void>;
  cacheGet<T>(key: string): Promise<{ savedAt: string; data: T } | null>;
  cachePut(key: string, data: unknown): Promise<void>;
}

const DB_NAME = 'hn-pos-offline';
const DB_VERSION = 1;
const QUEUE = 'queue';
const CACHE = 'cache';
const META = 'meta';

function request<T>(req: IDBRequest<T>): Promise<T> {
  return new Promise((resolve, reject) => {
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}

function done(tx: IDBTransaction): Promise<void> {
  return new Promise((resolve, reject) => {
    tx.oncomplete = () => resolve();
    tx.onerror = () => reject(tx.error);
    tx.onabort = () => reject(tx.error ?? new Error('transaction aborted'));
  });
}

export class IndexedDbStore implements OfflineStore {
  readonly persistent = true;
  private db: Promise<IDBDatabase> | null = null;

  constructor(private readonly factory: IDBFactory) {}

  private open(): Promise<IDBDatabase> {
    this.db ??= new Promise<IDBDatabase>((resolve, reject) => {
      const req = this.factory.open(DB_NAME, DB_VERSION);
      req.onupgradeneeded = () => {
        const db = req.result;
        if (!db.objectStoreNames.contains(QUEUE)) db.createObjectStore(QUEUE, { keyPath: 'id' }).createIndex('organizationId', 'organizationId');
        if (!db.objectStoreNames.contains(CACHE)) db.createObjectStore(CACHE, { keyPath: 'key' });
        if (!db.objectStoreNames.contains(META)) db.createObjectStore(META, { keyPath: 'key' });
      };
      req.onsuccess = () => {
        // Another tab upgrading the schema must not be blocked by this connection.
        req.result.onversionchange = () => {
          req.result.close();
          this.db = null;
        };
        resolve(req.result);
      };
      req.onerror = () => reject(req.error);
      req.onblocked = () => reject(new Error('IndexedDB blocked'));
    }).catch((e) => {
      this.db = null;
      throw e;
    });
    return this.db;
  }

  async entries(organizationId: string): Promise<unknown[]> {
    const db = await this.open();
    return request(db.transaction(QUEUE, 'readonly').objectStore(QUEUE).index('organizationId').getAll(organizationId));
  }

  async add(scope: string, knownSequence: number, build: (sequence: number) => QueueEntry): Promise<QueueEntry> {
    const db = await this.open();
    const tx = db.transaction([QUEUE, META], 'readwrite');
    const meta = tx.objectStore(META);
    const key = `seq:${scope}`;
    const stored = (await request(meta.get(key))) as { key: string; value: number } | undefined;
    const sequence = nextSequence(stored?.value, knownSequence);
    const entry = build(sequence);
    meta.put({ key, value: sequence });
    tx.objectStore(QUEUE).add(entry);
    await done(tx);
    return entry;
  }

  async put(entry: QueueEntry): Promise<void> {
    const db = await this.open();
    const tx = db.transaction(QUEUE, 'readwrite');
    tx.objectStore(QUEUE).put(entry);
    await done(tx);
  }

  async remove(id: string): Promise<void> {
    const db = await this.open();
    const tx = db.transaction(QUEUE, 'readwrite');
    tx.objectStore(QUEUE).delete(id);
    await done(tx);
  }

  async cacheGet<T>(key: string): Promise<{ savedAt: string; data: T } | null> {
    const db = await this.open();
    const row = (await request(db.transaction(CACHE, 'readonly').objectStore(CACHE).get(key))) as { savedAt: string; data: T } | undefined;
    return row ? { savedAt: row.savedAt, data: row.data } : null;
  }

  async cachePut(key: string, data: unknown): Promise<void> {
    const db = await this.open();
    const tx = db.transaction(CACHE, 'readwrite');
    tx.objectStore(CACHE).put({ key, savedAt: new Date().toISOString(), data });
    await done(tx);
  }
}

/** Not persistent: unit tests, SSR and browsers without IndexedDB. */
export class MemoryStore implements OfflineStore {
  readonly persistent = false;
  private readonly queue = new Map<string, QueueEntry>();
  private readonly cache = new Map<string, { savedAt: string; data: unknown }>();
  private readonly sequences = new Map<string, number>();

  async entries(organizationId: string): Promise<unknown[]> {
    return [...this.queue.values()].filter((e) => e.organizationId === organizationId);
  }

  async add(scope: string, knownSequence: number, build: (sequence: number) => QueueEntry): Promise<QueueEntry> {
    const sequence = nextSequence(this.sequences.get(scope), knownSequence);
    const entry = build(sequence);
    this.sequences.set(scope, sequence);
    this.queue.set(entry.id, entry);
    return entry;
  }

  async put(entry: QueueEntry): Promise<void> {
    this.queue.set(entry.id, entry);
  }

  async remove(id: string): Promise<void> {
    this.queue.delete(id);
  }

  async cacheGet<T>(key: string): Promise<{ savedAt: string; data: T } | null> {
    return (this.cache.get(key) as { savedAt: string; data: T } | undefined) ?? null;
  }

  async cachePut(key: string, data: unknown): Promise<void> {
    this.cache.set(key, { savedAt: new Date().toISOString(), data: structuredCloneSafe(data) });
  }
}

function structuredCloneSafe<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T;
}

/** The store for this browser: IndexedDB when it exists, memory otherwise. */
export function createOfflineStore(): OfflineStore {
  try {
    if (typeof indexedDB !== 'undefined' && indexedDB) return new IndexedDbStore(indexedDB);
  } catch {
    /* access to indexedDB can throw when storage is blocked */
  }
  return new MemoryStore();
}
