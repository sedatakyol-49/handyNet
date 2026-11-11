import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, defer, firstValueFrom, from, tap, throwError } from 'rxjs';
import { PosApi } from '../../../core/api/handynet-api';
import { CashSession, Paged, PosCategoryCount, PosCustomer, PosLookup, PosProduct, PosRegister } from '../../../core/api/models';
import { SessionStore } from '../../../core/auth/session.store';
import {
  CachedCustomer,
  cachedCategories,
  cachedLookup,
  filterCachedCustomers,
  filterCachedProducts,
  reduceCachedStock,
  toCachedCustomer,
} from './offline-queue.logic';
import { OfflineQueueService } from './offline-queue.service';
import { ReachabilityService, isUnreachable } from './reachability.service';

/** The API allows at most 200 rows per page; the cache holds up to this many products / customers of a register. */
const PAGE = 200;
const MAX_PRODUCTS = 4000;
const MAX_CUSTOMERS = 1000;
const REFRESH_EVERY_MS = 10 * 60_000;

interface CachedRegister {
  register: PosRegister;
  /** The open session when the till last reached the API (null = none). */
  session: CashSession | null;
}

/** Thrown for a scan that needs the API (device by IMEI) while the till is offline. */
export class OfflineLookupError extends Error {
  constructor(readonly reason: 'notCached' | 'serialized') {
    super(reason);
  }
}

/**
 * The till's read access to register data with an offline fallback: while the API is reachable every call goes to the
 * API (and refreshes the cache for registers in offline mode); when it is not, the last cached data answers — the
 * register, its open session, the product grid (search, categories, EAN scan) and a minimal customer list.
 */
@Injectable({ providedIn: 'root' })
export class PosOfflineData {
  private readonly api = inject(PosApi);
  private readonly queue = inject(OfflineQueueService);
  private readonly reach = inject(ReachabilityService);
  private readonly session = inject(SessionStore);
  private readonly refreshedAt = new Map<string, number>();
  private readonly refreshing = new Set<string>();
  private readonly live = new Map<string, { register?: PosRegister; session?: CashSession | null }>();

  private key(kind: string, registerId = ''): string {
    return `${this.session.organizationId() ?? 'none'}:${kind}:${registerId}`;
  }

  /** API first; the cache answers when the API is unreachable (and right away while the till knows it is offline). */
  private withFallback<T>(online: () => Observable<T>, cached: () => Promise<T | undefined>): Observable<T> {
    const fromCache = (error: unknown): Observable<T> =>
      from(
        cached().then((value) => {
          if (value === undefined) throw error; // nothing cached: the caller sees the original failure
          return value;
        }),
      );
    return defer(() => {
      if (!this.reach.online()) return fromCache(new HttpErrorResponse({ status: 0, statusText: 'offline' }));
      return online().pipe(catchError((e: unknown) => (e instanceof HttpErrorResponse && isUnreachable(e.status) ? fromCache(e) : throwError(() => e))));
    });
  }

  register(id: string): Observable<PosRegister> {
    return this.withFallback(
      () => this.api.register(id).pipe(tap((register) => void this.patchRegister(id, { register }))),
      async () => (await this.queue.cacheGet<CachedRegister>(this.key('register', id)))?.data.register,
    );
  }

  /** The open session; offline: the session that was open when the till last reached the API. */
  currentSession(registerId: string): Observable<CashSession | null> {
    return this.withFallback(
      () => this.api.currentSession(registerId).pipe(tap((session) => void this.patchRegister(registerId, { session: session ?? null }))),
      async () => {
        const cached = await this.queue.cacheGet<CachedRegister>(this.key('register', registerId));
        return cached ? cached.data.session : undefined;
      },
    );
  }

  /**
   * Register and session arrive in two requests: both are merged in memory and written once both are known, so the
   * cached copy always pairs a register with the session state seen at the same time.
   */
  private async patchRegister(id: string, patch: { register?: PosRegister; session?: CashSession | null }): Promise<void> {
    const live = { ...(this.live.get(id) ?? {}), ...patch };
    this.live.set(id, live);
    // Only registers in offline mode keep data on the device.
    if (live.register?.offlineMode && live.session !== undefined) {
      await this.queue.cachePut(this.key('register', id), { register: live.register, session: live.session } satisfies CachedRegister);
    }
  }

