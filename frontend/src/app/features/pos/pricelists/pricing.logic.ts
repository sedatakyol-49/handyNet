import { PriceSource } from '../../../core/api/models';
import { CartLine, lineAmounts, minorToPlain, parsePercentToBasisPoints } from '../pos.logic';

/**
 * Pricelists (customer-specific prices, ADR-0020): re-pricing the cart when the customer changes, and the pricelist form.
 */

/** The till price of a product for the ticket's customer (`GET /api/pos/prices/{id}?customerId=`). */
export interface ResolvedPrice {
  priceMinor: number | null;
  priceSource: PriceSource;
  /** The price without a pricelist (null = none). */
  basePriceMinor: number | null;
  pricelistName: string | null;
}

/** Did the cashier type another price than the till's (a manual override that a customer change must not touch)? */
export function isOverridden(line: Pick<CartLine, 'priceInput' | 'quantity' | 'discountInput' | 'discountKind' | 'listPriceMinor'>): boolean {
  if (line.listPriceMinor === null || line.listPriceMinor === undefined) return line.priceInput.trim() !== '';
  return lineAmounts(line).unitPriceMinor !== line.listPriceMinor;
}

/**
 * Applies the prices of the new customer: lines that still carry the till price follow the new price; lines with a
 * manual price keep it (their reference price is updated, so the price-override check stays right). Gift card lines and
 * lines without a resolved price are left alone. Returns the new lines and how many prices changed.
 */
export function repriceLines(lines: readonly CartLine[], prices: ReadonlyMap<string, ResolvedPrice>): { lines: CartLine[]; changed: number } {
  let changed = 0;
  const out = lines.map((l) => {
    if (l.kind === 'GiftCard') return l;
    const p = prices.get(l.productId);
    if (!p) return l;
    const follow = !isOverridden(l) && p.priceMinor !== null;
    const next: CartLine = {
      ...l,
      listPriceMinor: p.priceMinor,
      priceSource: p.priceSource,
      basePriceMinor: p.priceSource === 'Pricelist' ? p.basePriceMinor : null,
      pricelistName: p.priceSource === 'Pricelist' ? p.pricelistName : null,
      priceInput: follow ? minorToPlain(p.priceMinor!) : l.priceInput,
    };
    if (next.priceInput !== l.priceInput) changed++;
    return next;
  });
  return { lines: out, changed };
}

/** Product ids whose price depends on the customer (one price request per product). */
export function repriceTargets(lines: readonly CartLine[]): string[] {
  return [...new Set(lines.filter((l) => l.kind !== 'GiftCard' && !!l.productId).map((l) => l.productId))];
}

// ---------------------------------------------------------------- pricelist form

export interface PricelistForm {
  name: string;
  discountPercent: string;
  isActive: boolean;
  validFrom: string;
  validTo: string;
}

export interface PricelistRequest {
  name: string;
  discountBasisPoints: number;
  isActive: boolean;
  validFrom: string | null;
  validTo: string | null;
  version?: number | null;
}

export type PricelistFormErrors = Partial<Record<'name' | 'discountPercent' | 'validTo', string>>;

export const PRICELIST_LIMITS = { name: 100, maxBasisPoints: 10_000 } as const;

export function basisPointsToPercent(bp: number): string {
  const whole = Math.floor(bp / 100);
  const rest = bp % 100;
  return rest ? `${whole},${String(rest).padStart(2, '0').replace(/0$/, '')}` : String(whole);
}

export function pricelistInput(f: PricelistForm, version: number | null): { input: PricelistRequest | null; errors: PricelistFormErrors } {
  const errors: PricelistFormErrors = {};
  const name = f.name.trim();
  if (!name || name.length > PRICELIST_LIMITS.name) errors.name = 'pos.pricelists.form.errors.name';
  const bp = f.discountPercent.trim() === '' ? 0 : parsePercentToBasisPoints(f.discountPercent);
  if (bp === null || bp < 0 || bp > PRICELIST_LIMITS.maxBasisPoints) errors.discountPercent = 'pos.pricelists.form.errors.discount';
  if (f.validFrom && f.validTo && f.validTo < f.validFrom) errors.validTo = 'pos.pricelists.form.errors.validity';
  if (Object.keys(errors).length) return { input: null, errors };
  return { errors, input: { name, discountBasisPoints: bp!, isActive: f.isActive, validFrom: f.validFrom || null, validTo: f.validTo || null, version } };
}

/** Price the list gives a product without an item price: base − round_half_away(base × bp / 10000). */
export function discountedPrice(baseMinor: number, basisPoints: number): number {
  return baseMinor - Math.floor((baseMinor * basisPoints * 2 + 10_000) / 20_000);
}
