import { HttpClient, HttpResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE_URL } from './api-config';
import { queryParams } from './http-params';

/** Fiscal layer (TSE / KassenSichV, ADR-0012). Kept in its own file so POS/admin model files stay small to merge. */
export type FiscalMode = 'Disabled' | 'SimulatedTse' | 'FiskalyCloudTse';

/** Ok = real TSE · Test = simulated / sandbox · Outage = TSE failed · Disabled = no TSE · NotConfigured · Refused (not allowed here). */
export type FiscalState = 'Ok' | 'Test' | 'Outage' | 'Disabled' | 'NotConfigured' | 'Refused';

export interface RegisterFiscalStatus {
  registerId: string;
  mode: FiscalMode | null;
  state: FiscalState;
  isTest: boolean;
  tseSerialNumber: string | null;
  clientId: string;
  outageSince: string | null;
  outageReason: string | null;
  refusalCode: string | null;
  lastSignatureAt: string | null;
}

export interface FiscalConfig {
  mode: FiscalMode | null;
  latencyMs: number | null;
  failureRatePercent: number | null;
  forcedOutage: boolean | null;
  algorithm: string | null;
  clientId: string | null;
  fiskalyTssId: string | null;
  fiskalyEnvironment: 'Sandbox' | 'Production' | null;
  fiskalyCredentialReference: string | null;
  hasStoredFiskalyCredentials: boolean;
  updatedAt: string;
}

/** Write model; fiskaly credentials are write-only (never returned). */
export interface FiscalConfigRequest {
  mode: FiscalMode | null;
  latencyMs?: number | null;
  failureRatePercent?: number | null;
  forcedOutage?: boolean | null;
  algorithm?: string | null;
  clientId?: string | null;
  fiskalyTssId?: string | null;
  fiskalyEnvironment?: string | null;
  fiskalyCredentialReference?: string | null;
  fiskalyApiKey?: string | null;
  fiskalyApiSecret?: string | null;
  clearFiskalyCredentials?: boolean;
}

export interface TseSignature {
  transactionNumber: number;
  signatureCounter: number | null;
  startedAt: string;
  finishedAt: string | null;
  signature: string | null;
  clientId: string;
  processType: string | null;
}

export interface SimulatedTseInfo {
  id: string;
  serialNumber: string;
  publicKey: string;
  signatureAlgorithm: string;
  timeFormat: string;
  transactionCounter: number;
  signatureCounter: number;
  createdAt: string;
  lastTransaction: TseSignature | null;
}

export interface FiscalOutage {
  id: string;
  registerId: string;
  registerName: string | null;
  sessionId: string | null;
  mode: FiscalMode;
  startedAt: string;
  endedAt: string | null;
  reason: string;
}

export interface RegisterFiscal {
  registerId: string;
  name: string;
  isActive: boolean;
  mode: FiscalMode | null;
  modeSource: 'Register' | 'Organization' | 'Default' | 'None';
  clientId: string;
  forcedOutage: boolean;
  latencyMs: number;
  failureRatePercent: number;
  openOutage: boolean;
  override: FiscalConfig | null;
}

export interface OrganizationFiscal {
  organizationId: string;
  name: string;
  mode: FiscalMode | null;
  modeSource: string;
  defaultMode: FiscalMode | null;
  configuration: FiscalConfig | null;
  registers: RegisterFiscal[];
  simulatedTse: SimulatedTseInfo | null;
  outages: FiscalOutage[];
  productionEnvironment: boolean;
  simulatedTseAllowed: boolean;
  algorithms: string[];
}

export interface OrganizationFiscalSummary {
  organizationId: string;
  name: string;
  city: string;
  isVerified: boolean;
  mode: FiscalMode | null;
  modeSource: string;
  registerCount: number;
  openOutages: number;
  tseSerialNumber: string | null;
}

export type FiskalyClientState = 'Registered' | 'NotRegistered' | 'Deregistered' | 'InvalidClientId';

export interface FiskalyClientCheck {
  registerId: string;
  name: string;
  clientId: string;
  state: FiskalyClientState;
}

/** "Verbindung testen" (read-only, nothing is signed). errorCode is set when status is Failed (e.g. fiskaly.credentialsRejected). */
export interface FiskalyConnectionTest {
  status: 'Ok' | 'Warning' | 'Failed';
  environment: 'TEST' | 'LIVE' | null;
  configuredEnvironment: 'Sandbox' | 'Production' | null;
  environmentMismatch: boolean;
  tssId: string | null;
  tssState: string | null;
  tssSerialNumber: string | null;
  clients: FiskalyClientCheck[];
  errorCode: string | null;
  detail: string | null;
  checkedAt: string;
}

@Injectable({ providedIn: 'root' })
export class FiscalApi {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE_URL);

  status(registerId: string): Observable<RegisterFiscalStatus> {
    return this.http.get<RegisterFiscalStatus>(`${this.base}/api/pos/registers/${registerId}/fiscal`);
  }

  /** DSFinV-K ZIP of the register's closings whose business day is in [from, to] (yyyy-MM-dd). */
  dsfinvk(registerId: string, from: string, to: string): Observable<HttpResponse<Blob>> {
    return this.http.get(`${this.base}/api/pos/exports/dsfinvk`, {
      params: queryParams({ registerId, from, to }),
      responseType: 'blob',
      observe: 'response',
    });
  }
}

@Injectable({ providedIn: 'root' })
export class AdminFiscalApi {
  private readonly http = inject(HttpClient);
  private readonly url = `${inject(API_BASE_URL)}/api/admin/pos/fiscal`;

  organizations(search?: string): Observable<OrganizationFiscalSummary[]> {
    return this.http.get<OrganizationFiscalSummary[]>(this.url, { params: queryParams({ search }) });
  }

  organization(id: string): Observable<OrganizationFiscal> {
    return this.http.get<OrganizationFiscal>(`${this.url}/${id}`);
  }

  setOrganization(id: string, body: FiscalConfigRequest): Observable<OrganizationFiscal> {
    return this.http.put<OrganizationFiscal>(`${this.url}/${id}`, body);
  }

  setRegister(id: string, registerId: string, body: FiscalConfigRequest): Observable<OrganizationFiscal> {
    return this.http.put<OrganizationFiscal>(`${this.url}/${id}/registers/${registerId}`, body);
  }

  /** Authenticates with the stored fiskaly credentials and reads TSS and clients; signs nothing. */
  testFiskaly(id: string): Observable<FiskalyConnectionTest> {
    return this.http.post<FiskalyConnectionTest>(`${this.url}/${id}/fiskaly/test`, {});
  }

  /** Registers the till as fiskaly client; the TSS admin PIN is used once and not stored. */
  registerFiskalyClient(id: string, registerId: string, adminPin: string): Observable<FiskalyClientCheck> {
    return this.http.post<FiskalyClientCheck>(`${this.url}/${id}/registers/${registerId}/fiskaly-client`, { adminPin });
  }
}
