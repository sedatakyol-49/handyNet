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
}

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
export type ReceiptKind = 'Sale' | 'Storno' | 'Return';
export type TaxTreatment = 'Standard19' | 'Margin25a';
export type CashMovementKind = 'Deposit' | 'Withdrawal';

export interface PosRegister {
  id: string;
  name: string;
  warehouseId: string;
  isActive: boolean;
  nextReceiptNumber: number;
  openSessionId: string | null;
  createdAt: string;
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
  productId: string;
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
