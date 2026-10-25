import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE_URL } from '../../../core/api/api-config';
import { Codepage } from './escpos';

/** How the browser reaches the printer (ADR-0018). */
export type PrinterConnection = 'Browser' | 'EpsonEpos' | 'StarWebPrnt' | 'EscPosBridge' | 'WebUsb' | 'WebSerial';

export const PRINTER_CONNECTIONS: PrinterConnection[] = ['Browser', 'EpsonEpos', 'StarWebPrnt', 'EscPosBridge', 'WebUsb', 'WebSerial'];

export interface PrinterProfileInput {
  name: string;
  connection: PrinterConnection;
  paperWidthMm: number;
  charsPerLine: number;
  autoPrint: boolean;
  openDrawerOnCash: boolean;
  copies: number;
  cutPaper: boolean;
  printQr: boolean;
  codepage: Codepage;
  host: string | null;
  port: number | null;
  useHttps: boolean;
  eposDeviceId: string | null;
  bridgeUrl: string | null;
  usbVendorId: number | null;
  usbProductId: number | null;
  baudRate: number | null;
  isDefault?: boolean | null;
  version?: number | null;
}

export interface PrinterProfile extends PrinterProfileInput {
  id: string;
  registerId: string;
  isDefault: boolean;
  createdAt: string;
  updatedAt: string;
  version: number;
}

/** `/api/pos/.../printers`: read with pos.sell, write with pos.manage. */
@Injectable({ providedIn: 'root' })
export class PrinterApi {
  private readonly http = inject(HttpClient);
  private readonly url = `${inject(API_BASE_URL)}/api/pos`;

  list(registerId: string): Observable<PrinterProfile[]> {
    return this.http.get<PrinterProfile[]>(`${this.url}/registers/${registerId}/printers`);
  }

  create(registerId: string, body: PrinterProfileInput): Observable<PrinterProfile> {
    return this.http.post<PrinterProfile>(`${this.url}/registers/${registerId}/printers`, body);
  }

  update(id: string, body: PrinterProfileInput): Observable<PrinterProfile> {
    return this.http.put<PrinterProfile>(`${this.url}/printers/${id}`, body);
  }

  setDefault(id: string): Observable<PrinterProfile> {
    return this.http.post<PrinterProfile>(`${this.url}/printers/${id}/default`, {});
  }

  delete(id: string): Observable<void> {
    return this.http.delete<void>(`${this.url}/printers/${id}`);
  }
}
