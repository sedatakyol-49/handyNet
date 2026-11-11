import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE_URL } from '../../../core/api/api-config';
import { queryParams } from '../../../core/api/http-params';
import { Paged } from '../../../core/api/models';

/** How the till reaches the card terminal. The server never connects to a terminal or bridge. */
export type TerminalConnection = 'Manual' | 'Simulated' | 'ZvtBridge';
export const TERMINAL_CONNECTIONS: TerminalConnection[] = ['Manual', 'ZvtBridge', 'Simulated'];

export interface TerminalProfileInput {
  name: string;
  connection: TerminalConnection;
  host: string | null;
  port: number | null;
  bridgeUrl: string | null;
  /** 6 digits (ZVT password of the terminal). */
  zvtPassword: string | null;
  isDefault?: boolean | null;
  version?: number | null;
}

export interface TerminalProfile extends TerminalProfileInput {
  id: string;
  registerId: string;
  isDefault: boolean;
  createdAt: string;
  updatedAt: string;
  version: number;
}

export type CardTransactionKind = 'Payment' | 'Refund';
export type CardTransactionStatus = 'Pending' | 'Approved' | 'Declined' | 'Aborted' | 'Failed' | 'Reversed';

/** What the terminal reported for one attempt (all optional; the PAN is masked to first 6 + last 4 at most). */
export interface CardResultFields {
  terminalId?: string | null;
  traceNumber?: string | null;
  terminalReceiptNumber?: string | null;
  cardName?: string | null;
  maskedPan?: string | null;
  authCode?: string | null;
  resultCode?: string | null;
  resultText?: string | null;
  customerReceiptText?: string | null;
  merchantReceiptText?: string | null;
}

export interface CardResultRequest extends CardResultFields {
  status: Exclude<CardTransactionStatus, 'Pending' | 'Reversed'>;
}

/** One terminal attempt, stored so that a card charge without a receipt is never lost. */
export interface CardTransaction extends CardResultFields {
  id: string;
  registerId: string;
  kind: CardTransactionKind;
  amountMinor: number;
  status: CardTransactionStatus;
  terminalProfileId: string | null;
  /** Name of the profile when the attempt was made (profiles may be deleted later). */
  terminalName?: string | null;
  /** Made with the simulated terminal: no card was charged. */
  isTest?: boolean;
  /** Set once a sale (or return) used the transaction. */
  receiptId: string | null;
  receiptNumber?: number | null;
  createdAt: string;
  completedAt?: string | null;
  linkedAt?: string | null;
  reversedAt?: string | null;
  reversalText?: string | null;
  createdBy?: string;
  createdByName?: string | null;
}

export interface CardTransactionQuery {
  registerId?: string | null;
  status?: CardTransactionStatus | null;
  unlinked?: boolean | null;
  page?: number;
  pageSize?: number;
}

/** `/api/pos/.../terminals` (read `pos.sell`, write `pos.manage`) and `/api/pos/card-transactions` (`pos.sell`). */
@Injectable({ providedIn: 'root' })
export class TerminalApi {
  private readonly http = inject(HttpClient);
  private readonly url = `${inject(API_BASE_URL)}/api/pos`;

  // ---- profiles
  list(registerId: string): Observable<TerminalProfile[]> {
    return this.http.get<TerminalProfile[]>(`${this.url}/registers/${registerId}/terminals`);
  }

  create(registerId: string, body: TerminalProfileInput): Observable<TerminalProfile> {
    return this.http.post<TerminalProfile>(`${this.url}/registers/${registerId}/terminals`, body);
  }

  update(id: string, body: TerminalProfileInput): Observable<TerminalProfile> {
    return this.http.put<TerminalProfile>(`${this.url}/terminals/${id}`, body);
  }

  setDefault(id: string): Observable<TerminalProfile> {
    return this.http.post<TerminalProfile>(`${this.url}/terminals/${id}/default`, {});
  }

  delete(id: string): Observable<void> {
    return this.http.delete<void>(`${this.url}/terminals/${id}`);
  }

  // ---- card transactions
  /** Before the terminal is started: a Pending transaction. */
  createTransaction(registerId: string, body: { kind: CardTransactionKind; amountMinor: number; terminalProfileId?: string | null }): Observable<CardTransaction> {
    return this.http.post<CardTransaction>(`${this.url}/registers/${registerId}/card-transactions`, body);
  }

  /** What the terminal answered (Pending → Approved | Declined | Aborted | Failed). */
  reportResult(id: string, body: CardResultRequest): Observable<CardTransaction> {
    return this.http.post<CardTransaction>(`${this.url}/card-transactions/${id}/result`, body);
  }

  /** Approved → Reversed (only while no receipt uses it), after the reversal went through on the terminal. */
  reportReversal(id: string, resultText: string | null): Observable<CardTransaction> {
    return this.http.post<CardTransaction>(`${this.url}/card-transactions/${id}/reversal`, { resultText });
  }

  transaction(id: string): Observable<CardTransaction> {
    return this.http.get<CardTransaction>(`${this.url}/card-transactions/${id}`);
  }

  transactions(q: CardTransactionQuery): Observable<Paged<CardTransaction>> {
    return this.http.get<Paged<CardTransaction>>(`${this.url}/card-transactions`, { params: queryParams(q) });
  }
}
