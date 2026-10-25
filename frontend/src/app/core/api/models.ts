/** DTOs of the HandyNet API. Money is always integer minor units (cents) in `...Minor` fields. */

export interface Paged<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export type ModuleName = 'ProductImport' | 'Inventory' | 'B2BPurchasing' | 'B2BSales' | 'Ankauf' | 'Pos';
export const ALL_MODULES: ModuleName[] = ['ProductImport', 'Inventory', 'B2BPurchasing', 'B2BSales', 'Ankauf', 'Pos'];

export type Permission =
  | 'organization.manage'
  | 'imports.manage'
  | 'offers.manage'
  | 'inventory.manage'
  | 'marketplace.browse'
  | 'orders.read'
  | 'orders.place'
  | 'orders.fulfil'
  | 'pos.sell'
  | 'pos.manage'
  | 'pos.price_override'
  | 'pos.discount'
  | 'pos.refund'
  | 'pos.cash_movement'
  | 'pos.close_session';

/** POS rights an owner can grant or withhold per staff member (owners and managers always hold all of them). */
export type PosConfigurablePermission = 'pos.price_override' | 'pos.discount' | 'pos.refund' | 'pos.cash_movement' | 'pos.close_session';
export const POS_CONFIGURABLE_PERMISSIONS: PosConfigurablePermission[] = [
  'pos.price_override',
  'pos.discount',
  'pos.refund',
  'pos.cash_movement',
  'pos.close_session',
];

export type OrganizationStatus = 'PendingVerification' | 'Verified' | 'Rejected' | 'Suspended';

/** POST /api/auth/login, /refresh and /change-password. The refresh token is opaque and rotates on every use. */
export interface TokenResponse {
  accessToken: string;
  expiresAt: string;
  refreshToken: string;
  refreshExpiresAt: string;
  userId: string;
  email: string;
  displayName: string;
  isPlatformAdmin: boolean;
  emailVerified: boolean;
}

/** Language of account emails (verification, password reset); the API accepts only these two. */
export type EmailLanguage = 'de' | 'en';

export interface Membership {
  organizationId: string;
  organizationName: string;
  role: string;
  status: OrganizationStatus;
  modules: string[];
  permissions: string[];
  isWholesaler: boolean;
  isRetailer: boolean;
  isRepairProvider: boolean;
  /** Largest POS line discount in percent (100 for owners/managers; missing on older API versions). */
  maxDiscountPercent?: number;
}

export interface Me {
  userId: string;
  email: string;
  displayName: string;
  isPlatformAdmin: boolean;
  emailVerified: boolean;
  memberships: Membership[];
}

export interface OrganizationInput {
  name: string;
  legalName: string;
  vatId?: string | null;
  street: string;
  postalCode: string;
  city: string;
  countryCode?: string | null;
  isWholesaler: boolean;
  isRetailer: boolean;
  isRepairProvider: boolean;
}

export interface RegistrationRequest {
  email: string;
  displayName: string;
  password: string;
  organization: OrganizationInput;
  /** Language of the verification email. */
  language?: EmailLanguage;
}

export interface StaffMember {
  userId: string;
  email: string;
  displayName: string;
  role: string;
  createdAt: string;
  /** Staff only: own POS rights replacing the defaults; null = role defaults. */
  posPermissions?: string[] | null;
  /** Staff only: own discount limit; null = 100 %. */
  posMaxDiscountPercent?: number | null;
  effectivePermissions?: string[];
  maxDiscountPercent?: number;
}

/** `PUT /api/organization/staff/{userId}/pos-rights` — null values return to the role defaults. */
export interface PosRightsRequest {
  permissions: string[] | null;
  maxDiscountPercent: number | null;
}

export type StaffRole = 'Manager' | 'Staff';

export interface AdminOrganization {
  id: string;
  name: string;
  legalName: string;
  vatId: string | null;
  street: string;
  postalCode: string;
  city: string;
  countryCode: string;
  isWholesaler: boolean;
  isRetailer: boolean;
  isRepairProvider: boolean;
  status: OrganizationStatus;
  statusReason: string | null;
  createdAt: string;
  verifiedAt: string | null;
  modules: string[];
  taxNumber: string | null;
  contactEmail: string | null;
  contactPhone: string | null;
  website: string | null;
  memberCount: number;
  registerCount: number;
  offerCount: number;
}

export type MemberRole = 'Owner' | 'Manager' | 'Staff';
export type OrganizationCapability = 'wholesaler' | 'retailer' | 'repair';

export interface AdminOrganizationQuery {
  status?: string;
  search?: string;
  module?: string;
  capability?: string;
  /** ISO timestamp, inclusive. */
  createdFrom?: string;
  /** ISO timestamp, exclusive. */
  createdTo?: string;
  page?: number;
  pageSize?: number;
}

export interface AdminPerson {
  id: string;
  email: string;
  displayName: string;
}

export interface AdminStatusChange {
  from: OrganizationStatus | null;
  to: OrganizationStatus;
  reason: string | null;
  at: string;
  by: AdminPerson | null;
}

export interface AdminMember {
  userId: string;
  email: string;
  displayName: string;
  role: MemberRole;
  joinedAt: string;
  emailVerified: boolean;
  isDisabled: boolean;
}

/** GET /api/admin/organizations/{id} — company master data, status history, members, registers (no operational data). */
export interface AdminOrganizationDetail {
  organization: AdminOrganization;
  /** Concurrency token; send back with a master-data update. */
  version: number;
  createdBy: AdminPerson | null;
  verifiedBy: AdminPerson | null;
  statusHistory: AdminStatusChange[];
  members: AdminMember[];
  registers: string[];
  publishedOfferCount: number;
}

/** Master data as edited by the platform admin (same rules as the registration). */
export interface OrganizationProfileInput {
  name: string;
  legalName: string;
  vatId: string | null;
  street: string;
  postalCode: string;
  city: string;
  countryCode: string;
  isWholesaler: boolean;
  isRetailer: boolean;
  isRepairProvider: boolean;
  taxNumber: string | null;
  contactEmail: string | null;
  contactPhone: string | null;
  website: string | null;
}

export interface UserMembershipInfo {
  organizationId: string;
  organizationName: string;
  role: MemberRole;
  organizationStatus: OrganizationStatus;
  joinedAt: string;
}

export interface AdminUserQuery {
  search?: string;
  emailVerified?: boolean;
  locked?: boolean;
  disabled?: boolean;
  platformAdmin?: boolean;
  noOrganization?: boolean;
  page?: number;
  pageSize?: number;
}

export interface AdminUser {
  id: string;
  email: string;
  displayName: string;
  emailVerified: boolean;
  createdAt: string;
  lastLoginAt: string | null;
  /** Temporarily locked after too many failed sign-ins. */
  isLockedOut: boolean;
  lockedUntil: string | null;
  /** Disabled by a platform admin. */
  isDisabled: boolean;
  isPlatformAdmin: boolean;
  memberships: UserMembershipInfo[];
}

