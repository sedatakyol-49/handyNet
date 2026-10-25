import { PrinterProfile } from './printer-api';
import { parseEposResponse, parseStarResponse } from './printer-render';

/**
 * Transports from the browser to the printer (ADR-0018). The SEHA server is never involved: the till's browser talks to the
 * printer (ePOS-Print, WebPRNT), to a local bridge (raw TCP 9100) or to a USB/serial device.
 */

export type PrintErrorCode =
  | 'unsupported' // the browser lacks WebUSB/WebSerial (only Chromium-based browsers have them)
  | 'insecureContext' // WebUSB/WebSerial need https or localhost
  | 'mixedContent' // https page → http printer: the browser blocks the request
  | 'unreachable' // network error, CORS refused, printer off or wrong address
  | 'timeout'
  | 'printer' // the printer answered with an error (cover open, paper out, …)
  | 'bridge' // the bridge answered with an error
  | 'noDevice' // no paired USB/serial device on this computer
  | 'deviceBusy' // the device is used by another program or the operating system's printer driver
  | 'cancelled'; // the user closed the device chooser

export class PrintError extends Error {
  constructor(
    readonly code: PrintErrorCode,
    readonly detail: string | null = null,
  ) {
    super(detail ? `${code}: ${detail}` : code);
  }
}

export const NETWORK_TIMEOUT_MS = 15_000;

/** Host with optional port; IPv6 addresses get brackets. */
export function hostPort(host: string, port: number | null | undefined): string {
  const h = host.includes(':') && !host.startsWith('[') ? `[${host}]` : host;
  return port ? `${h}:${port}` : h;
}

export function eposUrl(p: Pick<PrinterProfile, 'host' | 'port' | 'useHttps' | 'eposDeviceId'>): string {
  const devid = encodeURIComponent(p.eposDeviceId || 'local_printer');
  return `${p.useHttps ? 'https' : 'http'}://${hostPort(p.host ?? '', p.port)}/cgi-bin/epos/service.cgi?devid=${devid}&timeout=10000`;
}

export function starUrl(p: Pick<PrinterProfile, 'host' | 'port' | 'useHttps'>): string {
  return `${p.useHttps ? 'https' : 'http'}://${hostPort(p.host ?? '', p.port)}/StarWebPRNT/SendMessage`;
}

/**
 * Would the browser block this request as mixed content? An https page may not call http URLs — except loopback
 * (localhost, 127.0.0.1, [::1]), which browsers treat as potentially trustworthy.
 */
export function isMixedContent(pageProtocol: string, target: string): boolean {
  if (pageProtocol !== 'https:') return false;
  try {
    const u = new URL(target);
    if (u.protocol !== 'http:') return false;
    return !['localhost', '127.0.0.1', '[::1]'].includes(u.hostname) && !u.hostname.endsWith('.localhost');
  } catch {
    return false;
  }
}

async function post(url: string, body: BodyInit, headers: Record<string, string>, kind: 'printer' | 'bridge'): Promise<string> {
  if (typeof location !== 'undefined' && isMixedContent(location.protocol, url)) throw new PrintError('mixedContent', url);
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), NETWORK_TIMEOUT_MS);
  let response: Response;
  try {
    response = await fetch(url, { method: 'POST', body, headers, mode: 'cors', cache: 'no-store', credentials: 'omit', signal: controller.signal });
  } catch (e) {
    throw controller.signal.aborted ? new PrintError('timeout', url) : new PrintError('unreachable', url);
  } finally {
    clearTimeout(timer);
  }
  const text = await response.text().catch(() => '');
  if (!response.ok) throw new PrintError(kind, `HTTP ${response.status}${text ? ` – ${text.slice(0, 200)}` : ''}`);
  return text;
}

export async function sendEpos(profile: PrinterProfile, xml: string): Promise<void> {
  const text = await post(eposUrl(profile), xml, {
    'Content-Type': 'text/xml; charset=utf-8',
    'If-Modified-Since': 'Thu, 01 Jan 1970 00:00:00 GMT',
    SOAPAction: '""',
  }, 'printer');
  const r = parseEposResponse(text);
  if (!r.ok) throw new PrintError('printer', r.code ?? 'ePOS');
}

