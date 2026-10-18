import qrcode from 'qrcode-generator';
import { FiscalConfig, FiscalConfigRequest, FiscalState } from '../../core/api/fiscal-api';
import { ReceiptFiscal } from '../../core/api/models';

/** Fiscal (TSE / KassenSichV) helpers for the receipt, the top bar and the admin form. Pure functions, unit-tested. */

/** Long Base64 signatures are shortened on the printed receipt: first and last characters around an ellipsis. */
export function shortSignature(signature: string | null | undefined, keep = 12): string {
  if (!signature) return '';
  return signature.length <= keep * 2 + 1 ? signature : `${signature.slice(0, keep)}…${signature.slice(-keep)}`;
}

/** Does the receipt carry a TSE block (signed, or issued during a TSE outage)? */
export function hasTseBlock(fiscal: ReceiptFiscal | null | undefined): boolean {
  return !!fiscal && (fiscal.status === 'Signed' || fiscal.status === 'Failed');
}

/** The 12 fields of a DSFinV-K QR payload (V0;client;processType;processData;tx;counter;start;end;alg;format;signature;publicKey). */
export interface QrFields {
  version: string;
  clientId: string;
  processType: string;
  processData: string;
  transactionNumber: string;
  signatureCounter: string;
  start: string;
  end: string;
  algorithm: string;
  timeFormat: string;
  signature: string;
  publicKey: string;
}

export function parseQrPayload(payload: string | null | undefined): QrFields | null {
  if (!payload) return null;
  const f = payload.split(';');
  if (f.length !== 12 || f[0] !== 'V0') return null;
  return {
    version: f[0], clientId: f[1], processType: f[2], processData: f[3], transactionNumber: f[4], signatureCounter: f[5],
    start: f[6], end: f[7], algorithm: f[8], timeFormat: f[9], signature: f[10], publicKey: f[11],
  };
}

/** QR modules as a single SVG path (rendered locally, no network). Error correction M, as usual for receipts. */
export function qrSvgPath(payload: string): { size: number; path: string } {
  const qr = qrcode(0, 'M');
  qr.addData(payload, 'Byte');
  qr.make();
  const size = qr.getModuleCount();
  let path = '';
  for (let r = 0; r < size; r++) {
    for (let c = 0; c < size; c++) {
      if (qr.isDark(r, c)) path += `M${c} ${r}h1v1h-1z`;
    }
  }
  return { size, path };
}

/** Visual tone of the TSE state in the till's top bar. */
export function tseTone(state: FiscalState | null | undefined): 'ok' | 'test' | 'danger' | 'muted' {
  switch (state) {
    case 'Ok':
      return 'ok';
    case 'Test':
      return 'test';
    case 'Outage':
    case 'Refused':
    case 'NotConfigured':
      return 'danger';
    default:
      return 'muted';
  }
}

/** The admin form for the simulated TSE / fiskaly (strings for inputs; empty = not set / inherit). */
export interface FiscalForm {
  mode: string;
  latencyMs: string;
  failureRatePercent: string;
  forcedOutage: '' | 'true' | 'false';
  algorithm: string;
  clientId: string;
  fiskalyTssId: string;
  fiskalyEnvironment: string;
  fiskalyCredentialReference: string;
  fiskalyApiKey: string;
  fiskalyApiSecret: string;
  clearFiskalyCredentials: boolean;
}

export function toForm(config: FiscalConfig | null): FiscalForm {
  return {
    mode: config?.mode ?? '',
    latencyMs: config?.latencyMs?.toString() ?? '',
    failureRatePercent: config?.failureRatePercent?.toString() ?? '',
    forcedOutage: config?.forcedOutage === true ? 'true' : config?.forcedOutage === false ? 'false' : '',
    algorithm: config?.algorithm ?? '',
    clientId: config?.clientId ?? '',
    fiskalyTssId: config?.fiskalyTssId ?? '',
    fiskalyEnvironment: config?.fiskalyEnvironment ?? '',
    fiskalyCredentialReference: config?.fiskalyCredentialReference ?? '',
    fiskalyApiKey: '',
    fiskalyApiSecret: '',
    clearFiskalyCredentials: false,
  };
}

export type FiscalFormErrors = Partial<Record<'mode' | 'latencyMs' | 'failureRatePercent' | 'fiskalyTssId' | 'fiskalyApiSecret', string>>;

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** Validates like the API (organization rows need a mode); returns i18n keys per field. */
export function validateFiscalForm(form: FiscalForm, scope: 'organization' | 'register'): FiscalFormErrors {
  const errors: FiscalFormErrors = {};
  if (scope === 'organization' && !form.mode) errors.mode = 'admin.fiscal.validation.modeRequired';
  const latency = form.latencyMs.trim();
  if (latency && !(/^\d+$/.test(latency) && Number(latency) <= 30000)) errors.latencyMs = 'admin.fiscal.validation.latency';
  const rate = form.failureRatePercent.trim();
  if (rate && !(/^\d+$/.test(rate) && Number(rate) <= 100)) errors.failureRatePercent = 'admin.fiscal.validation.failureRate';
  if (form.fiskalyTssId.trim() && !UUID.test(form.fiskalyTssId.trim())) errors.fiskalyTssId = 'admin.fiscal.validation.uuid';
  if (!!form.fiskalyApiKey.trim() !== !!form.fiskalyApiSecret.trim()) errors.fiskalyApiSecret = 'admin.fiscal.validation.credentialsPair';
  return errors;
}

export function toRequest(form: FiscalForm, scope: 'organization' | 'register'): FiscalConfigRequest {
  const text = (v: string) => (v.trim() ? v.trim() : null);
  const num = (v: string) => (v.trim() ? Number(v.trim()) : null);
  const request: FiscalConfigRequest = {
    mode: (text(form.mode) as FiscalConfigRequest['mode']) ?? null,
    latencyMs: num(form.latencyMs),
    failureRatePercent: num(form.failureRatePercent),
    forcedOutage: form.forcedOutage === '' ? null : form.forcedOutage === 'true',
  };
  if (scope === 'register') {
    request.clientId = text(form.clientId);
    return request;
  }
  request.algorithm = text(form.algorithm);
  request.fiskalyTssId = text(form.fiskalyTssId);
  request.fiskalyEnvironment = text(form.fiskalyEnvironment);
  request.fiskalyCredentialReference = text(form.fiskalyCredentialReference);
  if (form.clearFiskalyCredentials) request.clearFiskalyCredentials = true;
  else if (text(form.fiskalyApiKey) && text(form.fiskalyApiSecret)) {
    request.fiskalyApiKey = text(form.fiskalyApiKey);
    request.fiskalyApiSecret = text(form.fiskalyApiSecret);
  }
  return request;
}
