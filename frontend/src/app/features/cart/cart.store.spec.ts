import { CartItem, clampQuantity, groupCartBySeller } from './cart.store';

function item(overrides: Partial<CartItem>): CartItem {
  return {
    offerId: 'o1',
    sellerOrganizationId: 's1',
    sellerName: 'Seller',
    sellerCity: 'Berlin',
    productId: 'p1',
    brand: 'Apple',
    productName: 'iPhone 13 Display',
    supplierSku: 'SKU-1',
    quality: 'ServicePack',
    condition: 'New',
    priceMinor: 8990,
    currency: 'EUR',
    minOrderQuantity: 1,
    available: 10,
    deliveryDays: 2,
    quantity: 1,
    ...overrides,
  };
}

describe('groupCartBySeller', () => {
  it('creates one group per seller with net totals in cents', () => {
    const groups = groupCartBySeller([
      item({ offerId: 'a', sellerOrganizationId: 's2', sellerName: 'Zeta Parts', priceMinor: 1000, quantity: 3 }),
      item({ offerId: 'b', sellerOrganizationId: 's1', sellerName: 'Alpha Großhandel', priceMinor: 8990, quantity: 2 }),
      item({ offerId: 'c', sellerOrganizationId: 's2', sellerName: 'Zeta Parts', priceMinor: 250, quantity: 4 }),
    ]);

    expect(groups.map((g) => g.sellerName)).toEqual(['Alpha Großhandel', 'Zeta Parts']);
    expect(groups[0].items.map((i) => i.offerId)).toEqual(['b']);
    expect(groups[0].netTotalMinor).toBe(17980);
    expect(groups[1].items.map((i) => i.offerId)).toEqual(['a', 'c']);
    expect(groups[1].netTotalMinor).toBe(4000);
    expect(groups[1].itemCount).toBe(7);
  });

  it('returns no groups for an empty cart', () => {
    expect(groupCartBySeller([])).toEqual([]);
  });

  it('avoids floating point errors by summing integer cents', () => {
    const [group] = groupCartBySeller([item({ priceMinor: 10, quantity: 3 }), item({ offerId: 'o2', priceMinor: 20, quantity: 1 })]);
    expect(group.netTotalMinor).toBe(50);
  });
});

describe('clampQuantity', () => {
  it('respects minimum order quantity and availability', () => {
    expect(clampQuantity(1, 5, 20)).toBe(5);
    expect(clampQuantity(50, 5, 20)).toBe(20);
    expect(clampQuantity(7.8, 1, 20)).toBe(7);
  });

  it('returns null when the offer cannot satisfy the minimum', () => {
    expect(clampQuantity(3, 5, 4)).toBeNull();
  });
});
