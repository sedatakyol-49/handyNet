import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE_URL } from '../../../core/api/api-config';
import { queryParams } from '../../../core/api/http-params';
import { Paged } from '../../../core/api/models';
import { LoyaltyRules } from './loyalty.logic';

export interface LoyaltySettings extends LoyaltyRules {
  /** Null while the organization never saved settings (defaults: disabled). */
  updatedAt: string | null;
}

export interface LoyaltyTransaction {
  id: string;
  type: 'Earn' | 'Redeem' | 'ReverseEarn' | 'ReverseRedeem' | 'Adjust';
  points: number;
  balanceAfter: number;
  receiptId: string | null;
  receiptNumber: number | null;
  reason: string | null;
  createdAt: string;
  createdBy: string;
  createdByName: string | null;
}

/** A customer's points with what the till needs to offer a redemption, and a page of the history (newest first). */
export interface CustomerLoyalty {
  customerId: string;
  points: number;
  enabled: boolean;
  pointValueMinor: number;
  minRedeemPoints: number;
  transactions: Paged<LoyaltyTransaction>;
}

/** Loyalty points (ADR-0020): read `pos.sell`, settings and adjustments `pos.manage`. */
@Injectable({ providedIn: 'root' })
export class LoyaltyApi {
  private readonly http = inject(HttpClient);
  private readonly url = `${inject(API_BASE_URL)}/api/pos`;

  settings(): Observable<LoyaltySettings> {
    return this.http.get<LoyaltySettings>(`${this.url}/loyalty/settings`);
  }

  saveSettings(body: LoyaltyRules): Observable<LoyaltySettings> {
    return this.http.put<LoyaltySettings>(`${this.url}/loyalty/settings`, body);
  }

  customer(customerId: string, q: { page?: number; pageSize?: number } = {}): Observable<CustomerLoyalty> {
    return this.http.get<CustomerLoyalty>(`${this.url}/customers/${customerId}/loyalty`, { params: queryParams(q) });
  }

  adjust(customerId: string, points: number, reason: string): Observable<CustomerLoyalty> {
    return this.http.post<CustomerLoyalty>(`${this.url}/customers/${customerId}/loyalty/adjustments`, { points, reason });
  }
}