/** GET /api/admin/users/{id} — account data only (never password hashes or tokens). */
export interface AdminUserDetail {
  id: string;
  email: string;
  displayName: string;
  emailVerified: boolean;
  emailVerifiedAt: string | null;
  createdAt: string;
  lastLoginAt: string | null;
  failedLoginCount: number;
  lockedUntil: string | null;
  isLockedOut: boolean;
  isDisabled: boolean;
  disabledAt: string | null;
  disabledReason: string | null;
  isPlatformAdmin: boolean;
  /** Signed-in devices (refresh-token families with a live token). */
  activeSessions: number;
  verificationEmailRequestedAt: string | null;
  passwordResetRequestedAt: string | null;
  memberships: UserMembershipInfo[];
  /** The signed-in admin's own account (self-disable / self-revoke are refused). */
  isSelf: boolean;
}

export type AdminUserAction =
  | 'disable'
  | 'enable'
  | 'clear-lockout'
  | 'revoke-sessions'
  | 'mark-email-verified'
  | 'grant-platform-admin'
  | 'revoke-platform-admin'
  | 'password-reset'
  | 'resend-verification';

export interface CatalogProduct {
  id: string;
  brand: string;
  name: string;
  category: string;
  quality: string;
  ean: string | null;
  mpn: string | null;
  [key: string]: unknown;
}

export interface CatalogCategories {
  categories: string[];
  qualities: string[];
  /** Categories stocked only by IMEI (one per unit), e.g. Smartphone. */
  imeiRequired: string[];
  /** Categories that may be stocked by IMEI (includes imeiRequired), e.g. Tablet, Wearable. */
  imeiAllowed: string[];
}

/** Canonical product image; URLs are relative to the API base and immutable (ADR-0011). */
export interface ProductImage {
  id: string;
  position: number;
  isPrimary: boolean;
  width: number;
  height: number;
  thumbnailWidth: number;
  thumbnailHeight: number;
  thumbnailUrl: string;
  fullUrl: string;
}

export interface ProductSummary {
  id: string;
  brand: string;
  name: string;
  category: string;
  quality: string;
  mpn: string | null;
  ean: string | null;
  modelName: string | null;
  compatibleModels: string[];
}

export type ImportStatus = 'Previewed' | 'Committed' | 'Discarded';
export type ImportRowStatus = 'Valid' | 'Error' | 'Conflict';

export interface ImportSummary {
  totalRows: number;
  validRows: number;
  errorRows: number;
  conflictRows: number;
  newProducts: number;
  newOffers: number;
  updatedOffers: number;
  unchangedOffers: number;
  stockChanges: number;
}

export interface ImportBatch {
  id: string;
  fileName: string;
  format: string;
  status: ImportStatus;
  warehouseId: string;
  publishOffers: boolean;
  syncStock: boolean;
  mapping: Record<string, string>;
  headers: string[];
  summary: ImportSummary;
  duplicateOfCommittedFile: boolean;
  createdAt: string;
  completedAt: string | null;
}

export interface ImportRow {
  rowNumber: number;
  status: ImportRowStatus;
  messages: string[];
  sku: string | null;
  ean: string | null;
  mpn: string | null;
  brand: string | null;
  name: string | null;
  category: string | null;
  quality: string | null;
  condition: string | null;
  priceMinor: number | null;
  oldPriceMinor: number | null;
  quantity: number | null;
  oldQuantity: number | null;
  productAction: 'MatchExisting' | 'CreateNew' | 'None';
  productId: string | null;
  offerAction: 'Create' | 'Update' | 'Unchanged' | 'None';
}

export interface ImportFields {
  fields: string[];
  required: string[];
}

export interface ImportOptions {
  warehouseId?: string | null;
  publishOffers: boolean;
  defaultDeliveryDays: number;
  defaultCondition: OfferCondition;
}

export type OfferCondition = 'New' | 'Refurbished' | 'Used';
export const OFFER_CONDITIONS: OfferCondition[] = ['New', 'Refurbished', 'Used'];
export type OfferStatus = 'Draft' | 'Published' | 'Paused' | 'Archived';

export interface OwnOffer {
  id: string;
  productId: string;
  brand: string;
  productName: string;
  ean: string | null;
  mpn: string | null;
  quality: string;
  warehouseId: string;
  supplierSku: string;
  priceMinor: number;
  currency: string;
  minOrderQuantity: number;
  deliveryDays: number;
  condition: OfferCondition;
  status: OfferStatus;
  onHand: number;
  reserved: number;
  updatedAt: string;
  /** Platform moderation as the seller sees it. */
  moderation: OfferModerationInfo;
}

/**
 * `blockedBy`: 'offer' = this offer is blocked by the platform (its status is frozen until the block is lifted);
 * 'seller' = the whole organization is blocked as a seller (all offers hidden, also new ones).
 */
export interface OfferModerationInfo {
  blocked: boolean;
  blockedBy: 'offer' | 'seller' | null;
  reason: string | null;
  blockedAt: string | null;
}

/** `GET /api/offers/moderation`: platform block of the current organization as a seller. */
export interface OwnModeration {
  sellerBlocked: boolean;
  reason: string | null;
  blockedAt: string | null;
}

// ---- platform moderation of marketplace offers (/api/admin/marketplace, no X-Organization-Id) ----

export type ModerationStatus = 'None' | 'Blocked';

export interface AdminModerationOffer {
  offerId: string;
  sellerOrganizationId: string;
  sellerName: string;
  sellerCity: string;
  productId: string;
  brand: string;
  productName: string;
  category: string;
  ean: string | null;
  mpn: string | null;
  supplierSku: string;
  priceMinor: number;
  currency: string;
  status: OfferStatus;
  moderation: ModerationStatus;
  moderationReason: string | null;
  moderatedAt: string | null;
  moderatedBy: string | null;
  sellerBlocked: boolean;
  sellerBlockReason: string | null;
  /** Hidden by the platform: the offer itself or its whole seller is blocked. */
  blocked: boolean;
  blockedBy: 'offer' | 'seller' | null;
  updatedAt: string;
}

export interface AdminModerationSeller {
  organizationId: string;
  name: string;
  city: string;
  verification: string;
  /** Offers that are not archived. */
  offerCount: number;
  publishedCount: number;
  /** Offers blocked individually (independent of the seller block). */
  blockedOfferCount: number;
  blocked: boolean;
  blockReason: string | null;
  blockChangedAt: string | null;
  blockChangedBy: string | null;
}

export interface AdminModerationQuery {
  sellerId?: string | null;
  brand?: string | null;
  category?: string | null;
  status?: OfferStatus | null;
  blocked?: boolean | null;
  search?: string | null;
  page?: number;
  pageSize?: number;
}

export interface BulkModerationResult {
  requested: number;
  /** Offers actually changed; already blocked/unblocked ones stay as they are. */
  changed: number;
  offers: AdminModerationOffer[];
}

export interface SellerModerationResult {
  seller: AdminModerationSeller;
  changed: boolean;
}

export interface CreateOfferRequest {
  productId: string;
  warehouseId?: string | null;
  supplierSku: string;
  price: number;
  minOrderQuantity: number;
  deliveryDays: number;
  condition: OfferCondition;
  publish: boolean;
}

export interface UpdateOfferRequest {
  price: number;
  minOrderQuantity: number;
  deliveryDays: number;
  condition: OfferCondition;
  status?: 'Published' | 'Paused' | 'Archived' | null;
}

