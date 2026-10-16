import { HttpClient, HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, from, switchMap, throwError } from 'rxjs';
import { API_BASE_URL } from './api-config';
import { queryParams } from './http-params';
import {
  CashMovement,
  CashMovementKind,
  CashSession,
  CreateSaleRequest,
  PosLookup,
  PosRegister,
  Receipt,
  ReceiptSummary,
  ReturnRequest,
  AdminOrganization,
  AnkaufModel,
  CreateQuoteRequest,
  CompleteIntakeRequest,
  InspectIntakeRequest,
  Intake,
  ReceiveIntakeRequest,
  EntryDecision,
  Policy,
  PolicySettings,
  PriceChange,
  PriceList,
  PriceListEntry,
  Quote,
  CarrierProfile,
  CatalogCategories,
  CatalogProduct,
  CreateOfferRequest,
  CreateShipmentRequest,
  Dispute,
  DisputeReason,
  EscalatedDispute,
  Shipment,
  ImportBatch,
  ImportFields,
  ImportOptions,
  ImportRow,
  ImportRowStatus,
  Invoice,
  InvoiceSummary,
  IssueRunResult,
  LedgerLine,
  Listing,
  MarketplaceSort,
  Order,
  OwnOffer,
  Paged,
  Payment,
  PlaceOrderRequest,
  SellerBalance,
  StaffMember,
  StaffRole,
  StockMovement,
  StockRow,
  UpdateOfferRequest,
  Warehouse,
} from './models';

@Injectable({ providedIn: 'root' })
export class CatalogApi {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE_URL);

  products(q: { search?: string; category?: string; page?: number; pageSize?: number }): Observable<Paged<CatalogProduct>> {
    return this.http.get<Paged<CatalogProduct>>(`${this.base}/api/catalog/products`, { params: queryParams(q) });
  }

  categories(): Observable<CatalogCategories> {
    return this.http.get<CatalogCategories>(`${this.base}/api/catalog/categories`);
  }
}

@Injectable({ providedIn: 'root' })
export class OrganizationApi {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE_URL);

  staff(): Observable<StaffMember[]> {
    return this.http.get<StaffMember[]>(`${this.base}/api/organization/staff`);
  }

  addStaff(body: { email: string; displayName: string; password?: string | null; role: StaffRole }): Observable<{ userId: string }> {
    return this.http.post<{ userId: string }>(`${this.base}/api/organization/staff`, body);
  }
}

@Injectable({ providedIn: 'root' })
export class AdminApi {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE_URL);
  private readonly url = `${this.base}/api/admin/organizations`;

  organizations(q: { status?: string; search?: string; page?: number; pageSize?: number }): Observable<Paged<AdminOrganization>> {
    return this.http.get<Paged<AdminOrganization>>(this.url, { params: queryParams(q) });
  }

  modules(): Observable<string[]> {
    return this.http.get<string[]>(`${this.base}/api/admin/modules`);
  }

  verify(id: string): Observable<AdminOrganization> {
    return this.http.post<AdminOrganization>(`${this.url}/${id}/verify`, {});
  }

  reject(id: string, reason: string): Observable<AdminOrganization> {
    return this.http.post<AdminOrganization>(`${this.url}/${id}/reject`, { reason });
  }

  suspend(id: string, reason: string): Observable<AdminOrganization> {
    return this.http.post<AdminOrganization>(`${this.url}/${id}/suspend`, { reason });
  }

  setModules(id: string, modules: string[]): Observable<AdminOrganization> {
    return this.http.put<AdminOrganization>(`${this.url}/${id}/modules`, { modules });
  }
}

