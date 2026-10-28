/**
 * Where the shared quotation/invoice components link to: the POS app (`/kasse/:registerId/angebote`, Petrol look, collect
 * at the till possible) or the back office (`/app/verkauf/...`). Created by the route pages.
 */
export interface SalesNav {
  area: 'pos' | 'office';
  /** Register of the POS page (collect at the till, approvals); null in the back office. */
  registerId: string | null;
  quotations: string[];
  invoices: string[];
  /** Till receipts view (back office only). */
  receipts?: string[];
}

export function posSalesNav(registerId: string): SalesNav {
  return { area: 'pos', registerId, quotations: ['/kasse', registerId, 'angebote'], invoices: ['/kasse', registerId, 'rechnungen'] };
}

export const OFFICE_SALES_NAV: SalesNav = {
  area: 'office',
  registerId: null,
  quotations: ['/app/verkauf/angebote'],
  invoices: ['/app/verkauf/rechnungen'],
  receipts: ['/app/verkauf/kassenbelege'],
};

export function receiptLink(id: string): string[] {
  return ['/kasse/belege', id];
}

/** Saves a blob as a file (PDF download). */
export function saveBlob(doc: Document, blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const a = doc.createElement('a');
  a.href = url;
  a.download = fileName;
  doc.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 60_000);
}

/** Opens a PDF blob in a new tab for printing (the browser's PDF viewer prints); falls back to a download. */
export function openBlob(doc: Document, blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const win = doc.defaultView?.open(url, '_blank');
  if (!win) saveBlob(doc, blob, fileName);
  setTimeout(() => URL.revokeObjectURL(url), 120_000);
}
