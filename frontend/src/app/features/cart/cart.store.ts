import { Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { Listing } from '../../core/api/models';
import { SessionStore } from '../../core/auth/session.store';
import { StorageService } from '../../core/storage.service';

/** A cart line keeps a snapshot of the listing so the cart renders without refetching. */
export interface CartItem {
  offerId: string;
  sellerOrganizationId: string;
  sellerName: string;
  sellerCity: string;
  productId: string;
  brand: string;
  productName: string;
  supplierSku: string;
  quality: string;
  condition: string;
  priceMinor: number;
  currency: string;
  minOrderQuantity: number;
  available: number;
  deliveryDays: number;
  quantity: number;
  /** Set when a refresh found the price changed since the item was added. */
  previousPriceMinor?: number | null;
}

export interface SellerGroup {
  sellerOrganizationId: string;
  sellerName: string;
  sellerCity: string;
  items: CartItem[];
  netTotalMinor: number;
  itemCount: number;
}

interface CartState {
  items: CartItem[];
  /** Idempotency key per seller; created on the first checkout attempt and reused for retries. */
  checkoutKeys: Record<string, string>;
}

const EMPTY: CartState = { items: [], checkoutKeys: {} };

/** One order = one seller, so the cart is presented and checked out per seller group. */
export function groupCartBySeller(items: readonly CartItem[]): SellerGroup[] {
  const groups = new Map<string, SellerGroup>();
  for (const item of items) {
    let group = groups.get(item.sellerOrganizationId);
    if (!group) {
      group = {
        sellerOrganizationId: item.sellerOrganizationId,
        sellerName: item.sellerName,
        sellerCity: item.sellerCity,
        items: [],
        netTotalMinor: 0,
        itemCount: 0,
      };
      groups.set(item.sellerOrganizationId, group);
    }
    group.items.push(item);
    group.netTotalMinor += item.priceMinor * item.quantity;
    group.itemCount += item.quantity;
  }
  return [...groups.values()].sort((a, b) => a.sellerName.localeCompare(b.sellerName, 'de'));
}

/** Clamps a requested quantity to [minOrderQuantity, available]; returns null if the offer cannot be ordered. */
export function clampQuantity(requested: number, minOrderQuantity: number, available: number): number | null {
  if (available < minOrderQuantity) return null;
  const q = Math.floor(Number.isFinite(requested) ? requested : minOrderQuantity);
  return Math.min(Math.max(q, minOrderQuantity), available);
}

function uuid(): string {
  if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) return crypto.randomUUID();
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
    const r = (Math.random() * 16) | 0;
    return (c === 'x' ? r : (r & 0x3) | 0x8).toString(16);
  });
}

/** Client-side cart as a signal store, persisted in localStorage per selected organization. */
@Injectable({ providedIn: 'root' })
export class CartStore {
  private readonly storage = inject(StorageService);
  private readonly session = inject(SessionStore);
  private readonly state = signal<CartState>(EMPTY);
  private loadedFor: string | null = null;

  readonly items = computed(() => this.state().items);
  readonly groups = computed(() => groupCartBySeller(this.state().items));
  readonly count = computed(() => this.state().items.length);

  constructor() {
    // Swap the cart whenever the acting organization changes.
    effect(() => {
      const org = this.session.organizationId();
      untracked(() => {
        this.loadedFor = org;
        this.state.set(org ? (this.storage.getJson<CartState>(this.key(org)) ?? EMPTY) : EMPTY);
      });
    });
  }

  quantityOf(offerId: string): number {
    return this.state().items.find((i) => i.offerId === offerId)?.quantity ?? 0;
  }

