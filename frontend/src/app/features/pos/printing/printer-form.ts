import { Codepage } from './escpos';
import { PrinterConnection, PrinterProfile, PrinterProfileInput } from './printer-api';
import { hostPort } from './printer-transports';

/** Limits mirrored from the API (PrinterProfile domain rules, ADR-0018). */
export const PRINTER_LIMITS = { name: 100, host: 253, deviceId: 64, bridgeUrl: 500, minChars: 24, maxChars: 64, maxCopies: 5 } as const;
export const BAUD_RATES = [9600, 19200, 38400, 57600, 115200];
export const DEFAULT_BRIDGE_URL = 'http://localhost:9123/print';

/** Typical characters per line in font A: 48 on 80 mm, 32 on 58 mm. */
export function defaultChars(paperWidthMm: number): number {
  return paperWidthMm >= 80 ? 48 : 32;
}

/** Form state: numbers as text (inputs). USB ids are hex text ("04B8"). */
export interface PrinterForm {
  name: string;
  connection: PrinterConnection;
  paperWidthMm: number;
  charsPerLine: string;
  autoPrint: boolean;
  openDrawerOnCash: boolean;
  copies: string;
  cutPaper: boolean;
  printQr: boolean;
  codepage: Codepage;
  host: string;
  port: string;
  useHttps: boolean;
  eposDeviceId: string;
  bridgeUrl: string;
  usbVendorId: string;
  usbProductId: string;
  baudRate: number;
  isDefault: boolean;
}

export type PrinterFormErrors = Partial<Record<keyof PrinterForm, string>>;

export function emptyPrinterForm(): PrinterForm {
  return {
    name: '', connection: 'Browser', paperWidthMm: 80, charsPerLine: '48', autoPrint: false, openDrawerOnCash: false, copies: '1', cutPaper: true,
    printQr: true, codepage: 'Cp858', host: '', port: '', useHttps: false, eposDeviceId: 'local_printer', bridgeUrl: DEFAULT_BRIDGE_URL,
    usbVendorId: '', usbProductId: '', baudRate: 9600, isDefault: false,
  };
}

export function hexId(n: number | null | undefined): string {
  return n === null || n === undefined ? '' : n.toString(16).toUpperCase().padStart(4, '0');
}

export function formFromProfile(p: PrinterProfile): PrinterForm {
  return {
    name: p.name, connection: p.connection, paperWidthMm: p.paperWidthMm, charsPerLine: String(p.charsPerLine), autoPrint: p.autoPrint,
    openDrawerOnCash: p.openDrawerOnCash, copies: String(p.copies), cutPaper: p.cutPaper, printQr: p.printQr, codepage: p.codepage, host: p.host ?? '',
    port: p.port ? String(p.port) : '', useHttps: p.useHttps, eposDeviceId: p.eposDeviceId ?? 'local_printer', bridgeUrl: p.bridgeUrl ?? DEFAULT_BRIDGE_URL,
    usbVendorId: hexId(p.usbVendorId), usbProductId: hexId(p.usbProductId), baudRate: p.baudRate ?? 9600, isDefault: p.isDefault,
  };
}

export const isNetwork = (c: PrinterConnection) => c === 'EpsonEpos' || c === 'StarWebPrnt' || c === 'EscPosBridge';
export const isUsb = (c: PrinterConnection) => c === 'WebUsb' || c === 'WebSerial';

const HOST = /^(?=.{1,253}$)[A-Za-z0-9][A-Za-z0-9-]{0,62}(?:\.[A-Za-z0-9][A-Za-z0-9-]{0,62})*$/;
const IPV6 = /^\[?[0-9A-Fa-f:.]{2,45}\]?$/;

export function validHost(host: string): boolean {
  return HOST.test(host) || (IPV6.test(host) && (host.match(/:/g)?.length ?? 0) >= 2);
}

export function validBridgeUrl(url: string): boolean {
  try {
    const u = new URL(url);
    return (u.protocol === 'http:' || u.protocol === 'https:') && !u.username && !u.password && !u.hash && url.length <= PRINTER_LIMITS.bridgeUrl;
  } catch {
    return false;
  }
}

function int(text: string): number | null {
  return /^\d{1,6}$/.test(text.trim()) ? Number(text.trim()) : null;
}

