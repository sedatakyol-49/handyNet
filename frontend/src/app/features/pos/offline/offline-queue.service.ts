import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { toApiProblem } from '../../../core/api/api-problem';
import { PosApi } from '../../../core/api/handynet-api';
import { CreateSaleRequest, Receipt } from '../../../core/api/models';
import { SessionStore } from '../../../core/auth/session.store';
import { I18nService } from '../../../core/i18n/i18n.service';
import { StorageService } from '../../../core/storage.service';
import { posErrorText } from '../pos-error';
import { OfflineStore, MemoryStore, createOfflineStore } from './offline-db';
import {
  DiscardRecord,
  NewEntryInput,
  QueueEntry,
  QueueError,
  afterAttempt,
  discardRecord,
  isDeviceId,
  newDeviceId,
  newEntry,
  requeue,
  restoreEntries,
  scopeOf,
  syncOrder,
  syncOutcome,
} from './offline-queue.logic';
import { ReachabilityService } from './reachability.service';

const DEVICE_KEY = 'hn.pos.device';
const sequenceKey = (scope: string) => `hn.pos.offline.seq.${scope}`;
const RETRY_PAUSE_MS = 15_000;

/** A queued sale that was booked in this page session (shown to the cashier: reference → receipt number). */
export interface SyncedSale {
  reference: string;
  registerId: string;
  receiptId: string;
  receiptNumber: number;
  totalMinor: number;
  at: string;
}

/**
 * The offline queue of this device: sales recorded while the API was unreachable. Entries live in IndexedDB (they
 * survive reloads and browser restarts) and are sent in order with their original Idempotency-Key as soon as the API
 * is reachable again. Nothing is ever dropped silently: an entry leaves the queue only when the API booked it or when
 * a manager discards it with a reason (after it was saved as a file).
 */
@Injectable({ providedIn: 'root' })
export class OfflineQueueService {
  private readonly api = inject(PosApi);
  private readonly session = inject(SessionStore);
  private readonly storage = inject(StorageService);
  private readonly i18n = inject(I18nService);
  private readonly reach = inject(ReachabilityService);
  private store: OfflineStore = this.storage.isBrowser ? createOfflineStore() : new MemoryStore();
  private loadedFor: string | null = null;
  private loading: Promise<void> | null = null;
  private running: Promise<void> | null = null;
  private retryTimer: ReturnType<typeof setTimeout> | null = null;
  /** After a run stopped on an unreachable API: do not hammer it, the timer (or "sync now") tries again. */
  private pausedUntil = 0;

  /** False when the browser has no usable IndexedDB: offline sales are refused (they would not survive a restart). */
  readonly persistent = signal(this.store.persistent);
  /** Entries of the selected organization on this device, oldest first. */
  readonly entries = signal<QueueEntry[]>([]);
  readonly syncing = signal(false);
  readonly synced = signal<SyncedSale[]>([]);
  readonly waiting = computed(() => this.entries().filter((e) => e.status !== 'attention').length);
  readonly attention = computed(() => this.entries().filter((e) => e.status === 'attention').length);

  constructor() {
    if (!this.storage.isBrowser) return;
    // Queue of the selected organization; another organization on the same device has its own entries.
    effect(() => {
      const org = this.session.isAuthenticated() ? this.session.organizationId() : null;
      untracked(() => void this.load(org));
    });
    // Back online: book what is waiting.
    effect(() => {
      const online = this.reach.online();
      const waiting = this.entries().some((e) => e.status === 'queued');
      if (online && waiting && Date.now() >= this.pausedUntil) untracked(() => void this.sync());
    });
  }

  /** Entries of one register. */
  forRegister(registerId: string): QueueEntry[] {
    return this.entries().filter((e) => e.registerId === registerId);
  }

  /** Makes sure the queue of the selected organization is loaded (used before decisions such as "may close the session"). */
  ready(): Promise<void> {
    return this.loading ?? this.load(this.session.organizationId());
  }

  private load(organizationId: string | null): Promise<void> {
    this.loading = (async () => {
      if (!organizationId) {
        this.loadedFor = null;
        this.entries.set([]);
        return;
      }
      try {
        const raw = await this.store.entries(organizationId);
        this.entries.set(restoreEntries(raw));
      } catch {
        // IndexedDB exists but cannot be used (blocked storage): fall back to memory and say so.
        this.store = new MemoryStore();
        this.persistent.set(false);
        this.entries.set([]);
      }
      this.loadedFor = organizationId;
    })();
    return this.loading;
  }

  /** The 4-character id of this browser profile (part of every offline reference). */
  deviceId(): string {
    const stored = this.storage.get(DEVICE_KEY);
    if (isDeviceId(stored)) return stored;
    const id = newDeviceId();
    this.storage.set(DEVICE_KEY, id);
    return id;
  }