  products(registerId: string, q: { search?: string | null; category?: string | null; includeOutOfStock?: boolean; page?: number; pageSize?: number }): Observable<Paged<PosProduct>> {
    return this.withFallback(
      () => this.api.products(registerId, q),
      async () => {
        const all = await this.cachedProducts(registerId);
        return all ? filterCachedProducts(all, q) : undefined;
      },
    );
  }

  productCategories(registerId: string): Observable<PosCategoryCount[]> {
    return this.withFallback(
      () => this.api.productCategories(registerId),
      async () => {
        const all = await this.cachedProducts(registerId);
        return all ? cachedCategories(all) : undefined;
      },
    );
  }

  /** EAN / IMEI scan. Offline only products of the cached grid can be found; devices (IMEI) need the API. */
  lookup(code: string, registerId: string): Observable<PosLookup> {
    return this.withFallback(
      () => this.api.lookup(code, registerId),
      async () => {
        const all = await this.cachedProducts(registerId);
        if (!all) return undefined;
        const found = cachedLookup(all, code);
        if (found) return found;
        throw new OfflineLookupError(/^\d{15}$/.test(code.trim()) ? 'serialized' : 'notCached');
      },
    );
  }

  customers(q: { search?: string | null; page?: number; pageSize?: number }): Observable<Paged<PosCustomer>> {
    return this.withFallback(
      () => this.api.customers(q),
      async () => {
        const all = (await this.queue.cacheGet<CachedCustomer[]>(this.key('customers')))?.data;
        return all ? filterCachedCustomers(all, q.search, q.page, q.pageSize) : undefined;
      },
    );
  }

  private async cachedProducts(registerId: string): Promise<PosProduct[] | undefined> {
    return (await this.queue.cacheGet<PosProduct[]>(this.key('products', registerId)))?.data;
  }

  /** When the cache of a register was last refreshed (ISO), or null. */
  async cachedAt(registerId: string): Promise<string | null> {
    return (await this.queue.cacheGet<PosProduct[]>(this.key('products', registerId)))?.savedAt ?? null;
  }

  /**
   * Refreshes the offline copy of a register in offline mode: all products in stock with their till prices and a
   * minimal customer list. Runs in the background; a failure only means the previous copy stays.
   */
  async refresh(register: Pick<PosRegister, 'id' | 'offlineMode'>, force = false): Promise<void> {
    if (!register.offlineMode || !this.reach.online() || this.refreshing.has(register.id)) return;
    if (!force && Date.now() - (this.refreshedAt.get(register.id) ?? 0) < REFRESH_EVERY_MS) return;
    this.refreshing.add(register.id);
    try {
      const products: PosProduct[] = [];
      for (let page = 1; products.length < MAX_PRODUCTS; page++) {
        const r = await firstValueFrom(this.api.products(register.id, { page, pageSize: PAGE }));
        products.push(...r.items);
        if (!r.items.length || products.length >= r.totalCount) break;
      }
      await this.queue.cachePut(this.key('products', register.id), products);
      const customers: CachedCustomer[] = [];
      for (let page = 1; customers.length < MAX_CUSTOMERS; page++) {
        const r = await firstValueFrom(this.api.customers({ page, pageSize: PAGE }));
        customers.push(...r.items.map(toCachedCustomer));
        if (!r.items.length || customers.length >= r.totalCount) break;
      }
      await this.queue.cachePut(this.key('customers'), customers);
      this.refreshedAt.set(register.id, Date.now());
    } catch {
      /* keep the previous copy */
    } finally {
      this.refreshing.delete(register.id);
    }
  }

  /** After an offline sale: the cached stock goes down so the grid does not offer what was just sold. */
  async sold(registerId: string, lines: readonly { productId?: string | null; quantity: number }[]): Promise<void> {
    const all = await this.cachedProducts(registerId);
    if (all) await this.queue.cachePut(this.key('products', registerId), reduceCachedStock(all, lines));
  }
}
