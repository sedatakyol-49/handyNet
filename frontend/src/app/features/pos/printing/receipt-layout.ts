import { ReceiptDocument } from '../../../core/api/models';
import { formatMoney, formatSignedMoney } from '../../../core/format/money';
import { hasTseBlock, shortSignature } from '../fiscal.logic';
import { Align, Codepage, columns, toPrintable, wrap } from './escpos';
import { receiptCardLines } from '../terminals/card-flow';

/**
 * Printer-independent receipt layout (pure): the stored receipt document (unchanged fiscal data) as lines that already fit
 * the paper (chars per line), plus rules, feeds and the QR code. Rendered to ESC/POS bytes, ePOS-Print XML, Star WebPRNT
 * XML or HTML by printer-render.ts. The content follows the on-screen receipt (receipt-view.ts).
 */

export type PrintItem =
  | { kind: 'text'; text: string; align: Align; bold?: boolean; /** 2 = double width and height */ size?: 1 | 2 }
  | { kind: 'rule' }
  | { kind: 'feed'; lines: number }
  | { kind: 'qr'; data: string };

export interface PrintLayout {
  width: number;
  codepage: Codepage;
  items: PrintItem[];
}

export type Translate = (key: string, params?: Record<string, string | number | null | undefined>) => string;

export interface ReceiptLayoutOptions {
  width: number;
  codepage: Codepage;
  t: Translate;
  /** BCP 47 locale for dates (the thermal receipt is printed in German: de-DE). */
  locale: string;
  cashier?: string | null;
  /** Copy number of a recorded reprint ("Kopie n"). */
  copy?: number | null;
  headerLines?: string[];
  footerLines?: string[];
  /** Print the TSE QR code (when the receipt has one). */
  qr?: boolean;
}

/** Collects items; wraps text to the paper width (half of it for double size). */
export class LayoutBuilder {
  readonly items: PrintItem[] = [];

  constructor(
    readonly width: number,
    readonly codepage: Codepage,
  ) {}

  private p(text: string | number | null | undefined): string {
    return toPrintable(String(text ?? ''), this.codepage);
  }

  text(text: string | number | null | undefined, opts: { align?: Align; bold?: boolean; size?: 1 | 2 } = {}): this {
    const width = opts.size === 2 ? Math.floor(this.width / 2) : this.width;
    for (const line of wrap(this.p(text), width)) this.items.push({ kind: 'text', text: line, align: opts.align ?? 'left', bold: opts.bold, size: opts.size });
    return this;
  }

  /** Label left, value right (value right-aligned on the label's last line). */
  pair(label: string, value: string | number | null | undefined, opts: { bold?: boolean; size?: 1 | 2 } = {}): this {
    const width = opts.size === 2 ? Math.floor(this.width / 2) : this.width;
    for (const line of columns(this.p(label), this.p(value), width)) this.items.push({ kind: 'text', text: line, align: 'left', bold: opts.bold, size: opts.size });
    return this;
  }

  rule(): this {
    this.items.push({ kind: 'rule' });
    return this;
  }

  feed(lines = 1): this {
    this.items.push({ kind: 'feed', lines });
    return this;
  }

  qr(data: string): this {
    this.items.push({ kind: 'qr', data });
    return this;
  }

  build(): PrintLayout {
    return { width: this.width, codepage: this.codepage, items: this.items };
  }
}

