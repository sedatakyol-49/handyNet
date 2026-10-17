import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { API_BASE_URL } from '../../core/api/api-config';
import { PosProduct } from '../../core/api/models';
import { PosProductGridComponent } from './pos-product-grid';

const product = (over: Partial<PosProduct>): PosProduct => ({
  productId: 'p1',
  brand: 'Zubehör',
  name: 'Ladekabel',
  category: 'Accessory',
  quality: 'Aftermarket',
  ean: null,
  mpn: null,
  imageUrl: null,
  serialized: false,
  onHand: 3,
  reserved: 0,
  available: 3,
  priceMinor: 999,
  priceSource: 'PosPrice',
  posPriceMinor: 999,
  ...over,
});

describe('PosProductGridComponent images', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), { provide: API_BASE_URL, useValue: 'https://api.test' }],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('shows the catalog thumbnail (API base + relative URL) and falls back to the category icon', async () => {
    const fixture = TestBed.createComponent(PosProductGridComponent);
    fixture.componentRef.setInput('registerId', 'r1');
    fixture.detectChanges();
    await fixture.whenStable();

    http.expectOne((r) => r.url === 'https://api.test/api/pos/registers/r1/product-categories').flush([]);
    http.expectOne((r) => r.url === 'https://api.test/api/pos/registers/r1/products').flush({
      items: [
        product({ productId: 'p1', imageUrl: '/api/catalog/images/i1/thumb' }),
        product({ productId: 'p2', name: 'Hülle', imageUrl: null }),
      ],
      page: 1,
      pageSize: 48,
      total: 2,
    });
    await fixture.whenStable();
    fixture.detectChanges();

    const tiles = fixture.nativeElement.querySelectorAll('[data-testid="pos-product"]') as NodeListOf<HTMLElement>;
    expect(tiles.length).toBe(2);
    const img = tiles[0].querySelector('[data-testid="pos-product-image"]') as HTMLImageElement;
    expect(img.getAttribute('src')).toBe('https://api.test/api/catalog/images/i1/thumb');
    expect(tiles[1].querySelector('img')).toBeNull();
    expect(tiles[1].querySelector('svg')).not.toBeNull();
  });
});