  add(listing: Listing, quantity: number): void {
    const existing = this.state().items.find((i) => i.offerId === listing.offerId);
    const q = clampQuantity((existing?.quantity ?? 0) + quantity, listing.minOrderQuantity, listing.available);
    if (q === null) return;
    const item: CartItem = {
      offerId: listing.offerId,
      sellerOrganizationId: listing.sellerOrganizationId,
      sellerName: listing.sellerName,
      sellerCity: listing.sellerCity,
      productId: listing.productId,
      brand: listing.brand,
      productName: listing.productName,
      supplierSku: listing.supplierSku,
      quality: listing.quality,
      condition: listing.condition,
      priceMinor: listing.priceMinor,
      currency: listing.currency,
      minOrderQuantity: listing.minOrderQuantity,
      available: listing.available,
      deliveryDays: listing.deliveryDays,
      quantity: q,
    };
    this.mutate((s) => ({
      ...s,
      items: existing ? s.items.map((i) => (i.offerId === item.offerId ? item : i)) : [...s.items, item],
    }));
  }

  setQuantity(offerId: string, quantity: number): void {
    this.mutate((s) => ({
      ...s,
      items: s.items.map((i) =>
        i.offerId === offerId ? { ...i, quantity: clampQuantity(quantity, i.minOrderQuantity, Math.max(i.available, i.minOrderQuantity)) ?? i.quantity } : i,
      ),
    }));
  }

  remove(offerId: string): void {
    this.mutate((s) => ({ ...s, items: s.items.filter((i) => i.offerId !== offerId) }));
  }

  /** Updates a line from a fresh listing (after a price/stock conflict). Returns true if something changed. */
  refreshFromListing(listing: Listing): boolean {
    let changed = false;
    this.mutate((s) => ({
      ...s,
      items: s.items.map((i) => {
        if (i.offerId !== listing.offerId) return i;
        const priceChanged = i.priceMinor !== listing.priceMinor;
        changed = priceChanged || i.available !== listing.available || i.minOrderQuantity !== listing.minOrderQuantity;
        return {
          ...i,
          priceMinor: listing.priceMinor,
          previousPriceMinor: priceChanged ? i.priceMinor : (i.previousPriceMinor ?? null),
          available: listing.available,
          minOrderQuantity: listing.minOrderQuantity,
          deliveryDays: listing.deliveryDays,
          // Keep the requested quantity when stock is too low, so the line stays flagged until the user acts.
          quantity: clampQuantity(i.quantity, listing.minOrderQuantity, listing.available) ?? i.quantity,
        };
      }),
    }));
    return changed;
  }

  /** Marks an offer that disappeared from the marketplace as unavailable. */
  markUnavailable(offerId: string): void {
    this.mutate((s) => ({ ...s, items: s.items.map((i) => (i.offerId === offerId ? { ...i, available: 0 } : i)) }));
  }

  acknowledgePrices(sellerId: string): void {
    this.mutate((s) => ({
      ...s,
      items: s.items.map((i) => (i.sellerOrganizationId === sellerId ? { ...i, previousPriceMinor: null } : i)),
    }));
  }

  /** Returns the idempotency key for this seller's checkout, creating it on the first attempt. */
  checkoutKey(sellerId: string): string {
    const existing = this.state().checkoutKeys[sellerId];
    if (existing) return existing;
    const key = uuid();
    this.mutate((s) => ({ ...s, checkoutKeys: { ...s.checkoutKeys, [sellerId]: key } }));
    return key;
  }

  /** Called after the server answered definitively (order created, or rejected with 4xx). */
  resetCheckoutKey(sellerId: string): void {
    this.mutate((s) => {
      const { [sellerId]: _removed, ...rest } = s.checkoutKeys;
      return { ...s, checkoutKeys: rest };
    });
  }

  /** Removes the seller's lines after a successful order. */
  completeCheckout(sellerId: string): void {
    this.mutate((s) => {
      const { [sellerId]: _removed, ...rest } = s.checkoutKeys;
      return { items: s.items.filter((i) => i.sellerOrganizationId !== sellerId), checkoutKeys: rest };
    });
  }

  private mutate(fn: (s: CartState) => CartState): void {
    this.state.update(fn);
    if (this.loadedFor) this.storage.setJson(this.key(this.loadedFor), this.state());
  }

  private key(org: string): string {
    return `hn.cart.${org}`;
  }
}
