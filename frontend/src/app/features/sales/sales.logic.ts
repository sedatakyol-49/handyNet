import { PriceMode, SALES_VAT_RATES, SalesCustomer, SalesLine, SalesLineRequest, SalesVat } from '../../core/api/models';
import { minorToInput, parseMoneyToMinor } from '../../core/format/money';

/**
 * Pure rules of the quotation/invoice form (mirroring the API's SalesCalculator so the preview equals what is stored):
 * integer cents, VAT half away from zero, gross mode = VAT contained in the sum per rate, net mode = VAT added to the sum
 * per rate, §25a lines count with their final price and never show VAT.
 */

export const MAX_LINES = 200;
export const MAX_QUANTITY = 100_000;
export const MAX_AMOUNT_MINOR = 100_000_000;

/** A line as edited in the form (amounts as typed; parsed on validation). */
export interface DraftLine {
  key: string;
  productId: string | null;
  imei: string | null;
  /** Stock unit id of a device line (shown only). */
  stockUnitId: string | null;
  margin25a: boolean;
  description: string;
  note: string;
  quantity: string;
  unitPrice: string;
  discount: string;
  vatRateBasisPoints: number;
  /** Shown next to product lines (available in the chosen warehouse), null when unknown. */
  available: number | null;
}

export type LineField = 'description' | 'quantity' | 'unitPrice' | 'discount';
export type LineErrors = Partial<Record<LineField, string>>;

let keySeq = 0;
export function newLineKey(): string {
  keySeq += 1;
  return `l${Date.now().toString(36)}${keySeq}`;
}

export function emptyLine(): DraftLine {
  return {
    key: newLineKey(), productId: null, imei: null, stockUnitId: null, margin25a: false, description: '', note: '', quantity: '1', unitPrice: '',
    discount: '', vatRateBasisPoints: 1900, available: null,
  };
}

export function lineFromStored(l: SalesLine): DraftLine {
  return {
    key: newLineKey(), productId: l.productId, imei: l.imei, stockUnitId: l.stockUnitId, margin25a: l.margin25a, description: l.description,
    note: l.note ?? '', quantity: String(Math.abs(l.quantity)), unitPrice: minorToInput(l.unitPriceMinor),
    discount: l.discountMinor ? minorToInput(Math.abs(l.discountMinor)) : '', vatRateBasisPoints: l.vatRateBasisPoints, available: null,
  };
}

/** Whole positive quantity; null when invalid. */
export function parseQuantity(text: string): number | null {
  const s = text.trim();
  if (!/^\d{1,6}$/.test(s)) return null;
  const n = Number(s);
  return n >= 1 && n <= MAX_QUANTITY ? n : null;
}

/** Money input: German or English decimals with at most two places (sub-cent input is refused, never rounded). */
export function parseAmount(text: string, allowEmpty = false): number | null {
  if (allowEmpty && !text.trim()) return 0;
  const v = parseMoneyToMinor(text);
  return v === null || v < 0 || v > MAX_AMOUNT_MINOR ? null : v;
}

export interface ParsedLine {
  quantity: number;
  unitPriceMinor: number;
  discountMinor: number;
  amountMinor: number;
}

export function parseLine(l: DraftLine): { value: ParsedLine | null; errors: LineErrors } {
  const errors: LineErrors = {};
  const quantity = parseQuantity(l.quantity);
  const unit = parseAmount(l.unitPrice);
  const discount = parseAmount(l.discount, true);
  if (!l.description.trim()) errors.description = 'required';
  if (quantity === null) errors.quantity = 'quantity';
  if (l.imei && quantity !== null && quantity !== 1) errors.quantity = 'deviceQuantity';
  if (unit === null) errors.unitPrice = 'amount';
  if (discount === null) errors.discount = 'amount';
  if (quantity !== null && unit !== null && discount !== null && discount > unit * quantity) errors.discount = 'discountTooHigh';
  if (Object.keys(errors).length || quantity === null || unit === null || discount === null) return { value: null, errors };
  return { value: { quantity, unitPriceMinor: unit, discountMinor: discount, amountMinor: unit * quantity - discount }, errors };
}

/** Round half away from zero for a non-negative rational a / b (integers). */
function roundDiv(a: number, b: number): number {
  const q = Math.floor(a / b);
  return (a - q * b) * 2 >= b ? q + 1 : q;
}

export function vatFromGross(grossMinor: number, rateBp: number): number {
  if (rateBp === 0) return 0;
  const sign = grossMinor < 0 ? -1 : 1;
  const g = Math.abs(grossMinor);
  return sign * (g - roundDiv(g * 10_000, 10_000 + rateBp));
}

export function vatOnNet(netMinor: number, rateBp: number): number {
  const sign = netMinor < 0 ? -1 : 1;
  return sign * roundDiv(Math.abs(netMinor) * rateBp, 10_000);
}

export interface Totals {
  vat: SalesVat[];
  marginGrossMinor: number;
  totalNetMinor: number;
  totalVatMinor: number;
  totalGrossMinor: number;
}