export async function sendStar(profile: PrinterProfile, envelope: string): Promise<void> {
  const text = await post(starUrl(profile), envelope, { 'Content-Type': 'text/xml; charset=UTF-8' }, 'printer');
  const r = parseStarResponse(text);
  if (!r.ok) throw new PrintError('printer', r.code ?? 'WebPRNT');
}

/** SEHA Print Bridge contract (ADR-0018): POST raw ESC/POS bytes, target printer in X-Printer-Host / X-Printer-Port. */
export async function sendBridge(profile: PrinterProfile, bytes: Uint8Array): Promise<void> {
  const text = await post(profile.bridgeUrl ?? '', new Blob([bytes as BlobPart], { type: 'application/octet-stream' }), {
    'Content-Type': 'application/octet-stream',
    'X-Printer-Host': profile.host ?? '',
    'X-Printer-Port': String(profile.port ?? 9100),
  }, 'bridge');
  try {
    const r = JSON.parse(text) as { ok?: boolean; error?: string };
    if (r.ok === false) throw new PrintError('bridge', r.error ?? null);
  } catch (e) {
    if (e instanceof PrintError) throw e;
  }
}

// ------------------------------------------------------------------------------------------------ WebUSB / WebSerial
// Minimal typings (WebUSB/WebSerial are not in TypeScript's DOM library).

interface UsbEndpoint {
  endpointNumber: number;
  direction: 'in' | 'out';
  type: 'bulk' | 'interrupt' | 'isochronous';
}
interface UsbAlternate {
  alternateSetting: number;
  interfaceClass: number;
  endpoints: UsbEndpoint[];
}
interface UsbInterface {
  interfaceNumber: number;
  alternates: UsbAlternate[];
}
interface UsbDevice {
  vendorId: number;
  productId: number;
  productName?: string;
  opened: boolean;
  configuration: { interfaces: UsbInterface[] } | null;
  open(): Promise<void>;
  close(): Promise<void>;
  selectConfiguration(n: number): Promise<void>;
  claimInterface(n: number): Promise<void>;
  releaseInterface(n: number): Promise<void>;
  selectAlternateInterface(n: number, alt: number): Promise<void>;
  transferOut(endpoint: number, data: BufferSource): Promise<{ status: string }>;
}
interface Usb {
  getDevices(): Promise<UsbDevice[]>;
  requestDevice(o: { filters: { vendorId?: number; productId?: number }[] }): Promise<UsbDevice>;
}
interface SerialPortLike {
  getInfo(): { usbVendorId?: number; usbProductId?: number };
  open(o: { baudRate: number }): Promise<void>;
  close(): Promise<void>;
  writable: WritableStream<Uint8Array> | null;
}
interface Serial {
  getPorts(): Promise<SerialPortLike[]>;
  requestPort(o?: { filters: { usbVendorId?: number; usbProductId?: number }[] }): Promise<SerialPortLike>;
}

function nav(): { usb?: Usb; serial?: Serial } {
  return typeof navigator === 'undefined' ? {} : (navigator as unknown as { usb?: Usb; serial?: Serial });
}

export function webUsbSupported(): boolean {
  return !!nav().usb;
}

export function webSerialSupported(): boolean {
  return !!nav().serial;
}

function requireApi<T>(api: T | undefined): T {
  if (api) return api;
  if (typeof isSecureContext !== 'undefined' && !isSecureContext) throw new PrintError('insecureContext');
  throw new PrintError('unsupported');
}

export interface PairedIds {
  usbVendorId: number | null;
  usbProductId: number | null;
  label: string | null;
}

function matches(vendor: number | undefined, product: number | undefined, p: Pick<PrinterProfile, 'usbVendorId' | 'usbProductId'>): boolean {
  return (p.usbVendorId === null || p.usbVendorId === vendor) && (p.usbProductId === null || p.usbProductId === product);
}

/** Opens the browser's device chooser (must run in a click handler) and returns the ids to remember. */
export async function pairUsb(): Promise<PairedIds> {
  const usb = requireApi(nav().usb);
  try {
    const d = await usb.requestDevice({ filters: [] });
    return { usbVendorId: d.vendorId, usbProductId: d.productId, label: d.productName ?? null };
  } catch (e) {
    throw new PrintError((e as DOMException)?.name === 'NotFoundError' ? 'cancelled' : 'unsupported', (e as Error)?.message ?? null);
  }
}

