import { DOCUMENT } from '@angular/common';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Receipt } from '../../../core/api/models';
import { CompanyBrandingStore } from '../../../core/company/company-branding.store';
import { I18nService, flatten } from '../../../core/i18n/i18n.service';
import de from '../../../core/i18n/locales/de.json';
import { StorageService } from '../../../core/storage.service';
import { ToastService } from '../../../core/ui-state/toast.service';
import { receiptLines } from '../../company/company.logic';
import { PrinterApi, PrinterProfile } from './printer-api';
import { RenderOptions, toEposXml, toEscPos, toHtml, toStarEnvelope, toStarRequest } from './printer-render';
import { PrintError, PrintErrorCode, PairedIds, pairSerial, pairUsb, sendBridge, sendEpos, sendSerial, sendStar, sendUsb } from './printer-transports';
import { PrintLayout, Translate, receiptLayout, testPageLayout } from './receipt-layout';

/** Device choice stored per register: '' = the register's default profile, 'browser' = always the print dialog, else a profile id. */
export const DEVICE_BROWSER = 'browser';
const deviceKey = (registerId: string) => `hn.pos.printer.${registerId}`;

export type PrintOutcome = { ok: true; via: PrinterProfile['connection'] } | { ok: false; code: PrintErrorCode; detail: string | null; fellBack: boolean };

/**
 * Thermal receipts are printed with German labels: the code pages of ESC/POS printers (CP858/PC437) have no Cyrillic or
 * Arabic glyphs, and the receipt itself (title, notes, fiscal texts) is stored in German.
 */
const DE = flatten(de as never);
export const germanT: Translate = (key, params) => {
  let text = DE[key] ?? key;
  for (const [name, value] of Object.entries(params ?? {})) text = text.split(`{${name}}`).join(String(value ?? ''));
  return text;
};

/** Picks the profile a device uses: its own choice if it still exists, else the register's default. Null = print dialog. */
export function effectiveProfile(profiles: PrinterProfile[], choice: string | null): PrinterProfile | null {
  if (choice === DEVICE_BROWSER) return null;
  return (choice && profiles.find((p) => p.id === choice)) || profiles.find((p) => p.isDefault) || null;
}

/** Does the receipt take cash (so the drawer should open)? */
export function takesCash(receipt: Pick<Receipt, 'payments'>): boolean {
  return receipt.payments.some((p) => p.method === 'Cash' && p.amountMinor !== 0);
}

@Injectable({ providedIn: 'root' })
export class PrinterService {
  private readonly api = inject(PrinterApi);
  private readonly storage = inject(StorageService);
  private readonly toast = inject(ToastService);
  private readonly i18n = inject(I18nService);
  private readonly branding = inject(CompanyBrandingStore);
  private readonly document = inject(DOCUMENT);
  private readonly cache = new Map<string, Promise<PrinterProfile[]>>();

  /** Last browser-print fallback (shown on the printer page). */
  readonly lastFallback = signal<{ at: number; code: PrintErrorCode } | null>(null);

  profiles(registerId: string, reload = false): Promise<PrinterProfile[]> {
    let p = reload ? undefined : this.cache.get(registerId);
    if (!p) {
      p = firstValueFrom(this.api.list(registerId)).catch(() => {
        this.cache.delete(registerId);
        return [] as PrinterProfile[];
      });
      this.cache.set(registerId, p);
    }
    return p;
  }

  invalidate(registerId: string): void {
    this.cache.delete(registerId);
  }

  deviceChoice(registerId: string): string {
    return this.storage.get(deviceKey(registerId)) ?? '';
  }

  setDeviceChoice(registerId: string, choice: string): void {
    this.storage.set(deviceKey(registerId), choice || null);
  }

  async effective(registerId: string): Promise<PrinterProfile | null> {
    return effectiveProfile(await this.profiles(registerId), this.deviceChoice(registerId));
  }