@Injectable({ providedIn: 'root' })
export class ImportsApi {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE_URL);
  private readonly url = `${this.base}/api/imports`;

  upload(file: File, mapping: Record<string, string> | null, options: ImportOptions | null): Observable<ImportBatch> {
    const form = new FormData();
    form.append('file', file, file.name);
    if (mapping) form.append('mapping', JSON.stringify(mapping));
    if (options) form.append('options', JSON.stringify(options));
    return this.http.post<ImportBatch>(this.url, form);
  }

  fields(): Observable<ImportFields> {
    return this.http.get<ImportFields>(`${this.url}/fields`);
  }

  list(page = 1, pageSize = 25): Observable<Paged<ImportBatch>> {
    return this.http.get<Paged<ImportBatch>>(this.url, { params: queryParams({ page, pageSize }) });
  }

  get(id: string): Observable<ImportBatch> {
    return this.http.get<ImportBatch>(`${this.url}/${id}`);
  }

  rows(id: string, status: ImportRowStatus | null, page = 1, pageSize = 50): Observable<Paged<ImportRow>> {
    return this.http.get<Paged<ImportRow>>(`${this.url}/${id}/rows`, { params: queryParams({ status, page, pageSize }) });
  }

  commit(id: string): Observable<ImportBatch> {
    return this.http.post<ImportBatch>(`${this.url}/${id}/commit`, {});
  }

  discard(id: string): Observable<ImportBatch> {
    return this.http.post<ImportBatch>(`${this.url}/${id}/discard`, {});
  }
}

@Injectable({ providedIn: 'root' })
export class OffersApi {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE_URL);
  private readonly url = `${this.base}/api/offers`;

  list(q: { search?: string; status?: string; page?: number; pageSize?: number }): Observable<Paged<OwnOffer>> {
    return this.http.get<Paged<OwnOffer>>(this.url, { params: queryParams(q) });
  }

  create(body: CreateOfferRequest): Observable<OwnOffer> {
    return this.http.post<OwnOffer>(this.url, body);
  }

  update(id: string, body: UpdateOfferRequest): Observable<OwnOffer> {
    return this.http.put<OwnOffer>(`${this.url}/${id}`, body);
  }
}

@Injectable({ providedIn: 'root' })
export class InventoryApi {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE_URL);
  private readonly url = `${this.base}/api/inventory`;

  warehouses(): Observable<Warehouse[]> {
    return this.http.get<Warehouse[]>(`${this.url}/warehouses`);
  }

  createWarehouse(body: { code: string; name: string }): Observable<Warehouse> {
    return this.http.post<Warehouse>(`${this.url}/warehouses`, body);
  }

  stock(q: { warehouseId?: string | null; page?: number; pageSize?: number }): Observable<Paged<StockRow>> {
    return this.http.get<Paged<StockRow>>(`${this.url}/stock`, { params: queryParams(q) });
  }

  adjust(body: { warehouseId: string; productId: string; delta: number; reason: string }): Observable<StockRow> {
    return this.http.post<StockRow>(`${this.url}/adjustments`, body);
  }

  movements(q: { productId?: string | null; page?: number; pageSize?: number }): Observable<Paged<StockMovement>> {
    return this.http.get<Paged<StockMovement>>(`${this.url}/movements`, { params: queryParams(q) });
  }
}

export interface MarketplaceQuery {
  search?: string;
  category?: string;
  brand?: string;
  inStockOnly?: boolean;
  sort?: MarketplaceSort;
  page?: number;
  pageSize?: number;
}

@Injectable({ providedIn: 'root' })
export class MarketplaceApi {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE_URL);

  offers(q: MarketplaceQuery): Observable<Paged<Listing>> {
    return this.http.get<Paged<Listing>>(`${this.base}/api/marketplace/offers`, {
      params: queryParams({ ...q, inStockOnly: q.inStockOnly ? true : undefined }),
    });
  }

  offer(id: string): Observable<Listing> {
    return this.http.get<Listing>(`${this.base}/api/marketplace/offers/${id}`);
  }
}

@Injectable({ providedIn: 'root' })
export class OrdersApi {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE_URL);
  private readonly url = `${this.base}/api/orders`;

  /** Places an order; the idempotency key must be reused when retrying the same checkout attempt. */
  place(body: PlaceOrderRequest, idempotencyKey: string): Observable<Order> {
    return this.http.post<Order>(this.url, body, { headers: new HttpHeaders({ 'Idempotency-Key': idempotencyKey }) });
  }

  list(q: { role: 'buyer' | 'sales'; status?: string; page?: number; pageSize?: number }): Observable<Paged<Order>> {
    return this.http.get<Paged<Order>>(this.url, { params: queryParams(q) });
  }

  get(id: string): Observable<Order> {
    return this.http.get<Order>(`${this.url}/${id}`);
  }

  cancel(id: string, reason?: string | null): Observable<Order> {
    return this.http.post<Order>(`${this.url}/${id}/cancel`, { reason: reason || null });
  }

  confirm(id: string): Observable<Order> {
    return this.http.post<Order>(`${this.url}/${id}/confirm`, {});
  }

  ship(id: string): Observable<Order> {
    return this.http.post<Order>(`${this.url}/${id}/ship`, {});
  }

  reject(id: string, reason: string): Observable<Order> {
    return this.http.post<Order>(`${this.url}/${id}/reject`, { reason });
  }

  /** Buyer confirms receipt (Shipped → Delivered); starts the inspection window. */
  confirmDelivery(id: string): Observable<Order> {
    return this.http.post<Order>(`${this.url}/${id}/confirm-delivery`, {});
  }

  /** Buyer accepts the goods during the inspection window (Delivered → Completed). */
  accept(id: string): Observable<Order> {
    return this.http.post<Order>(`${this.url}/${id}/accept`, {});
  }
}