export interface Warehouse {
  id: string;
  code: string;
  name: string;
  isDefault: boolean;
}

export interface StockRow {
  warehouseId: string;
  productId: string;
  brand: string;
  productName: string;
  ean: string | null;
  mpn: string | null;
  onHand: number;
  reserved: number;
  available: number;
  updatedAt: string;
}

export interface StockMovement {
  id: number;
  warehouseId: string;
  productId: string;
  quantity: number;
  onHandAfter: number;
  type: string;
  reference: string | null;
  occurredAt: string;
}

export interface Listing {
  offerId: string;
  sellerOrganizationId: string;
  sellerName: string;
  sellerCity: string;
  productId: string;
  brand: string;
  productName: string;
  category: string;
  quality: string;
  ean: string | null;
  mpn: string | null;
  supplierSku: string;
  priceMinor: number;
  currency: string;
  minOrderQuantity: number;
  deliveryDays: number;
  condition: OfferCondition;
  available: number;
  updatedAt: string;
  modelName: string | null;
  compatibleModels: string[];
  imageCount: number;
  /** Relative URL of the primary image's thumbnail; null without images. */
  primaryThumbnailUrl: string | null;
}

export interface ProductDetail {
  id: string;
  brand: string;
  name: string;
  category: string;
  quality: string;
  ean: string | null;
  mpn: string | null;
  modelName: string | null;
  compatibleModels: string[];
  images: ProductImage[];
}

/** `GET /api/marketplace/offers/{id}/detail` */
export interface OfferDetail {
  offer: Listing;
  product: ProductDetail;
  otherOffers: Listing[];
}

export interface FacetValue {
  value: string;
  count: number;
}

export interface SellerFacet {
  id: string;
  name: string;
  city: string;
  count: number;
}

/** `GET /api/marketplace/facets` — each list is counted without its own filter (disjunctive). */
export interface MarketplaceFacets {
  total: number;
  brands: FacetValue[];
  categories: FacetValue[];
  qualities: FacetValue[];
  conditions: FacetValue[];
  sellers: SellerFacet[];
  price: { minMinor: number; maxMinor: number } | null;
  withImages: number;
}

// ---------------------------------------------------------------- manual Lager entry ("Artikel anlegen")

export interface ManualEntryProductInput {
  brand: string;
  name: string;
  category: string;
  quality?: string | null;
  ean?: string | null;
  mpn?: string | null;
  modelName?: string | null;
  compatibleModels?: string[] | null;
}

export interface ManualEntryOfferInput {
  supplierSku: string;
  /** Net VK in EUR as decimal (two places at most). */
  price: number;
  minOrderQuantity: number;
  deliveryDays: number;
  condition: OfferCondition;
  publish: boolean;
}

/** `POST /api/inventory/items` (Idempotency-Key header required). Either productId or newProduct. */
export interface ManualEntryRequest {
  productId?: string | null;
  newProduct?: ManualEntryProductInput | null;
  warehouseId?: string | null;
  quantity?: number | null;
  imeis?: string[] | null;
  /** Net EK per unit in EUR as decimal. */
  purchasePrice?: number | null;
  offer?: ManualEntryOfferInput | null;
}

export type EntryProductAction = 'Selected' | 'MatchedExisting' | 'Created';

export interface ManualEntryResult {
  entryId: string;
  productAction: EntryProductAction;
  product: ProductSummary;
  quantityReceived: number;
  unitIds: string[];
  unitCostMinor: number | null;
  stock: { warehouseId: string; onHand: number; reserved: number; available: number };
  offer: {
    id: string;
    action: 'Created' | 'Updated';
    supplierSku: string;
    priceMinor: number;
    currency: string;
    minOrderQuantity: number;
    deliveryDays: number;
    condition: OfferCondition;
    status: OfferStatus;
  } | null;
  createdAt: string;
}

export interface LookupOwnOffer {
  id: string;
  supplierSku: string;
  priceMinor: number;
  minOrderQuantity: number;
  deliveryDays: number;
  condition: OfferCondition;
  status: OfferStatus;
}

export interface LookupMatch {
  product: ProductSummary;
  matchedBy: 'Ean' | 'Mpn' | 'Text';
  imageCount: number;
  primaryThumbnailUrl: string | null;
  /** The selected organization created the product and may manage its images. */
  canManageImages: boolean;
  imeiRequired: boolean;
  imeiAllowed: boolean;
  ownOnHand: number;
  ownAvailable: number;
  ownSerializedUnits: number;
  ownOffers: LookupOwnOffer[];
  suggestedSku: string;
}

/** `GET /api/inventory/items/lookup?q=` */
export interface LookupResult {
  query: string;
  isValidEan: boolean;
  normalizedEan: string | null;
  looksLikeImei: boolean;
  matches: LookupMatch[];
  canCreateProduct: boolean;
  canCreateOffer: boolean;
  /** Prefill for "new product" when no EAN match was found. */
  newProduct: { ean: string | null; mpn: string | null } | null;
}

export type MarketplaceSort = 'price' | 'price_desc' | 'delivery' | 'name';

export type OrderStatus =
  | 'Placed'
  | 'Confirmed'
  | 'Shipped'
  | 'Delivered'
  /** Buyer complained within the inspection window; automatic completion and payout are on hold. */
  | 'Disputed'
  | 'Completed'
  /** Dispute ended with a full refund (the online payment is refunded by a background job shortly afterwards). */
  | 'Refunded'
  | 'Cancelled'
  | 'Rejected';
/** Lifecycle order: Placed → Confirmed → Shipped → Delivered (→ Disputed) → Completed, then the closed states. */
export const ORDER_STATUSES: OrderStatus[] = [
  'Placed',
  'Confirmed',
  'Shipped',
  'Delivered',
  'Disputed',
  'Completed',
  'Refunded',
  'Cancelled',
  'Rejected',
];

export interface OrderLine {
  lineNumber: number;
  offerId: string;
  productId: string;
  supplierSku: string;
  brand: string;
  productName: string;
  quantity: number;
  unitPriceMinor: number;
  lineNetMinor: number;
}

export interface Order {
  id: string;
  number: string;
  status: OrderStatus;
  role: string;
  buyerOrganizationId: string;
  buyerName: string;
  sellerOrganizationId: string;
  sellerName: string;
  currency: string;
  netTotalMinor: number;
  vatRateBasisPoints: number;
  vatTotalMinor: number;
  grossTotalMinor: number;
  buyerReference: string | null;
  placedAt: string;
  confirmedAt: string | null;
  shippedAt: string | null;
  /** Set when the buyer confirmed receipt or the carrier reported delivery. */
  deliveredAt: string | null;
  /** End of the buyer's inspection window; afterwards the server completes the order automatically. */
  inspectionEndsAt: string | null;
  completedAt: string | null;
  closedAt: string | null;
  closeReason: string | null;
  lines: OrderLine[];
}

export interface PlaceOrderRequest {
  lines: { offerId: string; quantity: number; expectedUnitPriceMinor: number }[];
  buyerReference?: string | null;
}

// ---------------------------------------------------------------- Shipping

/** Carrier names as returned by `GET /api/shipments/carriers` (dev: Manual, Simulated). */
export type Carrier = string;