  /** After a sale: auto-print and/or open the drawer for cash, as the device's profile says. */
  async afterSale(receipt: Receipt): Promise<void> {
    const profile = await this.effective(receipt.registerId);
    if (!profile) return;
    const drawer = profile.openDrawerOnCash && takesCash(receipt);
    if (profile.autoPrint) {
      await this.printReceipt(receipt, { profile, drawer });
    } else if (drawer && profile.connection !== 'Browser') {
      const r = await this.send(profile, null, { ...this.renderOptions(profile), drawer: true });
      if (!r.ok) this.toast.error(this.i18n.t('pos.printers.drawerFailed', { reason: this.errorText(r.code, r.detail) }));
    }
  }

  /**
   * Prints a receipt with the device's profile. The browser connection (and the fallback after an error) prints the
   * receipt shown on the page (`hn-print-area`), so it must be rendered when this is called.
   */
  async printReceipt(receipt: Receipt, o: { copy?: number | null; profile?: PrinterProfile | null; drawer?: boolean } = {}): Promise<PrintOutcome> {
    const profile = o.profile !== undefined ? o.profile : await this.effective(receipt.registerId);
    if (!profile || profile.connection === 'Browser') {
      this.printPage(profile?.paperWidthMm ?? 80);
      return { ok: true, via: 'Browser' };
    }
    const layout = receiptLayout(receipt.document, {
      width: profile.charsPerLine,
      codepage: profile.codepage,
      t: germanT,
      locale: 'de-DE',
      cashier: receipt.createdByName,
      copy: o.copy ?? null,
      headerLines: receiptLines(this.branding.receiptBranding()?.receiptHeader),
      footerLines: receiptLines(this.branding.receiptBranding()?.receiptFooter),
      qr: profile.printQr,
    });
    const r = await this.send(profile, layout, { ...this.renderOptions(profile), drawer: !!o.drawer });
    if (r.ok) return r;
    this.toast.error(this.i18n.t('pos.printers.fallbackNotice', { reason: this.errorText(r.code, r.detail) }));
    this.lastFallback.set({ at: Date.now(), code: r.code });
    this.printPage(profile.paperWidthMm);
    return { ...r, fellBack: true };
  }

  /**
   * Prints a layout that is not a stored receipt (the provisional receipt of an offline sale) with the device's profile.
   * Like receipts: the browser connection and every printer error print the block shown on the page (`hn-print-area`).
   * Offline the profiles are only known if they were loaded before the connection dropped; otherwise the dialog opens.
   */
  async printOther(registerId: string, build: (profile: PrinterProfile) => PrintLayout): Promise<PrintOutcome> {
    const profile = await this.effective(registerId);
    if (!profile || profile.connection === 'Browser') {
      this.printPage(profile?.paperWidthMm ?? 80);
      return { ok: true, via: 'Browser' };
    }
    const r = await this.send(profile, build(profile), { ...this.renderOptions(profile), copies: 1 });
    if (r.ok) return r;
    this.toast.error(this.i18n.t('pos.printers.fallbackNotice', { reason: this.errorText(r.code, r.detail) }));
    this.lastFallback.set({ at: Date.now(), code: r.code });
    this.printPage(profile.paperWidthMm);
    return { ...r, fellBack: true };
  }

  /**
   * Prints a document that has no block on the page (gift card voucher slip): thermal through the device's profile, else
   * (no profile, browser connection, printer error) in a hidden frame through the print dialog.
   */
  async printLayout(registerId: string, title: string, build: (width: number, codepage: PrinterProfile['codepage']) => PrintLayout): Promise<PrintOutcome> {
    const profile = await this.effective(registerId);
    const frameProfile = { paperWidthMm: profile?.paperWidthMm ?? 80, name: title } as PrinterProfile;
    const layout = build(profile?.charsPerLine ?? 48, profile?.codepage ?? 'Cp858');
    if (!profile || profile.connection === 'Browser') {
      this.printFrame(layout, frameProfile);
      return { ok: true, via: 'Browser' };
    }
    const r = await this.send(profile, layout, { ...this.renderOptions(profile), copies: 1 });
    if (r.ok) return r;
    this.toast.error(this.i18n.t('pos.printers.fallbackNotice', { reason: this.errorText(r.code, r.detail) }));
    this.lastFallback.set({ at: Date.now(), code: r.code });
    this.printFrame(layout, frameProfile);
    return { ...r, fellBack: true };
  }

