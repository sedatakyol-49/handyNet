import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE_URL } from '../../../core/api/api-config';
import { queryParams } from '../../../core/api/http-params';
import { Paged } from '../../../core/api/models';
import { PricelistRequest } from './pricing.logic';

export interface Pricelist {
  id: string;
  name: string;
  /** General discount off the till price for products without an item price (100 = 1 %). */
  discountBasisPoints: number;
  isActive: boolean;
  validFrom: string | null;
  validTo: string | null;
  /** Active and valid today: only then it changes customers' prices. */
  currentlyValid: boolean;
  itemCount: number;
  customerCount: number;
  createdAt: string;
  updatedAt: string;
  version: number;
}

export interface PricelistItem {
  productId: string;
  brand: string;
  name: string;
  ean: string | null;
  priceMinor: number;
  /** Regular till price of the product (null = none). */
  basePriceMinor: number | null;
  updatedAt: string;
}

/** Pricelists (ADR-0020): read `pos.sell`, write `pos.manage`. No DELETE — a list is deactivated instead. */
@Injectable({ providedIn: 'root' })
export class PricelistApi {
  private readonly http = inject(HttpClient);
  private readonly url = `${inject(API_BASE_URL)}/api/pos/pricelists`;

  list(): Observable<Pricelist[]> {
    return this.http.get<Pricelist[]>(this.url);
  }

  get(id: string): Observable<Pricelist> {
    return this.http.get<Pricelist>(`${this.url}/${id}`);
  }

  create(body: PricelistRequest): Observable<Pricelist> {
    return this.http.post<Pricelist>(this.url, body);
  }

  update(id: string, body: PricelistRequest): Observable<Pricelist> {
    return this.http.put<Pricelist>(`${this.url}/${id}`, body);
  }

  items(id: string, q: { search?: string | null; page?: number; pageSize?: number }): Observable<Paged<PricelistItem>> {
    return this.http.get<Paged<PricelistItem>>(`${this.url}/${id}/items`, { params: queryParams(q) });
  }

  setItem(id: string, productId: string, priceMinor: number): Observable<PricelistItem> {
    return this.http.put<PricelistItem>(`${this.url}/${id}/items/${productId}`, { priceMinor });
  }

  removeItem(id: string, productId: string): Observable<void> {
    return this.http.delete<void>(`${this.url}/${id}/items/${productId}`);
  }
}
