import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE_URL } from '../../../core/api/api-config';
import { queryParams } from '../../../core/api/http-params';
import { Paged } from '../../../core/api/models';

export type GiftCardStatus = 'Active' | 'Voided';

/** `GET /api/pos/gift-cards/lookup?code=` (rate-limited). Usable: active and not expired. */
export interface GiftCardLookup {
  id: string;
  codeLast4: string;
  balanceMinor: number;
  status: GiftCardStatus;
  expiresOn: string | null;
  usable: boolean;
  initialMinor: number;
}

export interface GiftCardTransaction {
  id: string;
  type: 'Issue' | 'Redeem' | 'Restore' | 'Void' | 'Reissue';
  amountMinor: number;
  balanceAfterMinor: number;
  receiptId: string | null;
  receiptNumber: number | null;
  reason: string | null;
  createdAt: string;
  createdBy: string;
  createdByName: string | null;
}

export interface GiftCard {
  id: string;
  codeLast4: string;
  initialMinor: number;
  balanceMinor: number;
  status: GiftCardStatus;
  issuedAt: string;
  issuedReceiptId: string;
  issuedReceiptNumber: number | null;
  expiresOn: string | null;
  expired: boolean;
  customerId: string | null;
  customerName: string | null;
  version: number;
  /** Only on the detail read. */
  transactions?: GiftCardTransaction[] | null;
}

/** Gift cards (ADR-0020): lookup with `pos.sell`, management with `pos.manage`. */
@Injectable({ providedIn: 'root' })
export class GiftCardApi {
  private readonly http = inject(HttpClient);
  private readonly url = `${inject(API_BASE_URL)}/api/pos/gift-cards`;

  lookup(code: string): Observable<GiftCardLookup> {
    return this.http.get<GiftCardLookup>(`${this.url}/lookup`, { params: queryParams({ code }) });
  }

  list(q: { search?: string | null; status?: GiftCardStatus | null; page?: number; pageSize?: number }): Observable<Paged<GiftCard>> {
    return this.http.get<Paged<GiftCard>>(this.url, { params: queryParams(q) });
  }

  get(id: string): Observable<GiftCard> {
    return this.http.get<GiftCard>(`${this.url}/${id}`);
  }

  void(id: string, reason: string): Observable<GiftCard> {
    return this.http.post<GiftCard>(`${this.url}/${id}/void`, { reason });
  }

  /** New code, same balance; the old code stops working. The new code is returned only here. */
  reissue(id: string, reason: string): Observable<{ card: GiftCard; code: string }> {
    return this.http.post<{ card: GiftCard; code: string }>(`${this.url}/${id}/reissue`, { reason });
  }
}