/** `LabelCreated` means booked with a label – it does NOT mean the parcel has left the seller. */
export type ShipmentStatus = 'LabelCreated' | 'HandedOver' | 'InTransit' | 'Delivered' | 'Exception' | 'Cancelled';

export type TrackingKind = 'Info' | 'PickedUp' | 'InTransit' | 'Exception' | 'Delivered';

export interface TrackingEvent {
  occurredAt: string;
  kind: TrackingKind;
  description: string;
  location: string | null;
}

export interface Shipment {
  id: string;
  orderId: string;
  carrier: Carrier;
  status: ShipmentStatus;
  trackingNumber: string;
  weightGrams: number;
  declaredValueMinor: number;
  insured: boolean;
  containsLithiumBatteries: boolean;
  hasLabel: boolean;
  createdAt: string;
  handedOverAt: string | null;
  deliveredAt: string | null;
  lastSyncedAt: string | null;
  events: TrackingEvent[];
}

export interface CreateShipmentRequest {
  orderId: string;
  carrier: Carrier;
  weightGrams: number;
  lengthCm: number;
  widthCm: number;
  heightCm: number;
  insured: boolean;
  trackingNumber?: string | null;
}

/** Limits of a carrier product (`GET /api/shipments/carrier-profiles`). The API remains authoritative. */
export interface CarrierProfile {
  carrier: Carrier;
  /** Goods value covered without extra insurance; above it the shipment must be insured. */
  standardLiabilityMinor: number;
  /** Highest value that can be insured at all. */
  maxInsuredValueMinor: number;
  maxWeightGrams: number;
  /** Longest side. */
  maxLengthCm: number;
  acceptsLithiumInEquipment: boolean;
  acceptsStandaloneLithiumBatteries: boolean;
}

// ---------------------------------------------------------------- Payments

export type PaymentStatus = 'Open' | 'Paid' | 'Failed' | 'Canceled' | 'Expired' | 'Refunded';

export interface Payment {
  id: string;
  orderId: string;
  /** e.g. "B2B-001042" */
  orderNumber: string;
  /** Payment service provider, e.g. "Simulated" in development. */
  provider: string;
  status: PaymentStatus;
  amountMinor: number;
  currency: string;
  /** Only for the buyer while the payment is Open. */
  checkoutUrl: string | null;
  createdAt: string;
  paidAt: string | null;
  refundedAt: string | null;
  /** Seller only (else null). */
  commissionNetMinor: number | null;
  commissionVatMinor: number | null;
  sellerShareMinor: number | null;
}

export interface Payout {
  id: string;
  orderId: string;
  /** e.g. "B2B-001042" */
  orderNumber: string;
  amountMinor: number;
  currency: string;
  providerTransferId: string;
  createdAt: string;
}

export interface SellerBalance {
  /** What the marketplace owes the seller for paid sales that are not paid out yet. */
  openBalanceMinor: number;
  currency: string;
  recentPayouts: Payout[];
}

export type LedgerSourceType = 'payment' | 'payout';

export interface LedgerLine {
  entryId: string;
  occurredAt: string;
  description: string;
  sourceType: LedgerSourceType | string;
  sourceId: string;
  account: string;
  /** Negative = credit to the seller (Gutschrift), positive = debit (Auszahlung). */
  amountMinor: number;
  currency: string;
}

// ---------------------------------------------------------------- Disputes

export type DisputeReason = 'Damaged' | 'WrongItem' | 'NotAsDescribed' | 'MissingItems' | 'Defective' | 'Other';
export const DISPUTE_REASONS: DisputeReason[] = ['Damaged', 'WrongItem', 'NotAsDescribed', 'MissingItems', 'Defective', 'Other'];

/** Open: the parties try to settle it; Escalated: HandyNet decides; Refunded / ClosedWithoutRefund: closed. */
export type DisputeStatus = 'Open' | 'Escalated' | 'Refunded' | 'ClosedWithoutRefund';
export const DISPUTE_STATUSES: DisputeStatus[] = ['Open', 'Escalated', 'Refunded', 'ClosedWithoutRefund'];

export type DisputeParty = 'Buyer' | 'Seller' | 'Platform';
export const DISPUTE_PARTIES: DisputeParty[] = ['Buyer', 'Seller', 'Platform'];

export interface DisputeMessage {
  from: DisputeParty;
  text: string;
  sentAt: string;
}

export interface Dispute {
  id: string;
  orderId: string;
  /** Perspective of the calling organization (admin views: the seller's, see DisputeAdministration). */
  role: 'buyer' | 'seller';
  buyerOrganizationId: string;
  sellerOrganizationId: string;
  reason: DisputeReason;
  status: DisputeStatus;
  openedAt: string;
  escalatedAt: string | null;
  resolvedAt: string | null;
  resolvedBy: DisputeParty | null;
  resolution: string | null;
  /** Chronological (oldest first); the first message is the buyer's description. */
  messages: DisputeMessage[];
}

/** Platform admin queue row (`GET /api/admin/disputes`): escalated disputes only, no organization names. */
export interface EscalatedDispute {
  id: string;
  orderId: string;
  orderNumber: string;
  buyerOrganizationId: string;
  buyerName: string;
  sellerOrganizationId: string;
  sellerName: string;
  reason: DisputeReason;
  escalatedAt: string;
}

// ---------------------------------------------------------------- Ankauf (used-device buyback)

export type PriceListStatus = 'Review' | 'Published' | 'Discarded';
export type PriceChange = 'New' | 'Unchanged' | 'Increase' | 'Decrease' | 'LargeDecrease';
export type EntryDecision = 'AutoAccepted' | 'Pending' | 'Approved' | 'Rejected';
export type DeviceCondition = 'Neu' | 'WieNeu' | 'SehrGut' | 'Gut' | 'Akzeptabel';
export type DeviceDefect = 'ScreenDamage' | 'HousingDamage' | 'CameraDefect' | 'BiometricDefect' | 'MissingFunction';

export const DEVICE_CONDITIONS: DeviceCondition[] = ['Neu', 'WieNeu', 'SehrGut', 'Gut', 'Akzeptabel'];
export const DEVICE_DEFECTS: DeviceDefect[] = ['ScreenDamage', 'HousingDamage', 'CameraDefect', 'BiometricDefect', 'MissingFunction'];
export const PRICE_CHANGES: PriceChange[] = ['New', 'Unchanged', 'Increase', 'Decrease', 'LargeDecrease'];
export const ENTRY_DECISIONS: EntryDecision[] = ['Pending', 'Approved', 'Rejected', 'AutoAccepted'];

export interface PriceListSummary {
  total: number;
  new: number;
  unchanged: number;
  increases: number;
  decreases: number;
  largeDecreases: number;
  pending: number;
  approved: number;
  rejected: number;
  autoAccepted: number;
  skipped: number;
}

export interface SkippedPriceRow {
  rowNumber: number;
  text: string;
  reason: string;
}

export interface PriceList {
  id: string;
  label: string;
  fileName: string;
  sheetName: string;
  validFrom: string;
  validUntil: string;
  status: PriceListStatus;
  importedAt: string;
  publishedAt: string | null;
  summary: PriceListSummary;
  skippedRows: SkippedPriceRow[];
}

