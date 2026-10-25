import { HttpClient, HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, from, switchMap, throwError } from 'rxjs';
import { API_BASE_URL } from './api-config';
import { queryParams } from './http-params';
import {
  CashMovement,
  CompanyLogo,
  CompanyProfile,
  UpdateCompanyProfileRequest,
  CashMovementReason,
  CashMovementRequest,
  CashSession,
  CreateSaleRequest,
  DenominationCount,
  PosCategoryCount,
  PosCustomer,
  PosCustomerInput,
  PosLookup,
  PosPrice,
  PosProduct,
  PosApprovalGrant,
  PosCashier,
  PosCashierOption,
  PosCashierSignIn,
  PosInvoice,
  PosInvoiceCustomerInput,
  PosPinStatus,
  PosPublicReceipt,
  PosReceiptEmail,
  PosReceiptLink,
  PosRegister,
  PosRightsRequest,
  Receipt,
  ReceiptReprint,
  ReceiptSummary,
  ReturnRequest,
  SaleQuery,
  SalesReport,
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
  LookupResult,
  ManualEntryRequest,
  ManualEntryResult,
  MarketplaceFacets,
  OfferDetail,
  ProductImage,
  AdminModerationOffer,
  AdminModerationQuery,
  AdminModerationSeller,
  BulkModerationResult,
  OwnModeration,
  SellerModerationResult,
  CatalogProductDetail,
  UpdateProductRequest,
  CollectInvoiceResult,
  InvoiceListItem,
  InvoicePaymentMethod,
  PosPaymentMethod,
  Quotation,
  QuotationRequest,
  QuotationSummary,
  SalesEmailResult,
  SalesInvoice,
  SalesInvoiceRequest,
  SalesInvoiceSummary,
  SalesQuery,
  SalesWarehouse,
} from './models';