  /** Records a sale on the device. Resolves once it is safely stored. */
  async enqueue(input: Omit<NewEntryInput, 'sequence' | 'deviceId'>): Promise<QueueEntry> {
    await this.ready();
    const scope = scopeOf(input.organizationId, input.registerId);
    const deviceId = this.deviceId();
    // The running number is kept in IndexedDB and in localStorage: clearing one of them never reuses a number.
    const known = Math.max(Number(this.storage.get(sequenceKey(scope))) || 0, ...this.entries().filter((e) => e.scope === scope).map((e) => e.sequence), 0);
    const entry = await this.store.add(scope, known, (sequence) => newEntry({ ...input, sequence, deviceId }));
    this.storage.set(sequenceKey(scope), String(entry.sequence));
    this.entries.update((list) => [...list, entry]);
    return entry;
  }

  /**
   * Sends the waiting entries, oldest first (single flight). Stops at the first entry whose outcome is unknown or
   * temporary — the connection is gone again; entries the API refuses are marked "needs attention" and the run goes on.
   */
  sync(registerId: string | null = null): Promise<void> {
    this.pausedUntil = 0;
    this.running ??= this.run(registerId).finally(() => {
      this.running = null;
      this.syncing.set(false);
    });
    return this.running;
  }

  private async run(registerId: string | null): Promise<void> {
    await this.ready();
    this.syncing.set(true);
    for (const queued of syncOrder(this.entries(), registerId)) {
      const entry: QueueEntry = { ...queued, status: 'syncing' };
      this.replace(entry);
      const result = await this.send(entry.body, entry.id);
      let outcome = syncOutcome(result.status, result.code, !!entry.onlineBody);
      let final = result;
      if (outcome === 'replayOnline') {
        // The first, online attempt of this sale did reach the API: the same key returns that receipt.
        final = await this.send(JSON.parse(entry.onlineBody!) as CreateSaleRequest, entry.id);
        outcome = syncOutcome(final.status, final.code, false);
      }
      if (outcome === 'synced' && final.receipt) {
        await this.store.remove(entry.id).catch(() => undefined);
        this.entries.update((list) => list.filter((e) => e.id !== entry.id));
        const r = final.receipt;
        this.synced.update((list) => [{ reference: entry.reference, registerId: entry.registerId, receiptId: r.id, receiptNumber: r.number, totalMinor: r.totalGrossMinor, at: new Date().toISOString() }, ...list].slice(0, 50));
        continue;
      }
      const error: QueueError = { status: final.status, code: final.code, message: final.message };
      const next = afterAttempt(entry, outcome === 'attention' ? 'attention' : 'retryLater', new Date().toISOString(), error);
      await this.store.put(next).catch(() => undefined);
      this.replace(next);
      if (outcome !== 'attention') return this.pause(); // unreachable again: keep the order, try later
    }
  }

  /** The API did not answer (or only temporarily refused): wait before the next automatic run. */
  private pause(): void {
    this.pausedUntil = Date.now() + RETRY_PAUSE_MS;
    if (this.retryTimer) clearTimeout(this.retryTimer);
    this.retryTimer = setTimeout(() => {
      this.retryTimer = null;
      if (this.reach.online() && this.entries().some((e) => e.status === 'queued')) void this.sync();
    }, RETRY_PAUSE_MS);
  }

  private async send(body: CreateSaleRequest, key: string): Promise<{ status: number; code: string | null; message: string; receipt: Receipt | null }> {
    try {
      const receipt = await firstValueFrom(this.api.sell(body, key));
      return { status: 200, code: null, message: '', receipt };
    } catch (e) {
      const status = e instanceof HttpErrorResponse ? e.status : 0;
      return { status, code: toApiProblem(e).code, message: posErrorText(this.i18n, e) ?? '', receipt: null };
    }
  }

  /** "Retry" on an entry that needs attention. */
  async retry(id: string): Promise<void> {
    const entry = this.entries().find((e) => e.id === id);
    if (!entry || entry.status !== 'attention') return;
    const next = requeue(entry);
    await this.store.put(next);
    this.replace(next);
    await this.sync();
  }

  /**
   * First step of discarding: the record to save as a file (entry, reason, who, when). Nothing is removed yet — the
   * sale never reached the API, so this file is the only trace of it.
   */
  discardRecordFor(id: string, reason: string, by: string | null): DiscardRecord | null {
    const entry = this.entries().find((e) => e.id === id);
    return entry && entry.status !== 'syncing' ? discardRecord(entry, reason, by, new Date().toISOString()) : null;
  }

  /** Second step: removes the entry from the device for good (after the record was saved). */
  async discard(id: string): Promise<boolean> {
    const entry = this.entries().find((e) => e.id === id);
    if (!entry || entry.status === 'syncing') return false;
    await this.store.remove(id);
    this.entries.update((list) => list.filter((e) => e.id !== id));
    return true;
  }

  /** Cached register data (register, session, products, customers) shares the store. */
  cacheGet<T>(key: string): Promise<{ savedAt: string; data: T } | null> {
    return this.store.cacheGet<T>(key).catch(() => null);
  }

  cachePut(key: string, data: unknown): Promise<void> {
    return this.store.cachePut(key, data).catch(() => undefined);
  }

  private replace(entry: QueueEntry): void {
    this.entries.update((list) => list.map((e) => (e.id === entry.id ? entry : e)));
  }
}