export interface PriceListEntry {
  id: number;
  rowNumber: number;
  brand: string;
  model: string;
  storage: string | null;
  priceMinor: number;
  previousPriceMinor: number | null;
  change: PriceChange;
  decision: EntryDecision;
  decidedAt: string | null;
}

/** Condition → basis points (100 = 1 %). */
export type ConditionMap = Partial<Record<DeviceCondition, number>>;

/** Pricing policy parameters; all percentages are basis points (100 = 1 %). */
export interface PolicySettings {
  conditionAdjustments: Record<DeviceCondition, number>;
  /** Normalized brand key (e.g. "APPLE") → condition overrides. */
  brandOverrides: Record<string, ConditionMap>;
  /** Normalized model key ("BRAND|MODEL|STORAGE") → condition overrides. */
  modelOverrides: Record<string, ConditionMap>;
  defectDeductions: Record<DeviceDefect, number>;
  batteryHealthThresholdPercent: number;
  batteryDeduction: number;
  platformMargin: number;
  minDealerMargin: number;
}

export interface Policy {
  id: string;
  number: number;
  settings: PolicySettings;
  effectiveFrom: string;
  createdAt: string;
  isActive: boolean;
}

export interface AnkaufModel {
  modelKey: string;
  brand: string;
  model: string;
  storage: string | null;
  priceValidUntil: string;
}

export interface CreateQuoteRequest {
  modelKey: string;
  condition: DeviceCondition;
  defects?: DeviceDefect[];
  batteryHealthPercent?: number | null;
  /** Decimal euros (the API converts to cents). */
  consumerPrice?: number | null;
  imei?: string | null;
}

export interface Quote {
  id: string;
  brand: string;
  model: string;
  storage: string | null;
  condition: DeviceCondition;
  defects: DeviceDefect[];
  batteryHealthPercent: number | null;
  imei: string | null;
  referenceGutMinor: number;
  conditionAdjustmentBasisPoints: number;
  totalDeductionBasisPoints: number;
  referencePurchasePriceMinor: number;
  platformOfferMinor: number;
  maxConsumerPriceMinor: number;
  consumerPriceMinor: number | null;
  dealerMarginMinor: number | null;
  priceVersionId: string;
  policyNumber: number;
  createdAt: string;
  validUntil: string;
  notice: string;
}

// ---------------------------------------------------------------- Ankauf device intake

export type IntakeStatus = 'Received' | 'Inspected' | 'Completed' | 'Rejected';
export type IdDocumentType = 'IdCard' | 'Passport' | 'ResidencePermit';
export type IntakePaymentMethod = 'Cash' | 'BankTransfer';

export const ID_DOCUMENT_TYPES: IdDocumentType[] = ['IdCard', 'Passport', 'ResidencePermit'];
export const INTAKE_PAYMENT_METHODS: IntakePaymentMethod[] = ['Cash', 'BankTransfer'];

export interface SellerDetails {
  firstName: string;
  lastName: string;
  street: string;
  postalCode: string;
  city: string;
  /** yyyy-MM-dd */
  birthDate: string;
  idDocumentType: IdDocumentType;
  idDocumentNumber: string;
}

export interface ReceiveIntakeRequest {
  quoteId: string;
  imei: string;
  seller: SellerDetails;
}

export interface InspectIntakeRequest {
  condition: DeviceCondition;
  defects: DeviceDefect[];
  batteryHealthPercent: number | null;
  notes: string | null;
}

export interface CompleteIntakeRequest {
  /** Decimal euros (the API converts to cents). */
  paidAmount: number;
  paymentMethod: IntakePaymentMethod;
  /** Null = the organization's default warehouse. */
  warehouseId: string | null;
}

export interface Intake {
  id: string;
  quoteId: string;
  brand: string;
  model: string;
  storage: string | null;
  imei: string;
  status: IntakeStatus;
  sellerName: string;
  receivedAt: string;
  quotedOfferMinor: number;
  inspectedCondition: DeviceCondition | null;
  inspectedDefects: DeviceDefect[];
  inspectedBatteryHealthPercent: number | null;
  inspectionNotes: string | null;
  inspectedOfferMinor: number | null;
  inspectedMaxConsumerPriceMinor: number | null;
  paidMinor: number | null;
  paymentMethod: IntakePaymentMethod | null;
  stockUnitId: string | null;
  completedAt: string | null;
  rejectionReason: string | null;
}

// ---------------------------------------------------------------- POS (Kasse)

export type PosPaymentMethod = 'Cash' | 'Card';
export const POS_PAYMENT_METHODS: PosPaymentMethod[] = ['Cash', 'Card'];
export type ReceiptKind = 'Sale' | 'Storno' | 'Return' | 'InvoicePayment';
export type TaxTreatment = 'Standard19' | 'Margin25a' | 'NotTaxable';
export type CashMovementKind = 'Deposit' | 'Withdrawal';

export interface PosRegister {
  id: string;
  name: string;
  warehouseId: string;
  isActive: boolean;
  nextReceiptNumber: number;
  openSessionId: string | null;
  createdAt: string;
  /** Shared device: lock the till after this many idle minutes (null = off). */
  autoLockMinutes?: number | null;
  /** Round cash totals to 0.05 € on this register. */
  cashRounding?: boolean;
}

export interface CashMovement {
  id: string;
  kind: CashMovementKind;
  amountMinor: number;
  reason: string;
  createdAt: string;
  createdBy: string;
  createdByName: string | null;
  reasonCode?: string | null;
  approval?: PosApprovalInfo | null;
}

/** `GET /api/pos/cash-movement-reasons`; `Other` needs a free text. */
export interface CashMovementReason {
  code: string;
  kind: CashMovementKind;
  label: string;
  requiresText: boolean;
}

export interface CashMovementRequest {
  kind: CashMovementKind;
  amountMinor: number;
  reason: string | null;
  reasonCode?: string | null;
}

/** Counted coins/notes of one euro denomination (cents). */
export interface DenominationCount {
  valueMinor: number;
  count: number;
}

export interface PaymentTotal {
  method: PosPaymentMethod;
  amountMinor: number;
}

export interface TaxTotal {
  treatment: TaxTreatment;
  vatRateBasisPoints: number;
  grossMinor: number;
  netMinor: number;
  vatMinor: number;
}

/** Z-report figures (live for an open session, stored for a closed one). */
export interface SessionSummary {
  receiptCount: number;
  saleCount: number;
  stornoCount: number;
  returnCount: number;
  totalGrossMinor: number;
  totalNetMinor: number;
  totalVatMinor: number;
  byPaymentMethod: PaymentTotal[];
  byTaxTreatment: TaxTotal[];
  openingCashMinor: number;
  cashPaymentsMinor: number;
  depositsMinor: number;
  withdrawalsMinor: number;
  expectedCashMinor: number;
  firstReceiptNumber: number | null;
  lastReceiptNumber: number | null;
  /** Sum of cash rounding of the session (0.05 € registers). */
  roundingMinor?: number;
  /** Payments on sales invoices collected at the till (not taxable here; the invoice carries the VAT). */
  invoicePaymentCount?: number;
  invoicePaymentsMinor?: number;
}