@Injectable({ providedIn: 'root' })
export class ShipmentsApi {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE_URL);
  private readonly url = `${this.base}/api/shipments`;

  /** Carriers the seller may book (requires the B2BSales module). */
  carriers(): Observable<string[]> {
    return this.http.get<string[]>(`${this.url}/carriers`);
  }

  /** Limits (liability, weight, length, lithium) of every carrier the seller may book (B2BSales). */
  carrierProfiles(): Observable<CarrierProfile[]> {
    return this.http.get<CarrierProfile[]>(`${this.url}/carrier-profiles`);
  }

  /** Shipments of an order, newest first (buyer and seller). */
  forOrder(orderId: string): Observable<Shipment[]> {
    return this.http.get<Shipment[]>(this.url, { params: queryParams({ orderId }) });
  }

  create(body: CreateShipmentRequest): Observable<Shipment> {
    return this.http.post<Shipment>(this.url, body);
  }

  /** Label PDF (seller only). Problem bodies of failed blob requests are parsed so `code` is available. */
  label(id: string): Observable<Blob> {
    return this.http.get(`${this.url}/${id}/label`, { responseType: 'blob' }).pipe(catchError(parseBlobError));
  }

  /** Seller declares the parcel was handed to the carrier: order becomes Shipped, stock is consumed. */
  handOver(id: string): Observable<Shipment> {
    return this.http.post<Shipment>(`${this.url}/${id}/handover`, {});
  }

  cancel(id: string): Observable<Shipment> {
    return this.http.post<Shipment>(`${this.url}/${id}/cancel`, {});
  }

  sync(id: string): Observable<Shipment> {
    return this.http.post<Shipment>(`${this.url}/${id}/sync`, {});
  }
}

@Injectable({ providedIn: 'root' })
export class PaymentsApi {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE_URL);

  /**
   * Buyer starts online payment (B2BPurchasing + orders.place). Returns the existing open payment if there is one;
   * the buyer then opens `checkoutUrl` of the payment service provider.
   */
  start(orderId: string): Observable<Payment> {
    return this.http.post<Payment>(`${this.base}/api/orders/${orderId}/payments`, {});
  }

  /** Payments of an order, newest first (buyer and seller; commission fields only for the seller). */
  forOrder(orderId: string): Observable<Payment[]> {
    return this.http.get<Payment[]>(`${this.base}/api/orders/${orderId}/payments`);
  }

  /** Seller: open balance owed by the marketplace and recent payouts (B2BSales + orders.fulfil). */
  balance(): Observable<SellerBalance> {
    return this.http.get<SellerBalance>(`${this.base}/api/payments/balance`);
  }

  /** Seller: own lines of the seller-payable account (paged). */
  ledger(page = 1, pageSize = 25): Observable<Paged<LedgerLine>> {
    return this.http.get<Paged<LedgerLine>>(`${this.base}/api/payments/ledger`, { params: queryParams({ page, pageSize }) });
  }
}

/** HttpClient delivers error bodies of `responseType: 'blob'` requests as Blob; turn problem+json back into an object. */
function parseBlobError(error: unknown): Observable<never> {
  if (!(error instanceof HttpErrorResponse) || !(error.error instanceof Blob)) return throwError(() => error);
  return from(error.error.text()).pipe(
    switchMap((text) => {
      let body: unknown = null;
      try {
        body = JSON.parse(text);
      } catch {
        body = null;
      }
      return throwError(
        () =>
          new HttpErrorResponse({
            error: body,
            headers: error.headers,
            status: error.status,
            statusText: error.statusText,
            url: error.url ?? undefined,
          }),
      );
    }),
  );
}