function hex(text: string): number | null | undefined {
  const t = text.trim().replace(/^0x/i, '');
  if (!t) return null;
  return /^[0-9A-Fa-f]{1,4}$/.test(t) ? parseInt(t, 16) : undefined;
}

/** Validates the form (keys of pos.printers.errors.*) and builds the API body. */
export function printerInput(f: PrinterForm, version: number | null): { input: PrinterProfileInput | null; errors: PrinterFormErrors } {
  const e: PrinterFormErrors = {};
  const name = f.name.trim();
  if (!name || name.length > PRINTER_LIMITS.name) e.name = 'pos.printers.form.errors.name';
  const chars = int(f.charsPerLine);
  if (chars === null || chars < PRINTER_LIMITS.minChars || chars > PRINTER_LIMITS.maxChars) e.charsPerLine = 'pos.printers.form.errors.chars';
  const copies = int(f.copies);
  if (copies === null || copies < 1 || copies > PRINTER_LIMITS.maxCopies) e.copies = 'pos.printers.form.errors.copies';
  const host = f.host.trim();
  let port: number | null = null;
  if (isNetwork(f.connection)) {
    if (!host || !validHost(host)) e.host = 'pos.printers.form.errors.host';
    if (f.port.trim()) {
      port = int(f.port);
      if (port === null || port < 1 || port > 65535) e.port = 'pos.printers.form.errors.port';
    }
  }
  const bridgeUrl = f.bridgeUrl.trim();
  if (f.connection === 'EscPosBridge' && !validBridgeUrl(bridgeUrl)) e.bridgeUrl = 'pos.printers.form.errors.bridgeUrl';
  const deviceId = f.eposDeviceId.trim() || 'local_printer';
  if (f.connection === 'EpsonEpos' && !/^[A-Za-z0-9_.-]{1,64}$/.test(deviceId)) e.eposDeviceId = 'pos.printers.form.errors.deviceId';
  const vendor = hex(f.usbVendorId);
  const product = hex(f.usbProductId);
  if (isUsb(f.connection) && (vendor === undefined || product === undefined)) e.usbVendorId = 'pos.printers.form.errors.usbId';
  if (Object.keys(e).length) return { input: null, errors: e };
  const network = isNetwork(f.connection);
  const usb = isUsb(f.connection);
  return {
    errors: e,
    input: {
      name, connection: f.connection, paperWidthMm: f.paperWidthMm, charsPerLine: chars!, autoPrint: f.autoPrint, openDrawerOnCash: f.openDrawerOnCash,
      copies: copies!, cutPaper: f.cutPaper, printQr: f.printQr, codepage: f.codepage,
      host: network ? host : null,
      port: network ? port : null,
      useHttps: (f.connection === 'EpsonEpos' || f.connection === 'StarWebPrnt') && f.useHttps,
      eposDeviceId: f.connection === 'EpsonEpos' ? deviceId : null,
      bridgeUrl: f.connection === 'EscPosBridge' ? bridgeUrl : null,
      usbVendorId: usb ? (vendor ?? null) : null,
      usbProductId: usb ? (product ?? null) : null,
      baudRate: f.connection === 'WebSerial' ? f.baudRate : null,
      isDefault: f.isDefault || null,
      version,
    },
  };
}

/** One-line description of where the profile prints (address, bridge or USB ids). */
export function connectionSummary(p: Pick<PrinterProfile, 'connection' | 'host' | 'port' | 'useHttps' | 'bridgeUrl' | 'usbVendorId' | 'usbProductId' | 'baudRate'>): string {
  switch (p.connection) {
    case 'EpsonEpos':
    case 'StarWebPrnt':
      return `${p.useHttps ? 'https' : 'http'}://${hostPort(p.host ?? '', p.port)}`;
    case 'EscPosBridge':
      return `${hostPort(p.host ?? '', p.port ?? 9100)} ← ${p.bridgeUrl ?? ''}`;
    case 'WebUsb':
    case 'WebSerial': {
      const ids = p.usbVendorId !== null ? `USB ${hexId(p.usbVendorId)}:${hexId(p.usbProductId)}` : '';
      return [ids, p.connection === 'WebSerial' ? `${p.baudRate ?? 9600} Bd` : ''].filter(Boolean).join(' · ');
    }
    default:
      return '';
  }
}