export interface CashSession {
  id: string;
  registerId: string;
  registerName: string;
  status: 'Open' | 'Closed';
  openedAt: string;
  openedBy: string;
  openedByName: string | null;
  openingCashMinor: number;
  closedAt: string | null;
  closedBy: string | null;
  closedByName: string | null;
  expectedCashMinor: number;
  countedCashMinor: number | null;
  differenceMinor: number | null;
  closingNote: string | null;
  summary: SessionSummary;
  movements: CashMovement[];
  /** Opening/closing cash counted by denomination (null when entered as a plain amount). */
  openingDenominations?: DenominationCount[] | null;
  closingDenominations?: DenominationCount[] | null;
  /** Manager approval used for the closing, if any. */
  closingApproval?: PosApprovalInfo | null;
  /** Gapless Z number of the register (DSFinV-K Z_NR), assigned at closing; null while open. */
  zNumber?: number | null;
}

/** Where the till's price comes from: own POS price, own marketplace offer (net + 19 %), or none (cashier types it). */
export type PriceSource = 'PosPrice' | 'Offer' | 'None';

/** `GET /api/pos/registers/{id}/products` — prices are gross cents. */
export interface PosProduct {
  productId: string;
  brand: string;
  name: string;
  category: string;
  quality: string;
  ean: string | null;
  mpn: string | null;
  imageUrl: string | null;
  /** Sold by IMEI: scan the device instead of picking the product. */
  serialized: boolean;
  onHand: number;
  reserved: number;
  available: number;
  priceMinor: number | null;
  priceSource: PriceSource;
  posPriceMinor: number | null;
}

export interface PosCategoryCount {
  category: string;
  productCount: number;
}

/** `GET/PUT /api/pos/prices/{productId}`. */
export interface PosPrice {
  productId: string;
  posPriceMinor: number | null;
  offerNetPriceMinor: number | null;
  priceMinor: number | null;
  priceSource: PriceSource;
  updatedAt: string | null;
}

export interface PosCustomerInput {
  name: string;
  company?: string | null;
  email?: string | null;
  phone?: string | null;
  street?: string | null;
  postalCode?: string | null;
  city?: string | null;
  countryCode?: string | null;
  vatId?: string | null;
}

export interface PosCustomer extends PosCustomerInput {
  id: string;
  company: string | null;
  email: string | null;
  phone: string | null;
  street: string | null;
  postalCode: string | null;
  city: string | null;
  countryCode: string | null;
  vatId: string | null;
  createdAt: string;
  updatedAt: string;
}

/** The customer as printed on the receipt (snapshot; no email/phone). */
export interface ReceiptCustomer {
  id: string;
  name: string;
  company: string | null;
  street: string | null;
  postalCode: string | null;
  city: string | null;
  countryCode: string | null;
  vatId: string | null;
}

/** `GET /api/pos/sales` filters (all optional; `to` is exclusive). */
export interface SaleQuery {
  registerId?: string | null;
  sessionId?: string | null;
  number?: number | null;
  from?: string | null;
  to?: string | null;
  minTotalMinor?: number | null;
  maxTotalMinor?: number | null;
  cashierId?: string | null;
  customerId?: string | null;
  paymentMethod?: PosPaymentMethod | null;
  kind?: ReceiptKind | null;
  page?: number;
  pageSize?: number;
}

/** `POST /api/pos/sales/{id}/reprints`: the copy number to print ("Kopie n") and the unchanged receipt. */
export interface ReceiptReprint {
  sequence: number;
  printedAt: string;
  printedBy: string;
  receipt: Receipt;
}

export interface ProductSales {
  productId: string;
  description: string;
  soldQuantity: number;
  returnedQuantity: number;
  netQuantity: number;
  grossMinor: number;
  discountMinor: number;
}

export interface CashierSales {
  userId: string;
  name: string | null;
  saleCount: number;
  refundCount: number;
  grossMinor: number;
}

export interface PaymentMethodSales {
  method: PosPaymentMethod;
  receiptCount: number;
  amountMinor: number;
}

/** `GET /api/pos/reports/sales` — gross figures, signed (returns/Storno negative). */
export interface SalesReport {
  sessionId: string | null;
  registerId: string | null;
  from: string | null;
  to: string | null;
  receiptCount: number;
  totalGrossMinor: number;
  byProduct: ProductSales[];
  byCashier: CashierSales[];
  byPaymentMethod: PaymentMethodSales[];
}

export interface PosLookup {
  kind: 'Product' | 'Unit';
  productId: string;
  brand: string;
  name: string;
  ean: string | null;
  imei: string | null;
  stockUnitId: string | null;
  taxTreatment: TaxTreatment | null;
  warehouseId: string | null;
  onHand: number | null;
  reserved: number | null;
  available: number | null;
  /** Stocked as serialized devices in the register's warehouse: scan the IMEI instead of the EAN. */
  requiresImei: boolean;
  /** Till price (gross cents) and its source; missing on older API versions. */
  priceMinor?: number | null;
  priceSource?: PriceSource;
}

export interface SaleLineRequest {
  productId?: string | null;
  imei?: string | null;
  quantity: number;
  unitPriceMinor: number;
  discountMinor?: number | null;
  /** Printed under the line (max 200 characters). */
  note?: string | null;
}

export interface SalePayment {
  method: PosPaymentMethod;
  amountMinor: number;
}

export interface CreateSaleRequest {
  registerId: string;
  lines: SaleLineRequest[];
  payments: SalePayment[];
  customerId?: string | null;
}

export interface ReturnRequest {
  lines: { lineNumber: number; quantity: number }[];
  refundMethod: PosPaymentMethod;
  reason: string;
  registerId?: string | null;
}

export interface ReceiptLine {
  lineNumber: number;
  /** Null on the line of an invoice payment receipt. */
  productId: string | null;
  stockUnitId: string | null;
  imei: string | null;
  description: string;
  /** Negative on Storno/Return receipts. */
  quantity: number;
  unitPriceMinor: number;
  discountMinor: number;
  lineGrossMinor: number;
  taxTreatment: TaxTreatment;
  vatRateBasisPoints: number;
  /** Null for §25a lines (the margin VAT is never exposed). */
  vatMinor: number | null;
  originalLineNumber: number | null;
  returnedQuantity: number;
  note?: string | null;
}

export interface ReceiptPayment {
  method: PosPaymentMethod;
  amountMinor: number;
  tenderedMinor: number | null;
}

export interface ReceiptVat {
  label: string;
  rateBasisPoints: number;
  grossMinor: number;
  netMinor: number;
  vatMinor: number;
}

export interface ReceiptFiscal {
  status: string;
  notice: string | null;
  tseSerialNumber: string | null;
  transactionNumber: number | null;
  signatureCounter: number | null;
  signature: string | null;
  startedAt: string | null;
  finishedAt: string | null;
  /** Fiscal layer (ADR-0012); absent on receipts issued before it. "Failed" status = TSE outage. */
  mode?: string | null;
  /** Signed by the simulated test TSE or a fiskaly sandbox — never valid for real operation. */
  isTest?: boolean;
  clientId?: string | null;
  signatureAlgorithm?: string | null;
  timeFormat?: string | null;
  publicKey?: string | null;
  processType?: string | null;
  processData?: string | null;
  /** DSFinV-K QR code content (V0;…). */
  qrPayload?: string | null;
}