/** Buyer/seller side of disputes (complaints about delivered orders); tenant-scoped like all order data. */
@Injectable({ providedIn: 'root' })
export class DisputesApi {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE_URL);
  private readonly url = `${this.base}/api/disputes`;

  /** Buyer (orders.place) reports a problem: order must be Delivered and within the inspection window. Order → Disputed. */
  open(orderId: string, body: { reason: DisputeReason; description: string }): Observable<Dispute> {
    return this.http.post<Dispute>(`${this.base}/api/orders/${orderId}/disputes`, body);
  }

  /** Disputes of an order (buyer and seller), newest first. */
  forOrder(orderId: string): Observable<Dispute[]> {
    return this.http.get<Dispute[]>(this.url, { params: queryParams({ orderId }) });
  }

  get(id: string): Observable<Dispute> {
    return this.http.get<Dispute>(`${this.url}/${id}`);
  }

  message(id: string, text: string): Observable<Dispute> {
    return this.http.post<Dispute>(`${this.url}/${id}/messages`, { text });
  }

  /** Either party, only while Open: HandyNet decides. */
  escalate(id: string, text: string): Observable<Dispute> {
    return this.http.post<Dispute>(`${this.url}/${id}/escalate`, { text });
  }

  /** Seller (orders.fulfil) grants a full refund: dispute Refunded, order Refunded. */
  refund(id: string, text: string): Observable<Dispute> {
    return this.http.post<Dispute>(`${this.url}/${id}/refund`, { text });
  }

  /** Buyer withdraws: dispute closed without refund, order Completed (seller is paid out). */
  withdraw(id: string, text: string): Observable<Dispute> {
    return this.http.post<Dispute>(`${this.url}/${id}/withdraw`, { text });
  }
}

/** Platform administration of escalated disputes (no X-Organization-Id needed). */
@Injectable({ providedIn: 'root' })
export class AdminDisputesApi {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE_URL);
  private readonly url = `${this.base}/api/admin/disputes`;

  escalated(): Observable<EscalatedDispute[]> {
    return this.http.get<EscalatedDispute[]>(this.url);
  }

  get(id: string): Observable<Dispute> {
    return this.http.get<Dispute>(`${this.url}/${id}`);
  }

  decide(id: string, refund: boolean, resolution: string): Observable<Dispute> {
    return this.http.post<Dispute>(`${this.url}/${id}/decide`, { refund, resolution });
  }
}

/** Platform administration of Ankauf price lists and pricing policies (platform data, no X-Organization-Id). */
@Injectable({ providedIn: 'root' })
export class AdminAnkaufApi {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE_URL);
  private readonly url = `${this.base}/api/admin/ankauf`;

  /** Uploads a monthly XLSX list; it starts in Review with an approval decision per entry. */
  upload(body: { file: File; label: string; validFrom: string; validUntil: string; sheet?: string | null }): Observable<PriceList> {
    const form = new FormData();
    form.append('file', body.file, body.file.name);
    form.append('label', body.label);
    form.append('validFrom', body.validFrom);
    form.append('validUntil', body.validUntil);
    if (body.sheet) form.append('sheet', body.sheet);
    return this.http.post<PriceList>(`${this.url}/price-lists`, form);
  }

  lists(): Observable<PriceList[]> {
    return this.http.get<PriceList[]>(`${this.url}/price-lists`);
  }

  list(id: string): Observable<PriceList> {
    return this.http.get<PriceList>(`${this.url}/price-lists/${id}`);
  }

  entries(
    id: string,
    q: { decision?: EntryDecision | null; change?: PriceChange | null; search?: string; page?: number; pageSize?: number },
  ): Observable<Paged<PriceListEntry>> {
    return this.http.get<Paged<PriceListEntry>>(`${this.url}/price-lists/${id}/entries`, { params: queryParams(q) });
  }

  /** Approve or reject 1..1000 entries (only while the list is in Review; auto-accepted entries are refused). */
  decide(id: string, entryIds: number[], approve: boolean): Observable<PriceList> {
    return this.http.post<PriceList>(`${this.url}/price-lists/${id}/decisions`, { entryIds, approve });
  }

  publish(id: string): Observable<PriceList> {
    return this.http.post<PriceList>(`${this.url}/price-lists/${id}/publish`, {});
  }

  discard(id: string): Observable<PriceList> {
    return this.http.post<PriceList>(`${this.url}/price-lists/${id}/discard`, {});
  }

  policies(): Observable<Policy[]> {
    return this.http.get<Policy[]>(`${this.url}/policies`);
  }

  /** Creates a new immutable policy version (effective immediately when `effectiveFrom` is null). */
  createPolicy(settings: PolicySettings, effectiveFrom: string | null): Observable<Policy> {
    return this.http.post<Policy>(`${this.url}/policies`, { settings, effectiveFrom });
  }
}

