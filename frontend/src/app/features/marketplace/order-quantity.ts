import { Listing } from '../../core/api/models';

export interface QuantityProblem {
  key: 'marketplace.qtyInvalid' | 'marketplace.qtyBelowMinimum' | 'marketplace.qtyAboveAvailable';
  params?: Record<string, number>;
}

/** Why `quantity` (on top of what is already in the cart) cannot be added; null when it can. */
export function quantityProblem(listing: Pick<Listing, 'minOrderQuantity' | 'available'>, quantity: number, inCart: number): QuantityProblem | null {
  if (!Number.isInteger(quantity) || quantity < 1) return { key: 'marketplace.qtyInvalid' };
  if (inCart + quantity < listing.minOrderQuantity) return { key: 'marketplace.qtyBelowMinimum', params: { min: listing.minOrderQuantity } };
  if (inCart + quantity > listing.available) return { key: 'marketplace.qtyAboveAvailable', params: { available: listing.available } };
  return null;
}