export interface ReceiptSeller {
  name: string;
  legalName: string;
  street: string;
  postalCode: string;
  city: string;
  countryCode: string;
  vatId: string | null;
}

export interface ReceiptDocumentLine {
  description: string;
  imei: string | null;
  quantity: number;
  unitPriceMinor: number;
  discountMinor: number;
  lineGrossMinor: number;
  /** "A" (19 %) or "§25a". */
  taxLabel: string;
  note?: string | null;
}

/** The printable receipt exactly as issued (stored with the receipt; a reprint always shows the original). */
export interface ReceiptDocument {
  title: string;
  seller: ReceiptSeller;
  registerName: string;
  receiptNumber: number;
  kind: ReceiptKind;
  referencedReceiptNumber: number | null;
  issuedAt: string;
  currency: string;
  lines: ReceiptDocumentLine[];
  vat: ReceiptVat[];
  totalGrossMinor: number;
  payments: ReceiptPayment[];
  changeMinor: number;
  notes: string[];
  fiscal: ReceiptFiscal;
  customer?: ReceiptCustomer | null;
  /** Cash rounding to 0.05 € (payments = total + rounding); 0/absent without rounding. */
  roundingMinor?: number;
}

export interface Receipt {
  id: string;
  registerId: string;
  registerName: string;
  sessionId: string;
  number: number;
  kind: ReceiptKind;
  referencedReceiptId: string | null;
  referencedReceiptNumber: number | null;
  cancelledByReceiptId: string | null;
  createdAt: string;
  createdBy: string;
  createdByName: string | null;
  currency: string;
  totalGrossMinor: number;
  changeMinor: number;
  reason: string | null;
  lines: ReceiptLine[];
  payments: ReceiptPayment[];
  vat: ReceiptVat[];
  fiscal: ReceiptFiscal | null;
  document: ReceiptDocument;
  customerId?: string | null;
  customer?: ReceiptCustomer | null;
  reprintCount?: number;
  lastReprintedAt?: string | null;
  /** Manager approval used for this receipt (who approved), if any. */
  approval?: PosApprovalInfo | null;
  roundingMinor?: number;
}

export interface ReceiptSummary {
  id: string;
  registerId: string;
  sessionId: string;
  number: number;
  kind: ReceiptKind;
  referencedReceiptId: string | null;
  createdAt: string;
  totalGrossMinor: number;
  createdBy?: string;
  createdByName?: string | null;
  customerId?: string | null;
  customerName?: string | null;
  paymentMethods?: PosPaymentMethod[];
  reprintCount?: number;
}

// ---------------------------------------------------------------- Invoicing (commission invoices, ADR-0009)

/** `Invoice` = monthly commission invoice (HN-YYYY-nnnnnn); `CreditNote` = correction/Storno (HN-RK-YYYY-nnnnnn, negative amounts). */
export type InvoiceKind = 'Invoice' | 'CreditNote';

/** Issuer or recipient as printed on the document (immutable snapshot). */
export interface InvoiceParty {
  name: string;
  legalName: string | null;
  street: string;
  postalCode: string;
  city: string;
  countryCode: string;
  vatId: string | null;
  taxNumber: string | null;
}

/** List row (`GET /api/invoices`, admin month listing, issue run result). */
export interface InvoiceSummary {
  id: string;
  kind: InvoiceKind;
  number: string;
  /** German local calendar date, `yyyy-MM-dd`. */
  issueDate: string;
  /** `yyyy-MM`; for corrections the month in which the refunds happened. */
  billingMonth: string;
  servicePeriodStart: string;
  servicePeriodEnd: string;
  totalNetMinor: number;
  totalVatMinor: number;
  totalGrossMinor: number;
  currency: string;
  /** Corrections: the corrected invoice. */
  referencedInvoiceId: string | null;
  referencedInvoiceNumber: string | null;
  organizationId: string;
  recipientName: string;
  lineCount: number;
}

export interface InvoiceLine {
  lineNumber: number;
  orderId: string;
  orderNumber: string;
  paymentId: string;
  paidAt: string;
  netMinor: number;
  vatMinor: number;
  grossMinor: number;
  /** Credit lines: the invoice whose line they negate. */
  creditedInvoiceId: string | null;
}

/** `GET /api/invoices/{id}` */
export interface Invoice {
  id: string;
  kind: InvoiceKind;
  number: string;
  issueDate: string;
  issuedAt: string;
  billingMonth: string;
  servicePeriodStart: string;
  servicePeriodEnd: string;
  /** 1900 = 19 %. */
  vatRateBasisPoints: number;
  totalNetMinor: number;
  totalVatMinor: number;
  totalGrossMinor: number;
  currency: string;
  referencedInvoiceId: string | null;
  referencedInvoiceNumber: string | null;
  issuer: InvoiceParty;
  recipient: InvoiceParty;
  lines: InvoiceLine[];
}

export interface IssueFailure {
  organizationId: string;
  code: string;
  message: string;
}

/** `POST /api/admin/invoices/issue` — idempotent: existing documents are returned and counted in `alreadyIssued`. */
export interface IssueRunResult {
  month: string;
  sellersChecked: number;
  invoicesIssued: number;
  creditNotesIssued: number;
  alreadyIssued: number;
  /** Created and already existing documents of the run, ordered by number. */
  documents: InvoiceSummary[];
  failures: IssueFailure[];
}

// ---- POS cashier switching (PIN) -------------------------------------------------------------------------------------
export interface PosPinStatus {
  userId: string;
  hasPin: boolean;
  lockedUntil: string | null;
  updatedAt: string | null;
}

/** A member who can be switched to at the till (has a PIN). */
export interface PosCashierOption {
  userId: string;
  displayName: string;
  role: string;
  locked: boolean;
}

/** The cashier the till acts as (effective POS rights of that member). */
export interface PosCashier {
  sessionId: string;
  registerId: string;
  userId: string;
  displayName: string | null;
  role: string;
  permissions: string[];
  maxDiscountPercent: number;
  expiresAt: string;
  autoLockMinutes: number | null;
}

export interface PosCashierSignIn {
  token: string;
  cashier: PosCashier;
}

/** A manager approval by PIN for the next action (value of X-Pos-Approval; single use). */
export interface PosApprovalGrant {
  token: string;
  approvalId: string;
  expiresAt: string;
  approverUserId: string;
  approverName: string | null;
  permissions: string[];
}

/** Who approved a recorded action (receipt, cash movement, closing). */
export interface PosApprovalInfo {
  approvalId: string;
  approverUserId: string;
  approverName: string | null;
  permissions: string[];
  approvedAt: string;
}

// ---- POS e-receipts -------------------------------------------------------------------------------------------------
export interface PosReceiptEmail {
  id: string;
  receiptId: string;
  /** e.g. "k***@example.de" (never the full address on a shared screen). */
  recipientMasked: string;
  requestedAt: string;
}

export interface PosReceiptLink {
  token: string;
  /** Web app path, e.g. "/beleg/{token}". */
  path: string;
  expiresAt: string;
}

export interface PosPublicReceipt {
  document: ReceiptDocument;
  cancelled: boolean;
  expiresAt: string;
}