/** Dealer side of Ankauf (module Ankauf, tenant-scoped). */
@Injectable({ providedIn: 'root' })
export class AnkaufApi {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE_URL);
  private readonly url = `${this.base}/api/ankauf`;

  /** Models with a price in the current published list (max. 100). */
  models(search: string): Observable<AnkaufModel[]> {
    return this.http.get<AnkaufModel[]>(`${this.url}/models`, { params: queryParams({ search }) });
  }

  createQuote(body: CreateQuoteRequest): Observable<Quote> {
    return this.http.post<Quote>(`${this.url}/quotes`, body);
  }

  quotes(): Observable<Quote[]> {
    return this.http.get<Quote[]>(`${this.url}/quotes`);
  }

  // ---- device intake (module Ankauf + permission inventory.manage)

  /** Latest 100 intakes of the organization, newest first. */
  intakes(): Observable<Intake[]> {
    return this.http.get<Intake[]>(`${this.url}/intakes`);
  }

  intake(id: string): Observable<Intake> {
    return this.http.get<Intake>(`${this.url}/intakes/${id}`);
  }

  /** Records device + seller for a quote (status Received). */
  receiveIntake(body: ReceiveIntakeRequest): Observable<Intake> {
    return this.http.post<Intake>(`${this.url}/intakes`, body);
  }

  /** Recalculates the offer with the quote's price version and policy; repeatable until completed. */
  inspectIntake(id: string, body: InspectIntakeRequest): Observable<Intake> {
    return this.http.post<Intake>(`${this.url}/intakes/${id}/inspect`, body);
  }

  /** Pays the seller and stocks the device (§25a margin scheme). */
  completeIntake(id: string, body: CompleteIntakeRequest): Observable<Intake> {
    return this.http.post<Intake>(`${this.url}/intakes/${id}/complete`, body);
  }

  rejectIntake(id: string, reason: string): Observable<Intake> {
    return this.http.post<Intake>(`${this.url}/intakes/${id}/reject`, { reason });
  }

  /** Ankaufbeleg PDF of a completed intake (409 `ankauf.not_completed` otherwise). */
  beleg(id: string): Observable<Blob> {
    return this.http.get(`${this.url}/intakes/${id}/beleg`, { responseType: 'blob' }).pipe(catchError(parseBlobError));
  }
}