  /** Test page for a profile; on failure the same page opens in the print dialog. */
  async testPrint(profile: PrinterProfile): Promise<PrintOutcome> {
    const layout = testPageLayout({
      width: profile.charsPerLine,
      codepage: profile.codepage,
      t: germanT,
      profileName: profile.name,
      connectionLabel: germanT(`pos.printers.connection.${profile.connection}`),
      paperWidthMm: profile.paperWidthMm,
      printedAt: new Intl.DateTimeFormat('de-DE', { dateStyle: 'medium', timeStyle: 'medium' }).format(new Date()),
      qr: profile.printQr,
    });
    if (profile.connection === 'Browser') {
      this.printFrame(layout, profile);
      return { ok: true, via: 'Browser' };
    }
    const r = await this.send(profile, layout, { ...this.renderOptions(profile), copies: 1 });
    if (r.ok) return r;
    this.lastFallback.set({ at: Date.now(), code: r.code });
    this.printFrame(layout, profile);
    return { ...r, fellBack: true };
  }

  /** Device chooser for USB/serial profiles (call from a click). */
  pair(connection: PrinterProfile['connection']): Promise<PairedIds> {
    return connection === 'WebSerial' ? pairSerial() : pairUsb();
  }

  /** Understandable text for a print error (translated). */
  errorText(code: PrintErrorCode, detail: string | null = null): string {
    const text = this.i18n.t(`pos.printers.errors.${code}`);
    return code === 'printer' || code === 'bridge' ? `${text}${detail ? ` (${detail})` : ''}` : text;
  }

  private renderOptions(p: PrinterProfile): RenderOptions {
    return { copies: p.copies, cut: p.cutPaper, drawer: false, paperWidthMm: p.paperWidthMm };
  }

  private async send(profile: PrinterProfile, layout: PrintLayout | null, o: RenderOptions): Promise<PrintOutcome> {
    try {
      switch (profile.connection) {
        case 'EpsonEpos':
          await sendEpos(profile, toEposXml(layout, o));
          break;
        case 'StarWebPrnt':
          await sendStar(profile, toStarEnvelope(toStarRequest(layout, o)));
          break;
        case 'EscPosBridge':
          await sendBridge(profile, toEscPos(layout, o));
          break;
        case 'WebUsb':
          await sendUsb(profile, toEscPos(layout, o));
          break;
        case 'WebSerial':
          await sendSerial(profile, toEscPos(layout, o));
          break;
        case 'Browser':
          break;
      }
      return { ok: true, via: profile.connection };
    } catch (e) {
      const err = e instanceof PrintError ? e : new PrintError('unreachable', (e as Error)?.message ?? null);
      return { ok: false, code: err.code, detail: err.detail, fellBack: false };
    }
  }

  /** Browser print of the receipt on the page; 58 mm paper uses the narrow print layout (styles.css). */
  private printPage(paperWidthMm: number): void {
    const win = this.document.defaultView;
    if (!win) return;
    const body = this.document.body;
    const narrow = paperWidthMm < 80;
    if (narrow) body.classList.add('hn-paper-58');
    try {
      win.print();
    } finally {
      if (narrow) setTimeout(() => body.classList.remove('hn-paper-58'), 1000);
    }
  }

  /** Browser print of a layout in a hidden iframe sized to the paper width. */
  private printFrame(layout: PrintLayout, profile: PrinterProfile): void {
    const doc = this.document;
    if (!doc.defaultView) return;
    doc.querySelectorAll('iframe[data-hn-print-frame]').forEach((f) => f.remove());
    const frame = doc.createElement('iframe');
    frame.setAttribute('data-hn-print-frame', '');
    frame.setAttribute('aria-hidden', 'true');
    frame.setAttribute('tabindex', '-1');
    frame.style.cssText = 'position:fixed;inset-inline-end:0;bottom:0;width:0;height:0;border:0;visibility:hidden';
    frame.srcdoc = toHtml(layout, { paperWidthMm: profile.paperWidthMm, copies: 1 }, profile.name);
    frame.onload = () => {
      const w = frame.contentWindow;
      if (!w) return;
      w.focus();
      w.print();
      setTimeout(() => frame.remove(), 60_000);
    };
    doc.body.appendChild(frame);
  }
}