// ---- POS invoices (Rechnung for a sale) -----------------------------------------------------------------------------
export interface PosInvoiceCustomerInput {
  name: string;
  company?: string | null;
  street: string;
  postalCode: string;
  city: string;
  countryCode?: string | null;
  vatId?: string | null;
}

export interface PosInvoice {
  id: string;
  receiptId: string;
  number: string;
  issueDate: string;
  issuedAt: string;
  issuedBy: string;
  totalNetMinor: number;
  totalVatMinor: number;
  totalGrossMinor: number;
  document: {
    customer: { name: string; company: string | null; street: string; postalCode: string; city: string; countryCode: string; vatId: string | null };
    marginGrossMinor: number;
    vat: { rateBasisPoints: number; netMinor: number; vatMinor: number; grossMinor: number }[];
  };
  pdfUrl: string;
}

// ---------------------------------------------------------------- Quotations and sales invoices (POS, ADR-0014)

export type PriceMode = 'Gross' | 'Net';
/** Stored statuses plus the derived "Expired" (open quotation past its validity date). */
export type QuotationStatus = 'Draft' | 'Sent' | 'Accepted' | 'Rejected' | 'Expired';
export type SalesInvoiceKind = 'Invoice' | 'CreditNote';
export type SalesInvoiceStatus = 'Draft' | 'Issued' | 'Discarded';
export type InvoicePaymentMethod = 'BankTransfer' | 'Card' | 'Cash' | 'Other';
export type InvoicePaymentState = 'Open' | 'PartiallyPaid' | 'Paid';
export const SALES_VAT_RATES = [1900, 700, 0] as const;

export interface SalesCustomer {
  customerId: string | null;
  name: string;
  company: string | null;
  street: string | null;
  postalCode: string | null;
  city: string | null;
  countryCode: string | null;
  vatId: string | null;
}

/** A stored line: amountMinor = unit price × quantity − discount, in the document's price mode (negative on credit notes). */
export interface SalesLine {
  position: number;
  productId: string | null;
  stockUnitId: string | null;
  imei: string | null;
  description: string;
  note: string | null;
  quantity: number;
  unitPriceMinor: number;
  discountMinor: number;
  amountMinor: number;
  vatRateBasisPoints: number;
  margin25a: boolean;
}

export interface SalesVat {
  rateBasisPoints: number;
  netMinor: number;
  vatMinor: number;
  grossMinor: number;
}

export interface SalesLineRequest {
  productId?: string | null;
  imei?: string | null;
  description?: string | null;
  note?: string | null;
  quantity: number;
  unitPriceMinor: number;
  discountMinor?: number | null;
  vatRateBasisPoints?: number | null;
}

export interface SalesCustomerRequest {
  name: string;
  company?: string | null;
  street?: string | null;
  postalCode?: string | null;
  city?: string | null;
  countryCode?: string | null;
  vatId?: string | null;
}

export interface QuotationRequest {
  customerId?: string | null;
  customer?: SalesCustomerRequest | null;
  validUntil?: string | null;
  priceMode?: PriceMode | null;
  lines: SalesLineRequest[];
  notes?: string | null;
}

export interface SalesInvoiceRequest {
  customerId?: string | null;
  customer?: SalesCustomerRequest | null;
  serviceDate?: string | null;
  paymentTermDays?: number | null;
  priceMode?: PriceMode | null;
  reverseCharge?: boolean | null;
  warehouseId?: string | null;
  lines: SalesLineRequest[];
  notes?: string | null;
}

interface SalesTotalsFields {
  vat: SalesVat[];
  marginGrossMinor: number;
  totalNetMinor: number;
  totalVatMinor: number;
  totalGrossMinor: number;
}

export interface Quotation extends SalesTotalsFields {
  id: string;
  number: string;
  status: QuotationStatus;
  storedStatus: Exclude<QuotationStatus, 'Expired'>;
  editable: boolean;
  customerId: string | null;
  customer: SalesCustomer;
  issueDate: string;
  validUntil: string;
  priceMode: PriceMode;
  lines: SalesLine[];
  notes: string | null;
  convertedInvoiceId: string | null;
  convertedInvoiceNumber: string | null;
  sentAt: string | null;
  createdAt: string;
  createdBy: string;
  createdByName: string | null;
  updatedAt: string;
  pdfUrl: string;
}

export interface QuotationSummary {
  id: string;
  number: string;
  status: QuotationStatus;
  customerName: string;
  customerCompany: string | null;
  issueDate: string;
  validUntil: string;
  totalGrossMinor: number;
  convertedInvoiceId: string | null;
}

export interface InvoicePayment {
  id: string;
  method: InvoicePaymentMethod;
  amountMinor: number;
  paidOn: string;
  receiptId: string | null;
  receiptNumber: number | null;
  reference: string | null;
  createdAt: string;
  createdBy: string;
  createdByName: string | null;
}

export interface SalesInvoice extends SalesTotalsFields {
  id: string;
  kind: SalesInvoiceKind;
  status: SalesInvoiceStatus;
  number: string | null;
  customerId: string | null;
  customer: SalesCustomer | null;
  invoiceDate: string | null;
  serviceDate: string | null;
  paymentTermDays: number;
  dueDate: string | null;
  priceMode: PriceMode;
  reverseCharge: boolean;
  warehouseId: string | null;
  quotationId: string | null;
  quotationNumber: string | null;
  originalInvoiceId: string | null;
  originalNumber: string | null;
  creditNoteId: string | null;
  creditNoteNumber: string | null;
  reason: string | null;
  notes: string | null;
  lines: SalesLine[];
  paidMinor: number;
  openMinor: number;
  paymentState: InvoicePaymentState | null;
  overdue: boolean;
  cancelled: boolean;
  payments: InvoicePayment[];
  issuedAt: string | null;
  createdAt: string;
  createdBy: string;
  createdByName: string | null;
  pdfUrl: string;
  /** Draft hints: "customer_vat_id_missing", "reverse_charge_check". */
  warnings: string[];
}

/** One row of the invoice list. Origin "Receipt" = invoice for a POS sale (read only, paid at the till). */
export interface InvoiceListItem {
  id: string;
  origin: 'Sale' | 'Receipt';
  kind: SalesInvoiceKind;
  status: SalesInvoiceStatus;
  number: string | null;
  customerName: string | null;
  customerCompany: string | null;
  invoiceDate: string | null;
  dueDate: string | null;
  totalGrossMinor: number;
  paidMinor: number;
  paymentState: InvoicePaymentState | null;
  overdue: boolean;
  cancelled: boolean;
  receiptId: string | null;
}

export interface SalesInvoiceSummary {
  overdueCount: number;
  overdueMinor: number;
  openCount: number;
  openMinor: number;
  draftCount: number;
}

export interface SalesQuery {
  search?: string | null;
  status?: string | null;
  customerId?: string | null;
  from?: string | null;
  to?: string | null;
  kind?: string | null;
  paymentState?: string | null;
  overdue?: boolean | null;
  page?: number;
  pageSize?: number;
}

export interface SalesWarehouse {
  id: string;
  code: string;
  name: string;
  isDefault: boolean;
}

export interface SalesEmailResult {
  id: string;
  recipientMasked: string;
  requestedAt: string;
}

export interface CollectInvoiceResult {
  receipt: Receipt;
  invoiceId: string;
  replayed: boolean;
}