export async function pairSerial(): Promise<PairedIds> {
  const serial = requireApi(nav().serial);
  try {
    const port = await serial.requestPort({ filters: [] });
    const info = port.getInfo();
    return { usbVendorId: info.usbVendorId ?? null, usbProductId: info.usbProductId ?? null, label: null };
  } catch (e) {
    throw new PrintError((e as DOMException)?.name === 'NotFoundError' ? 'cancelled' : 'unsupported', (e as Error)?.message ?? null);
  }
}

/** Is a device for this profile paired in this browser (no prompt)? */
export async function hasPairedDevice(profile: PrinterProfile): Promise<boolean> {
  const n = nav();
  try {
    if (profile.connection === 'WebUsb' && n.usb) return (await n.usb.getDevices()).some((d) => matches(d.vendorId, d.productId, profile));
    if (profile.connection === 'WebSerial' && n.serial)
      return (await n.serial.getPorts()).some((p) => matches(p.getInfo().usbVendorId, p.getInfo().usbProductId, profile));
  } catch {
    return false;
  }
  return false;
}

const USB_CHUNK = 16 * 1024;

export async function sendUsb(profile: PrinterProfile, bytes: Uint8Array): Promise<void> {
  const usb = requireApi(nav().usb);
  const device = (await usb.getDevices()).find((d) => matches(d.vendorId, d.productId, profile));
  if (!device) throw new PrintError('noDevice');
  let claimed: number | null = null;
  try {
    if (!device.opened) await device.open();
    if (device.configuration === null) await device.selectConfiguration(1);
    const target = findBulkOut(device);
    if (!target) throw new PrintError('printer', 'no bulk OUT endpoint');
    try {
      await device.claimInterface(target.interfaceNumber);
    } catch (e) {
      throw new PrintError('deviceBusy', (e as Error)?.message ?? null);
    }
    claimed = target.interfaceNumber;
    if (target.alternate !== 0) await device.selectAlternateInterface(target.interfaceNumber, target.alternate);
    for (let i = 0; i < bytes.length; i += USB_CHUNK) {
      const r = await device.transferOut(target.endpoint, bytes.slice(i, i + USB_CHUNK));
      if (r.status !== 'ok') throw new PrintError('printer', `USB ${r.status}`);
    }
  } catch (e) {
    if (e instanceof PrintError) throw e;
    throw new PrintError('deviceBusy', (e as Error)?.message ?? null);
  } finally {
    if (claimed !== null) await device.releaseInterface(claimed).catch(() => undefined);
    await device.close().catch(() => undefined);
  }
}

/** The first bulk OUT endpoint, preferring the USB printer class (7). */
export function findBulkOut(device: Pick<UsbDevice, 'configuration'>): { interfaceNumber: number; alternate: number; endpoint: number } | null {
  const candidates: { interfaceNumber: number; alternate: number; endpoint: number; printerClass: boolean }[] = [];
  for (const iface of device.configuration?.interfaces ?? []) {
    for (const alt of iface.alternates) {
      const ep = alt.endpoints.find((e) => e.direction === 'out' && e.type === 'bulk');
      if (ep) candidates.push({ interfaceNumber: iface.interfaceNumber, alternate: alt.alternateSetting, endpoint: ep.endpointNumber, printerClass: alt.interfaceClass === 7 });
    }
  }
  const best = candidates.find((c) => c.printerClass) ?? candidates[0];
  return best ? { interfaceNumber: best.interfaceNumber, alternate: best.alternate, endpoint: best.endpoint } : null;
}

export async function sendSerial(profile: PrinterProfile, bytes: Uint8Array): Promise<void> {
  const serial = requireApi(nav().serial);
  const ports = await serial.getPorts();
  const port = ports.find((p) => matches(p.getInfo().usbVendorId, p.getInfo().usbProductId, profile)) ?? (ports.length === 1 && profile.usbVendorId === null ? ports[0] : undefined);
  if (!port) throw new PrintError('noDevice');
  try {
    await port.open({ baudRate: profile.baudRate ?? 9600 });
  } catch (e) {
    throw new PrintError('deviceBusy', (e as Error)?.message ?? null);
  }
  try {
    const writer = port.writable?.getWriter();
    if (!writer) throw new PrintError('deviceBusy');
    try {
      await writer.write(bytes);
    } finally {
      writer.releaseLock();
    }
  } finally {
    await port.close().catch(() => undefined);
  }
}