@Injectable({ providedIn: 'root' })
export class CatalogApi {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE_URL);

  products(q: { search?: string; category?: string; page?: number; pageSize?: number }): Observable<Paged<CatalogProduct>> {
    return this.http.get<Paged<CatalogProduct>>(`${this.base}/api/catalog/products`, { params: queryParams(q) });
  }

  product(id: string): Observable<CatalogProductDetail> {
    return this.http.get<CatalogProductDetail>(`${this.base}/api/catalog/products/${id}`);
  }

  /** Creating organization only (ProductImport + imports.manage); 409 `concurrency` / `product.ean_taken` / `product.identity_taken`. */
  updateProduct(id: string, body: UpdateProductRequest): Observable<CatalogProductDetail> {
    return this.http.put<CatalogProductDetail>(`${this.base}/api/catalog/products/${id}`, body);
  }

  categories(): Observable<CatalogCategories> {
    return this.http.get<CatalogCategories>(`${this.base}/api/catalog/categories`);
  }

  images(productId: string): Observable<ProductImage[]> {
    return this.http.get<ProductImage[]>(`${this.base}/api/catalog/products/${productId}/images`);
  }

  /** Multipart field `file`; JPEG/PNG/WebP up to 5 MB, at most 8 per product (only the creating organization). */
  uploadImage(productId: string, file: File): Observable<ProductImage> {
    const form = new FormData();
    form.append('file', file, file.name);
    return this.http.post<ProductImage>(`${this.base}/api/catalog/products/${productId}/images`, form);
  }

  /** Every current image id exactly once; the first becomes the primary image. */
  reorderImages(productId: string, imageIds: string[]): Observable<ProductImage[]> {
    return this.http.put<ProductImage[]>(`${this.base}/api/catalog/products/${productId}/images/order`, { imageIds });
  }

  deleteImage(productId: string, imageId: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/api/catalog/products/${productId}/images/${imageId}`);
  }

  /** Image URLs from the API are relative to the API base (public, immutable). */
  imageUrl(relative: string | null | undefined): string | null {
    return relative ? `${this.base}${relative}` : null;
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

  /** Per-cashier POS rights of a staff member (`organization.manage`; 409 `staff.pos_rights_role` for owners/managers). */
  setPosRights(userId: string, body: PosRightsRequest): Observable<StaffMember> {
    return this.http.put<StaffMember>(`${this.base}/api/organization/staff/${userId}/pos-rights`, body);
  }

  /** Own company profile (every member; `canEdit` for organization.manage). */
  profile(): Observable<CompanyProfile> {
    return this.http.get<CompanyProfile>(`${this.base}/api/organization/profile`);
  }

  /** 409 `concurrency` when someone else saved in between; 422 `validation.failed` with field errors. */
  updateProfile(body: UpdateCompanyProfileRequest): Observable<CompanyProfile> {
    return this.http.put<CompanyProfile>(`${this.base}/api/organization/profile`, body);
  }

  /** PNG/JPEG/WebP up to 2 MB (checked by content), re-encoded by the API. */
  uploadLogo(file: Blob, fileName: string): Observable<CompanyLogo> {
    const form = new FormData();
    form.append('file', file, fileName);
    return this.http.post<CompanyLogo>(`${this.base}/api/organization/logo`, form);
  }

  removeLogo(): Observable<void> {
    return this.http.delete<void>(`${this.base}/api/organization/logo`);
  }

  /** Absolute URL for an <img> (logo URLs are public, relative to the API). */
  assetUrl(relative: string | null | undefined): string | null {
    return relative ? `${this.base}${relative}` : null;
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

  list(q: { search?: string; status?: string; productId?: string | null; page?: number; pageSize?: number }): Observable<Paged<OwnOffer>> {
    return this.http.get<Paged<OwnOffer>>(this.url, { params: queryParams(q) });
  }

  create(body: CreateOfferRequest): Observable<OwnOffer> {
    return this.http.post<OwnOffer>(this.url, body);
  }

  update(id: string, body: UpdateOfferRequest): Observable<OwnOffer> {
    return this.http.put<OwnOffer>(`${this.url}/${id}`, body);
  }

  /** Whether the platform blocks the current organization as a seller (all offers hidden). */
  moderation(): Observable<OwnModeration> {
    return this.http.get<OwnModeration>(`${this.url}/moderation`);
  }
}

/**
 * Platform moderation of marketplace offers (no X-Organization-Id): hide single offers, a selection or all offers of a
 * seller. Independent of the seller's own offer status; every action is audited with the optional reason.
 */
@Injectable({ providedIn: 'root' })
export class AdminModerationApi {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE_URL);
  private readonly url = `${this.base}/api/admin/marketplace`;

  offers(q: AdminModerationQuery): Observable<Paged<AdminModerationOffer>> {
    return this.http.get<Paged<AdminModerationOffer>>(`${this.url}/offers`, { params: queryParams(q) });
  }

  sellers(search?: string | null): Observable<AdminModerationSeller[]> {
    return this.http.get<AdminModerationSeller[]>(`${this.url}/sellers`, { params: queryParams({ search }) });
  }

  blockOffer(id: string, reason: string | null): Observable<AdminModerationOffer> {
    return this.http.post<AdminModerationOffer>(`${this.url}/offers/${id}/block`, { reason });
  }

  unblockOffer(id: string, reason: string | null): Observable<AdminModerationOffer> {
    return this.http.post<AdminModerationOffer>(`${this.url}/offers/${id}/unblock`, { reason });
  }

  /** All or nothing: 404 `moderation.offer_not_found` if any id is unknown. At most 500 ids. */
  blockOffers(offerIds: string[], reason: string | null): Observable<BulkModerationResult> {
    return this.http.post<BulkModerationResult>(`${this.url}/offers/block`, { offerIds, reason });
  }

  unblockOffers(offerIds: string[], reason: string | null): Observable<BulkModerationResult> {
    return this.http.post<BulkModerationResult>(`${this.url}/offers/unblock`, { offerIds, reason });
  }

  /** Hides all offers of the seller, including offers created while the block lasts. */
  blockSeller(organizationId: string, reason: string | null): Observable<SellerModerationResult> {
    return this.http.post<SellerModerationResult>(`${this.url}/sellers/${organizationId}/block`, { reason });
  }

  unblockSeller(organizationId: string, reason: string | null): Observable<SellerModerationResult> {
    return this.http.post<SellerModerationResult>(`${this.url}/sellers/${organizationId}/unblock`, { reason });
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

  stock(q: { warehouseId?: string | null; productId?: string | null; page?: number; pageSize?: number }): Observable<Paged<StockRow>> {
    return this.http.get<Paged<StockRow>>(`${this.url}/stock`, { params: queryParams(q) });
  }

  adjust(body: { warehouseId: string; productId: string; delta: number; reason: string }): Observable<StockRow> {
    return this.http.post<StockRow>(`${this.url}/adjustments`, body);
  }

  movements(q: { productId?: string | null; page?: number; pageSize?: number }): Observable<Paged<StockMovement>> {
    return this.http.get<Paged<StockMovement>>(`${this.url}/movements`, { params: queryParams(q) });
  }

  /** Step 1 of "Artikel anlegen": EAN / MPN / text → catalog matches with own stock and offers. */
  lookupItem(q: string): Observable<LookupResult> {
    return this.http.get<LookupResult>(`${this.url}/items/lookup`, { params: queryParams({ q }) });
  }

  /** Manual entry; reuse the same key when retrying the same attempt (201 created, 200 replayed). */
  createItem(body: ManualEntryRequest, idempotencyKey: string): Observable<ManualEntryResult> {
    return this.http.post<ManualEntryResult>(`${this.url}/items`, body, { headers: new HttpHeaders({ 'Idempotency-Key': idempotencyKey }) });
  }
}

/** Marketplace filters as the API binds them (lists are repeated query parameters, prices net EUR decimals). */
export interface MarketplaceQuery {
  search?: string;
  category?: string[];
  brand?: string[];
  quality?: string[];
  condition?: string[];
  sellerId?: string[];
  /** Net VK in EUR, e.g. "10.50". */
  priceMin?: string;
  priceMax?: string;
  maxDeliveryDays?: number | null;
  compatibleModel?: string;
  hasImages?: boolean;
  inStockOnly?: boolean;
  productId?: string;
  sort?: MarketplaceSort;
  page?: number;
  pageSize?: number;
}

@Injectable({ providedIn: 'root' })
export class MarketplaceApi {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE_URL);

  offers(q: MarketplaceQuery): Observable<Paged<Listing>> {
    return this.http.get<Paged<Listing>>(`${this.base}/api/marketplace/offers`, { params: this.params(q) });
  }

  facets(q: MarketplaceQuery): Observable<MarketplaceFacets> {
    const { page: _page, pageSize: _size, sort: _sort, ...filters } = q;
    return this.http.get<MarketplaceFacets>(`${this.base}/api/marketplace/facets`, { params: this.params(filters) });
  }

  offer(id: string): Observable<Listing> {
    return this.http.get<Listing>(`${this.base}/api/marketplace/offers/${id}`);
  }

  detail(id: string): Observable<OfferDetail> {
    return this.http.get<OfferDetail>(`${this.base}/api/marketplace/offers/${id}/detail`);
  }

  private params(q: MarketplaceQuery) {
    return queryParams({ ...q, inStockOnly: q.inStockOnly ? true : undefined, hasImages: q.hasImages ? true : undefined });
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

  updateRegister(id: string, body: { name: string; isActive: boolean; autoLockMinutes?: number | null; cashRounding?: boolean }): Observable<PosRegister> {
    return this.http.put<PosRegister>(`${this.url}/registers/${id}`, body);
  }

  // ---- cash sessions
  /** Opening cash as an amount or as denomination counts (the API stores their total). */
  openSession(registerId: string, body: { openingCashMinor?: number | null; denominations?: DenominationCount[] | null }): Observable<CashSession> {
    return this.http.post<CashSession>(`${this.url}/registers/${registerId}/sessions`, body);
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

  /** `pos.cash_movement`. */
  addMovement(sessionId: string, body: CashMovementRequest): Observable<CashMovement> {
    return this.http.post<CashMovement>(`${this.url}/sessions/${sessionId}/cash-movements`, body);
  }

  cashMovementReasons(): Observable<CashMovementReason[]> {
    return this.http.get<CashMovementReason[]>(`${this.url}/cash-movement-reasons`);
  }

  /** `pos.close_session`. Counted cash as an amount or as denomination counts. */
  closeSession(
    sessionId: string,
    body: { countedCashMinor?: number | null; note?: string | null; denominations?: DenominationCount[] | null },
  ): Observable<CashSession> {
    return this.http.post<CashSession>(`${this.url}/sessions/${sessionId}/close`, body);
  }

  // ---- products, prices
  /** Products of the register's warehouse with till prices (in stock unless `includeOutOfStock`). */
  products(
    registerId: string,
    q: { search?: string | null; category?: string | null; includeOutOfStock?: boolean; page?: number; pageSize?: number },
  ): Observable<Paged<PosProduct>> {
    return this.http.get<Paged<PosProduct>>(`${this.url}/registers/${registerId}/products`, { params: queryParams(q) });
  }

  productCategories(registerId: string): Observable<PosCategoryCount[]> {
    return this.http.get<PosCategoryCount[]>(`${this.url}/registers/${registerId}/product-categories`);
  }

  price(productId: string, registerId?: string | null): Observable<PosPrice> {
    return this.http.get<PosPrice>(`${this.url}/prices/${productId}`, { params: queryParams({ registerId }) });
  }

  /** `pos.manage`; null clears the own POS price (the till falls back to the offer price). */
  setPrice(productId: string, priceMinor: number | null): Observable<PosPrice> {
    return this.http.put<PosPrice>(`${this.url}/prices/${productId}`, { priceMinor });
  }

  // ---- customers
  customers(q: { search?: string | null; page?: number; pageSize?: number }): Observable<Paged<PosCustomer>> {
    return this.http.get<Paged<PosCustomer>>(`${this.url}/customers`, { params: queryParams(q) });
  }

  customer(id: string): Observable<PosCustomer> {
    return this.http.get<PosCustomer>(`${this.url}/customers/${id}`);
  }

  createCustomer(body: PosCustomerInput): Observable<PosCustomer> {
    return this.http.post<PosCustomer>(`${this.url}/customers`, body);
  }

  updateCustomer(id: string, body: PosCustomerInput): Observable<PosCustomer> {
    return this.http.put<PosCustomer>(`${this.url}/customers/${id}`, body);
  }

  // ---- reports
  /** A session (`pos.close_session` or `pos.manage`) or a date range (`pos.manage`, `to` exclusive, ≤ 366 days). */
  salesReport(q: { sessionId?: string | null; registerId?: string | null; from?: string | null; to?: string | null }): Observable<SalesReport> {
    return this.http.get<SalesReport>(`${this.url}/reports/sales`, { params: queryParams(q) });
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

  /** Order history, newest first (filters combined with AND; `to` exclusive). */
  receipts(q: SaleQuery): Observable<Paged<ReceiptSummary>> {
    return this.http.get<Paged<ReceiptSummary>>(`${this.url}/sales`, { params: queryParams(q) });
  }

  /** Records a copy of an issued receipt and returns its copy number (the receipt itself never changes). */
  reprint(id: string): Observable<ReceiptReprint> {
    return this.http.post<ReceiptReprint>(`${this.url}/sales/${id}/reprints`, {});
  }

  receipt(id: string): Observable<Receipt> {
    return this.http.get<Receipt>(`${this.url}/sales/${id}`);
  }

  storno(id: string, body: { reason: string; registerId?: string | null }): Observable<Receipt> {
    return this.http.post<Receipt>(`${this.url}/sales/${id}/storno`, body);
  }

  // ---- PINs and cashier switching (shared device)
  myPin(): Observable<PosPinStatus> {
    return this.http.get<PosPinStatus>(`${this.url}/me/pin`);
  }

  /** Own PIN (4–6 digits; null clears it); needs the account password. */
  setMyPin(pin: string | null, currentPassword: string): Observable<PosPinStatus> {
    return this.http.put<PosPinStatus>(`${this.url}/me/pin`, { pin, currentPassword });
  }

  /** `organization.manage`: PIN status of every member. */
  pins(): Observable<PosPinStatus[]> {
    return this.http.get<PosPinStatus[]>(`${this.url}/pins`);
  }

  /** `organization.manage`: set or clear (null) a member's PIN; also lifts a lockout. */
  setPin(userId: string, pin: string | null): Observable<PosPinStatus> {
    return this.http.put<PosPinStatus>(`${this.url}/cashiers/${userId}/pin`, { pin });
  }

  cashiers(): Observable<PosCashierOption[]> {
    return this.http.get<PosCashierOption[]>(`${this.url}/cashiers`);
  }

  /** Switch the till to a cashier with their PIN; the token goes into the X-Pos-Cashier header (posCashierInterceptor). */
  signInCashier(registerId: string, userId: string, pin: string): Observable<PosCashierSignIn> {
    return this.http.post<PosCashierSignIn>(`${this.url}/registers/${registerId}/cashier`, { userId, pin });
  }

  currentCashier(): Observable<PosCashier> {
    return this.http.get<PosCashier>(`${this.url}/cashier`);
  }

  lockCashier(): Observable<void> {
    return this.http.post<void>(`${this.url}/cashier/lock`, {});
  }

  // ---- invoices for POS sales (Rechnung)
  /** The receipt's invoice, or null (204) when none was issued yet. */
  receiptInvoice(receiptId: string): Observable<PosInvoice | null> {
    return this.http.get<PosInvoice | null>(`${this.url}/sales/${receiptId}/invoice`);
  }

  /** Issues the invoice (201) or returns the existing one (200). Customer null = the receipt's customer. */
  createInvoice(receiptId: string, customer: PosInvoiceCustomerInput | null): Observable<PosInvoice> {
    return this.http.post<PosInvoice>(`${this.url}/sales/${receiptId}/invoice`, { customer });
  }

  invoicePdf(invoiceId: string): Observable<Blob> {
    return this.http.get(`${this.url}/invoices/${invoiceId}/pdf`, { responseType: 'blob' }).pipe(catchError(parseBlobError));
  }

  // ---- e-receipts
  /** Email the receipt (null = to the receipt's customer); sent by the Notifications outbox. */
  emailReceipt(id: string, email: string | null): Observable<PosReceiptEmail> {
    return this.http.post<PosReceiptEmail>(`${this.url}/sales/${id}/email`, { email });
  }

  /** A public, read-only link (QR) to the receipt; the token is only returned here. */
  receiptLink(id: string): Observable<PosReceiptLink> {
    return this.http.post<PosReceiptLink>(`${this.url}/sales/${id}/public-link`, {});
  }

  /** Anonymous public receipt view (no customer data). */
  publicReceipt(token: string): Observable<PosPublicReceipt> {
    return this.http.get<PosPublicReceipt>(`${this.base}/api/public/receipts/${encodeURIComponent(token)}`);
  }

  /** Owner/manager PIN approves the given rights for the next action on this register (posCashierInterceptor sends it). */
  requestApproval(registerId: string, body: { approverUserId: string; pin: string; permissions: string[] }): Observable<PosApprovalGrant> {
    return this.http.post<PosApprovalGrant>(`${this.url}/registers/${registerId}/approvals`, body);
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

/**
 * Quotations and sales invoices outside the till (module Pos, `pos.sell`; credit notes `pos.refund` or a manager approval).
 * Invoices share the organization's single gapless series with invoices for POS sales.
 */
@Injectable({ providedIn: 'root' })
export class SalesApi {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE_URL);
  private readonly url = `${this.base}/api/pos`;

  warehouses(): Observable<SalesWarehouse[]> {
    return this.http.get<SalesWarehouse[]>(`${this.url}/sales-warehouses`);
  }

  products(warehouseId: string, q: { search?: string | null; includeOutOfStock?: boolean; page?: number; pageSize?: number }): Observable<Paged<PosProduct>> {
    return this.http.get<Paged<PosProduct>>(`${this.url}/warehouses/${warehouseId}/products`, { params: queryParams(q) });
  }

  /** EAN or IMEI; without a register the stock figures are omitted. */
  lookup(code: string, registerId?: string | null): Observable<PosLookup> {
    return this.http.get<PosLookup>(`${this.url}/lookup`, { params: queryParams({ code, registerId }) });
  }

  // ---- quotations
  quotations(q: SalesQuery): Observable<Paged<QuotationSummary>> {
    return this.http.get<Paged<QuotationSummary>>(`${this.url}/quotations`, { params: queryParams(q) });
  }

  quotation(id: string): Observable<Quotation> {
    return this.http.get<Quotation>(`${this.url}/quotations/${id}`);
  }

  createQuotation(body: QuotationRequest): Observable<Quotation> {
    return this.http.post<Quotation>(`${this.url}/quotations`, body);
  }

  updateQuotation(id: string, body: QuotationRequest): Observable<Quotation> {
    return this.http.put<Quotation>(`${this.url}/quotations/${id}`, body);
  }

  setQuotationStatus(id: string, status: 'Draft' | 'Sent' | 'Accepted' | 'Rejected'): Observable<Quotation> {
    return this.http.post<Quotation>(`${this.url}/quotations/${id}/status`, { status });
  }

  duplicateQuotation(id: string): Observable<Quotation> {
    return this.http.post<Quotation>(`${this.url}/quotations/${id}/duplicate`, {});
  }

  /** Invoice draft from the quotation (201), or the existing one (200). */
  convertQuotation(id: string): Observable<SalesInvoice> {
    return this.http.post<SalesInvoice>(`${this.url}/quotations/${id}/convert`, {});
  }

  emailQuotation(id: string, email: string | null): Observable<SalesEmailResult> {
    return this.http.post<SalesEmailResult>(`${this.url}/quotations/${id}/email`, { email });
  }

  quotationPdf(id: string): Observable<Blob> {
    return this.http.get(`${this.url}/quotations/${id}/pdf`, { responseType: 'blob' }).pipe(catchError(parseBlobError));
  }

  // ---- invoices
  invoices(q: SalesQuery): Observable<Paged<InvoiceListItem>> {
    return this.http.get<Paged<InvoiceListItem>>(`${this.url}/sales-invoices`, { params: queryParams(q) });
  }

  summary(): Observable<SalesInvoiceSummary> {
    return this.http.get<SalesInvoiceSummary>(`${this.url}/sales-invoices/summary`);
  }

  invoice(id: string): Observable<SalesInvoice> {
    return this.http.get<SalesInvoice>(`${this.url}/sales-invoices/${id}`);
  }

  createInvoice(body: SalesInvoiceRequest): Observable<SalesInvoice> {
    return this.http.post<SalesInvoice>(`${this.url}/sales-invoices`, body);
  }

  updateInvoice(id: string, body: SalesInvoiceRequest): Observable<SalesInvoice> {
    return this.http.put<SalesInvoice>(`${this.url}/sales-invoices/${id}`, body);
  }

  discardInvoice(id: string): Observable<SalesInvoice> {
    return this.http.post<SalesInvoice>(`${this.url}/sales-invoices/${id}/discard`, {});
  }

  duplicateInvoice(id: string): Observable<SalesInvoice> {
    return this.http.post<SalesInvoice>(`${this.url}/sales-invoices/${id}/duplicate`, {});
  }

  /** Idempotent: issuing an issued invoice returns it. */
  issueInvoice(id: string): Observable<SalesInvoice> {
    return this.http.post<SalesInvoice>(`${this.url}/sales-invoices/${id}/issue`, {});
  }

  recordPayment(id: string, body: { method: Exclude<InvoicePaymentMethod, 'Cash'>; amountMinor: number; paidOn?: string | null; reference?: string | null }): Observable<SalesInvoice> {
    return this.http.post<SalesInvoice>(`${this.url}/sales-invoices/${id}/payments`, body);
  }

  /** "An der Kasse kassieren": a signed POS receipt over the amount; reuse the key when retrying the same attempt. */
  collect(id: string, body: { registerId: string; amountMinor: number | null; payments: { method: PosPaymentMethod; amountMinor: number }[] }, idempotencyKey: string): Observable<CollectInvoiceResult> {
    return this.http.post<CollectInvoiceResult>(`${this.url}/sales-invoices/${id}/collect`, body, { headers: new HttpHeaders({ 'Idempotency-Key': idempotencyKey }) });
  }

  creditNote(id: string, body: { reason: string; registerId?: string | null }): Observable<SalesInvoice> {
    return this.http.post<SalesInvoice>(`${this.url}/sales-invoices/${id}/credit-note`, body);
  }

  emailInvoice(id: string, email: string | null): Observable<SalesEmailResult> {
    return this.http.post<SalesEmailResult>(`${this.url}/sales-invoices/${id}/email`, { email });
  }

  invoicePdf(id: string): Observable<Blob> {
    return this.http.get(`${this.url}/sales-invoices/${id}/pdf`, { responseType: 'blob' }).pipe(catchError(parseBlobError));
  }
}