/** Point of sale (module Pos, tenant-scoped). `pos.sell`: sessions, sales, returns, lookup; `pos.manage`: registers, Storno. */
@Injectable({ providedIn: 'root' })
export class PosApi {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE_URL);
  private readonly url = `${this.base}/api/pos`;

  // ---- registers
  registers(): Observable<PosRegister[]> {
    return this.http.get<PosRegister[]>(`${this.url}/registers`);
  }

  register(id: string): Observable<PosRegister> {
    return this.http.get<PosRegister>(`${this.url}/registers/${id}`);
  }

  createRegister(body: { name: string; warehouseId?: string | null }): Observable<PosRegister> {
    return this.http.post<PosRegister>(`${this.url}/registers`, body);
  }

  updateRegister(id: string, body: { name: string; isActive: boolean }): Observable<PosRegister> {
    return this.http.put<PosRegister>(`${this.url}/registers/${id}`, body);
  }

  // ---- cash sessions
  openSession(registerId: string, openingCashMinor: number): Observable<CashSession> {
    return this.http.post<CashSession>(`${this.url}/registers/${registerId}/sessions`, { openingCashMinor });
  }

  /** Null (204) when the register has no open session. */
  currentSession(registerId: string): Observable<CashSession | null> {
    return this.http.get<CashSession | null>(`${this.url}/registers/${registerId}/sessions/current`);
  }

  sessions(q: { registerId?: string | null; page?: number; pageSize?: number }): Observable<Paged<CashSession>> {
    return this.http.get<Paged<CashSession>>(`${this.url}/sessions`, { params: queryParams(q) });
  }

  session(id: string): Observable<CashSession> {
    return this.http.get<CashSession>(`${this.url}/sessions/${id}`);
  }

  addMovement(sessionId: string, body: { kind: CashMovementKind; amountMinor: number; reason: string }): Observable<CashMovement> {
    return this.http.post<CashMovement>(`${this.url}/sessions/${sessionId}/cash-movements`, body);
  }

  closeSession(sessionId: string, body: { countedCashMinor: number; note?: string | null }): Observable<CashSession> {
    return this.http.post<CashSession>(`${this.url}/sessions/${sessionId}/close`, body);
  }

  // ---- sales
  /** EAN (product) or 15-digit IMEI (own in-stock device); stock figures refer to the register's warehouse. */
  lookup(code: string, registerId: string): Observable<PosLookup> {
    return this.http.get<PosLookup>(`${this.url}/lookup`, { params: queryParams({ code, registerId }) });
  }

  /** The idempotency key must be reused when retrying the same checkout attempt (a replay answers 200 with the original). */
  sell(body: CreateSaleRequest, idempotencyKey: string): Observable<Receipt> {
    return this.http.post<Receipt>(`${this.url}/sales`, body, { headers: new HttpHeaders({ 'Idempotency-Key': idempotencyKey }) });
  }

  receipts(q: { registerId?: string | null; sessionId?: string | null; page?: number; pageSize?: number }): Observable<Paged<ReceiptSummary>> {
    return this.http.get<Paged<ReceiptSummary>>(`${this.url}/sales`, { params: queryParams(q) });
  }

  receipt(id: string): Observable<Receipt> {
    return this.http.get<Receipt>(`${this.url}/sales/${id}`);
  }

  storno(id: string, body: { reason: string; registerId?: string | null }): Observable<Receipt> {
    return this.http.post<Receipt>(`${this.url}/sales/${id}/storno`, body);
  }

  returnItems(id: string, body: ReturnRequest, idempotencyKey: string): Observable<Receipt> {
    return this.http.post<Receipt>(`${this.url}/sales/${id}/returns`, body, { headers: new HttpHeaders({ 'Idempotency-Key': idempotencyKey }) });
  }
}

/** Seller's own commission invoices and corrections (B2BSales + orders.fulfil, tenant-scoped, read only). */
@Injectable({ providedIn: 'root' })
export class InvoicesApi {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE_URL);
  private readonly url = `${this.base}/api/invoices`;

  /** Newest first. */
  list(page = 1, pageSize = 25): Observable<Paged<InvoiceSummary>> {
    return this.http.get<Paged<InvoiceSummary>>(this.url, { params: queryParams({ page, pageSize }) });
  }

  /** 404 `invoicing.not_found` for unknown ids or documents of another organization. */
  get(id: string): Observable<Invoice> {
    return this.http.get<Invoice>(`${this.url}/${id}`);
  }

  /** PDF rendered from the immutable invoice data. Problem bodies of failed blob requests are parsed so `code` is available. */
  pdf(id: string): Observable<Blob> {
    return this.http.get(`${this.url}/${id}/pdf`, { responseType: 'blob' }).pipe(catchError(parseBlobError));
  }
}

/** Platform administration of commission invoicing (no X-Organization-Id). */
@Injectable({ providedIn: 'root' })
export class AdminInvoicesApi {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE_URL);
  private readonly url = `${this.base}/api/admin/invoices`;

  /**
   * Issues invoices and corrections for a completed month (`yyyy-MM`), the same idempotent path as the monthly job.
   * 422 `invoicing.month_not_over` for the current/future month; 409 `invoicing.issuer_not_configured` / `issuer_incomplete`.
   */
  issue(month: string): Observable<IssueRunResult> {
    return this.http.post<IssueRunResult>(`${this.url}/issue`, { month });
  }

  /** All documents with the given billing month across sellers, ordered by number. */
  forMonth(month: string): Observable<InvoiceSummary[]> {
    return this.http.get<InvoiceSummary[]>(this.url, { params: queryParams({ month }) });
  }
}