export function receiptLayout(d: ReceiptDocument, o: ReceiptLayoutOptions): PrintLayout {
  const b = new LayoutBuilder(o.width, o.codepage);
  const t = o.t;
  const money = (minor: number) => formatMoney(minor, d.currency);
  const date = (iso: string | null | undefined) => {
    if (!iso) return '';
    const v = new Date(iso);
    return Number.isNaN(v.getTime()) ? iso : new Intl.DateTimeFormat(o.locale, { dateStyle: 'medium', timeStyle: 'short' }).format(v);
  };

  if (o.copy) b.text(t('pos.receipt.copy', { sequence: o.copy }).toUpperCase(), { align: 'center', bold: true, size: 2 }).feed();
  if (d.fiscal.status !== 'Signed') b.text(d.fiscal.notice || t('pos.receipt.notSigned'), { align: 'center', bold: true }).feed();

  for (const l of o.headerLines ?? []) b.text(l, { align: 'center' });
  b.text(d.seller.name, { align: 'center', bold: true });
  if (d.seller.legalName && d.seller.legalName !== d.seller.name) b.text(d.seller.legalName, { align: 'center' });
  b.text(d.seller.street, { align: 'center' });
  b.text(`${d.seller.postalCode} ${d.seller.city}`.trim(), { align: 'center' });
  if (d.seller.vatId) b.text(`${t('pos.receipt.vatId')}: ${d.seller.vatId}`, { align: 'center' });
  b.feed();

  b.text(d.title, { align: 'center', bold: true });
  b.pair(t('pos.receipt.number'), d.receiptNumber, { bold: true });
  if (d.referencedReceiptNumber) b.pair(t('pos.receipt.reference'), d.referencedReceiptNumber);
  b.pair(t('pos.receipt.register'), d.registerName);
  if (o.cashier) b.pair(t('pos.receipt.cashier'), o.cashier);
  b.pair(t('pos.receipt.date'), date(d.issuedAt));

  if (d.customer) {
    const c = d.customer;
    b.rule().text(t('pos.receipt.customer'), { bold: true }).text(c.name);
    if (c.company) b.text(c.company);
    if (c.street) b.text(c.street);
    if (c.postalCode || c.city) b.text(`${c.postalCode ?? ''} ${c.city ?? ''}`.trim());
    if (c.vatId) b.text(`${t('pos.receipt.vatId')}: ${c.vatId}`);
  }

  b.rule();
  for (const l of d.lines) {
    b.text(l.description, { bold: true });
    if (l.imei) b.text(`IMEI ${l.imei}`);
    b.pair(`  ${l.quantity} x ${money(l.unitPriceMinor)}`, `${money(l.lineGrossMinor)} ${l.taxLabel}`);
    if (l.discountMinor) b.text(`  ${t('pos.receipt.discount')} ${money(-l.discountMinor)}`);
    if (l.note) b.text(`  ${l.note}`);
  }
  b.rule();
  b.pair(t('pos.receipt.total'), money(d.totalGrossMinor), { bold: true, size: 2 });

  if (d.payments.length) {
    for (const p of d.payments) {
      const method = t(`posPaymentMethod.${p.method}`);
      if (p.tenderedMinor !== null && p.tenderedMinor !== p.amountMinor) b.pair(t('pos.receipt.tendered', { method }), money(p.tenderedMinor));
      else b.pair(method, money(p.amountMinor));
      for (const line of receiptCardLines(p.card, t)) b.text(line);
    }
    if (d.roundingMinor) b.pair(t('pos.rounding.label'), formatSignedMoney(d.roundingMinor, d.currency));
    if (d.changeMinor) b.pair(t('pos.receipt.change'), money(d.changeMinor), { bold: true });
  }

  if (d.vat.length) {
    b.rule().text(t('pos.receipt.vatBreakdown'), { bold: true });
    for (const v of d.vat) {
      b.text(v.label);
      b.pair(`  ${t('common.net')}`, money(v.netMinor));
      b.pair(`  ${t('pos.receipt.vat')}`, money(v.vatMinor));
      b.pair(`  ${t('common.gross')}`, money(v.grossMinor));
    }
  }

  const notes = d.notes.filter((n) => n !== d.fiscal.notice);
  if (notes.length) {
    b.rule();
    for (const n of notes) b.text(n);
  }

  const f = d.fiscal;
  if (hasTseBlock(f)) {
    b.rule().text(t('pos.tse.blockTitle'), { bold: true });
    if (f.isTest) b.text(t('pos.tse.testLabel'), { align: 'center', bold: true });
    const kv = (label: string, value: string | number | null | undefined) => {
      if (value === null || value === undefined || value === '') return;
      b.text(`${label}: ${value}`);
    };
    kv(t('pos.receipt.tseSerial'), f.tseSerialNumber);
    kv(t('pos.tse.clientId'), f.clientId);
    kv(t('pos.receipt.tseTransaction'), f.transactionNumber);
    kv(t('pos.receipt.tseCounter'), f.signatureCounter);
    kv(t('pos.receipt.tseStart'), date(f.startedAt));
    kv(t('pos.receipt.tseEnd'), date(f.finishedAt));
    kv(t('pos.tse.algorithm'), f.signatureAlgorithm);
    kv(t('pos.receipt.tseSignature'), shortSignature(f.signature));
    if (f.status === 'Failed') b.text(t('pos.tse.outageMarker'), { bold: true });
    if (f.qrPayload && o.qr !== false) b.feed().qr(f.qrPayload);
  }

  if (o.footerLines?.length) {
    b.rule();
    for (const l of o.footerLines) b.text(l, { align: 'center' });
  }
  return b.build();
}

export interface TestPageOptions {
  width: number;
  codepage: Codepage;
  t: Translate;
  profileName: string;
  connectionLabel: string;
  paperWidthMm: number;
  printedAt: string;
  qr: boolean;
}

/** Test page: ruler for the chars-per-line setting, umlauts and €, bold and double size, optional QR. */
export function testPageLayout(o: TestPageOptions): PrintLayout {
  const b = new LayoutBuilder(o.width, o.codepage);
  const t = o.t;
  b.text(t('pos.printers.testPage.title'), { align: 'center', bold: true, size: 2 }).feed();
  b.pair(t('pos.printers.testPage.profile'), o.profileName);
  b.pair(t('pos.printers.testPage.connection'), o.connectionLabel);
  b.pair(t('pos.printers.testPage.paper'), `${o.paperWidthMm} mm / ${o.width}`);
  b.pair(t('pos.printers.testPage.time'), o.printedAt);
  b.rule();
  b.text(t('pos.printers.testPage.ruler'));
  b.text(Array.from({ length: o.width }, (_, i) => String((i + 1) % 10)).join(''));
  b.rule();
  b.text('ÄÖÜ äöü ß € § é');
  b.text(t('pos.printers.testPage.bold'), { bold: true });
  b.text(t('pos.printers.testPage.large'), { size: 2 });
  b.text(t('pos.printers.testPage.center'), { align: 'center' });
  b.text(t('pos.printers.testPage.right'), { align: 'right' });
  if (o.qr) b.feed().qr('SEHA TEST');
  b.rule();
  b.text(t('pos.printers.testPage.ok'), { align: 'center' });
  return b.build();
}