export function computeTotals(mode: PriceMode, lines: { amountMinor: number; vatRateBasisPoints: number; margin25a: boolean }[]): Totals {
  const byRate = new Map<number, number>();
  let margin = 0;
  for (const l of lines) {
    if (l.margin25a) margin += l.amountMinor;
    else byRate.set(l.vatRateBasisPoints, (byRate.get(l.vatRateBasisPoints) ?? 0) + l.amountMinor);
  }
  const vat = [...byRate.entries()]
    .sort((a, b) => b[0] - a[0])
    .map(([rate, amount]) => {
      if (mode === 'Gross') {
        const v = vatFromGross(amount, rate);
        return { rateBasisPoints: rate, netMinor: amount - v, vatMinor: v, grossMinor: amount };
      }
      const v = vatOnNet(amount, rate);
      return { rateBasisPoints: rate, netMinor: amount, vatMinor: v, grossMinor: amount + v };
    });
  const totalVat = vat.reduce((s, v) => s + v.vatMinor, 0);
  const gross = vat.reduce((s, v) => s + v.grossMinor, 0) + margin;
  return { vat, marginGrossMinor: margin, totalNetMinor: gross - totalVat, totalVatMinor: totalVat, totalGrossMinor: gross };
}

export function rateLabel(bp: number): string {
  return `${(bp / 100).toLocaleString('de-DE', { maximumFractionDigits: 2 })} %`;
}

export function isSalesRate(bp: number): boolean {
  return (SALES_VAT_RATES as readonly number[]).includes(bp);
}

/** API line from a parsed draft line (devices by IMEI so the API re-reads the unit and its §25a scheme). */
export function lineRequest(l: DraftLine, p: ParsedLine): SalesLineRequest {
  return {
    productId: l.productId,
    imei: l.imei,
    description: l.description.trim(),
    note: l.note.trim() || null,
    quantity: p.quantity,
    unitPriceMinor: p.unitPriceMinor,
    discountMinor: p.discountMinor || null,
    vatRateBasisPoints: l.margin25a ? 1900 : l.vatRateBasisPoints,
  };
}

/** Percent discount → cents of the line amount, truncated (like the till's discount limit arithmetic). */
export function percentToDiscount(unitPriceMinor: number, quantity: number, percent: number): number {
  if (!(percent > 0)) return 0;
  return Math.floor((Math.min(percent, 100) * unitPriceMinor * quantity) / 100);
}

/** Missing §14 fields of an invoice recipient: name and full address. */
export function missingInvoiceAddress(c: Pick<SalesCustomer, 'name' | 'street' | 'postalCode' | 'city'> | null): boolean {
  return !c || !c.name?.trim() || !c.street?.trim() || !c.postalCode?.trim() || !c.city?.trim();
}

export function customerLines(c: SalesCustomer | null): string[] {
  if (!c) return [];
  return [c.company, c.name, c.street, [c.postalCode, c.city].filter(Boolean).join(' '), c.countryCode && c.countryCode !== 'DE' ? c.countryCode : null]
    .filter((x): x is string => !!x && !!x.trim());
}

export type Tone = 'neutral' | 'info' | 'success' | 'warning' | 'danger';

export function quotationTone(status: string): Tone {
  switch (status) {
    case 'Sent':
      return 'info';
    case 'Accepted':
      return 'success';
    case 'Rejected':
      return 'danger';
    case 'Expired':
      return 'warning';
    default:
      return 'neutral';
  }
}

/** One badge per invoice row: draft, cancelled, credit note, overdue, or the payment state. */
export function invoiceBadge(i: { status: string; kind: string; cancelled: boolean; overdue: boolean; paymentState: string | null }): { key: string; tone: Tone } {
  if (i.status === 'Draft') return { key: 'Draft', tone: 'neutral' };
  if (i.status === 'Discarded') return { key: 'Discarded', tone: 'neutral' };
  if (i.kind === 'CreditNote') return { key: 'CreditNote', tone: 'info' };
  if (i.cancelled) return { key: 'Cancelled', tone: 'neutral' };
  if (i.overdue) return { key: 'Overdue', tone: 'danger' };
  if (i.paymentState === 'Paid') return { key: 'Paid', tone: 'success' };
  if (i.paymentState === 'PartiallyPaid') return { key: 'PartiallyPaid', tone: 'warning' };
  return { key: 'Open', tone: 'warning' };
}

/** Today in the browser's calendar as yyyy-mm-dd (date inputs). */
export function isoToday(now = new Date()): string {
  const m = String(now.getMonth() + 1).padStart(2, '0');
  const d = String(now.getDate()).padStart(2, '0');
  return `${now.getFullYear()}-${m}-${d}`;
}

export function addDays(iso: string, days: number): string {
  const [y, m, d] = iso.split('-').map(Number);
  const date = new Date(Date.UTC(y, m - 1, d + days));
  return date.toISOString().slice(0, 10);
}
