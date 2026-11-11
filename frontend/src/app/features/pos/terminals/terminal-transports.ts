import { isMixedContent, hostPort } from '../printing/printer-transports';
import { CardResultFields, CardResultRequest, CardTransactionKind, TerminalProfile } from './terminal-api';

/**
 * Transports from the till's browser to the card terminal. The SEHA server is never involved:
 *   Manual     – no connection; the cashier keys the amount into the terminal and confirms the payment in the till;
 *   Simulated  – test only: the cashier picks the outcome in a dialog, the result data is fake and marked TEST;
 *   ZvtBridge  – the local SEHA bridge on the till PC talks ZVT over TCP to the terminal (not verified on hardware).
 */

export type TerminalStatus = 'Approved' | 'Declined' | 'Aborted' | 'Failed';

/** Why a terminal run failed before or outside the terminal's own answer. */
export type TerminalErrorCode =
  | 'mixedContent' // https page → http bridge on another host: the browser blocks the request
  | 'bridgeUnreachable' // bridge not running, wrong URL or CORS refused (the browser does not tell them apart)
  | 'bridgeRefused' // the bridge answered 4xx (terminal not in its allow-list, token, origin, bad request)
  | 'bridgeBusy' // another transaction is running on this terminal
  | 'bridgeError' // the bridge answered 5xx or nonsense
  | 'terminalUnreachable' // the bridge could not connect to the terminal
  | 'terminal' // the terminal reported a technical fault
  | 'timeout' // no answer in time
  | 'noReceiptNumber' // a reversal needs the terminal's receipt number of the payment
  | 'simulated'; // chosen in the simulator

export interface TerminalOutcome extends CardResultFields {
  status: TerminalStatus;
  /**
   * The outcome is unknown: the connection broke or timed out after the amount was sent to the terminal. The terminal
   * may or may not have booked the payment — a person has to check on the terminal.
   */
  uncertain: boolean;
  errorCode: TerminalErrorCode | null;
  /** Fake result of the simulated terminal. */
  test: boolean;
}

export type TerminalOperation = 'payment' | 'refund' | 'reversal' | 'end-of-day' | 'status' | 'abort';

/** Payments wait for the customer; the bridge itself gives up after its idle timeout (default 180 s). */
export const TERMINAL_TIMEOUT_MS = { money: 200_000, status: 20_000 } as const;
export const DEFAULT_TERMINAL_BRIDGE_URL = 'http://localhost:9123';
export const DEFAULT_ZVT_PORT = 20007;
/** A request to the bridge that fails after this long was in flight (refused connections fail within milliseconds). */
const BRIDGE_IN_FLIGHT_MS = 2000;

/** Bridge endpoint for an operation; a URL copied from a printer profile (…/print) or with a trailing slash works too. */
export function bridgeEndpoint(bridgeUrl: string | null | undefined, operation: TerminalOperation): string {
  const base = (bridgeUrl || DEFAULT_TERMINAL_BRIDGE_URL).trim().replace(/\/+$/, '').replace(/\/(print|zvt(\/[a-z-]+)?)$/i, '');
  return `${base}/zvt/${operation}`;
}

/** Never more than the first 6 and last 4 digits, whatever a terminal or bridge sends. */
export function maskPan(pan: string | null | undefined): string | null {
  const digits = (pan ?? '').replace(/[^0-9*xX•]/g, '').replace(/[xX•]/g, '*');
  if (!digits) return null;
  if (digits.length <= 10) return '*'.repeat(digits.length);
  return digits.slice(0, 6) + '*'.repeat(digits.length - 10) + digits.slice(-4);
}

const clip = (value: unknown, max: number): string | null => {
  if (value === null || value === undefined) return null;
  const text = String(value).trim();
  return text ? text.slice(0, max) : null;
};

function failed(errorCode: TerminalErrorCode, uncertain: boolean, resultText: string | null = null): TerminalOutcome {
  return { status: 'Failed', uncertain, errorCode, test: false, resultText };
}

const BRIDGE_STATUS: Record<string, TerminalStatus> = { approved: 'Approved', declined: 'Declined', aborted: 'Aborted', failed: 'Failed' };

/** Maps the bridge's `result` object (see tools/print-bridge) to the till's outcome. Anything unexpected is a failure. */
export function mapBridgeResult(result: unknown, operation: TerminalOperation): TerminalOutcome {
  const money = operation === 'payment' || operation === 'refund' || operation === 'reversal';
  if (!result || typeof result !== 'object') return failed('bridgeError', money);
  const r = result as Record<string, unknown>;
  const status = BRIDGE_STATUS[String(r['status'])];
  if (!status) return failed('bridgeError', money);
  const code = typeof r['resultCode'] === 'number' ? `0x${r['resultCode'].toString(16).toUpperCase().padStart(2, '0')}` : clip(r['resultCode'], 10);
  const error = clip(r['error'], 200);
  const unreachable = status === 'Failed' && !!error && /not reachable/i.test(error);
  return {
    status,
    uncertain: status === 'Failed' && r['uncertain'] === true,
    errorCode: status === 'Failed' ? (unreachable ? 'terminalUnreachable' : r['uncertain'] === true ? 'timeout' : 'terminal') : null,
    test: false,
    terminalId: clip(r['terminalId'], 20),
    traceNumber: clip(r['traceNumber'], 20),
    terminalReceiptNumber: clip(r['receiptNumber'], 20),
    cardName: clip(r['cardName'], 60),
    maskedPan: maskPan(clip(r['maskedPan'], 40)),
    authCode: clip(r['authCode'], 20),
    resultCode: code,
    resultText: clip(r['resultText'], 300) ?? error,
    customerReceiptText: clip(r['customerReceipt'], 4000),
    merchantReceiptText: clip(r['merchantReceipt'], 4000),
  };
}

export interface BridgeRequest {
  amountMinor?: number;
  receiptNumber?: number;
}

/** Body the bridge expects for a profile. */
export function bridgeBody(profile: Pick<TerminalProfile, 'host' | 'port' | 'zvtPassword'>, request: BridgeRequest = {}): Record<string, unknown> {
  return { host: (profile.host ?? '').trim(), port: profile.port ?? DEFAULT_ZVT_PORT, password: profile.zvtPassword || '000000', ...request };
}

/**
 * One call to the bridge. Never throws: every failure is an outcome. `uncertain` is set whenever a money operation may
 * have reached the terminal (timeout, connection to the bridge lost after the request was sent).
 */
export async function callBridge(profile: TerminalProfile, operation: TerminalOperation, request: BridgeRequest = {}, fetchFn: typeof fetch = fetch): Promise<TerminalOutcome> {
  const money = operation === 'payment' || operation === 'refund' || operation === 'reversal';
  const url = bridgeEndpoint(profile.bridgeUrl, operation);
  if (typeof location !== 'undefined' && isMixedContent(location.protocol, url)) return failed('mixedContent', false);
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), money ? TERMINAL_TIMEOUT_MS.money : TERMINAL_TIMEOUT_MS.status);
  const started = Date.now();
  let response: Response;
  try {
    response = await fetchFn(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(bridgeBody(profile, request)),
      mode: 'cors',
      cache: 'no-store',
      credentials: 'omit',
      signal: controller.signal,
    });
  } catch {
    // A timeout means the bridge got the request and the terminal may be mid-transaction. A network error right away
    // is "bridge not running" (nothing reached the terminal); one that comes later means the connection to the bridge
    // broke while the transaction was running — the outcome is unknown.
    if (controller.signal.aborted) return failed('timeout', money);
    return failed('bridgeUnreachable', money && Date.now() - started > BRIDGE_IN_FLIGHT_MS);
  } finally {
    clearTimeout(timer);
  }
  let json: unknown = null;
  try {
    json = await response.json();
  } catch {
    json = null;
  }
  const body = (json && typeof json === 'object' ? json : {}) as Record<string, unknown>;
  if (response.status === 409) return failed('bridgeBusy', false, clip(body['error'], 200));
  if (response.status >= 400 && response.status < 500) return failed('bridgeRefused', false, clip(body['error'], 200) ?? `HTTP ${response.status}`);
  if (!response.ok || body['ok'] !== true) return failed('bridgeError', money, clip(body['error'], 200) ?? `HTTP ${response.status}`);
  if (operation === 'abort') return { status: body['aborted'] === true ? 'Aborted' : 'Failed', uncertain: false, errorCode: null, test: false };
  if (operation === 'status' && body['busy'] === true) {
    const s = body['intermediate'] as Record<string, unknown> | null;
    return { status: 'Approved', uncertain: false, errorCode: 'bridgeBusy', test: false, resultText: clip(s?.['text'], 200) };
  }
  return mapBridgeResult(body['result'], operation);
}

/** Progress text of a running transaction (`/zvt/status` answers `busy` with the terminal's last intermediate status). */
export async function bridgeProgress(profile: TerminalProfile, fetchFn: typeof fetch = fetch): Promise<string | null> {
  const outcome = await callBridge(profile, 'status', {}, fetchFn);
  return outcome.errorCode === 'bridgeBusy' ? (outcome.resultText ?? null) : null;
}

// ---------------------------------------------------------------- simulated terminal (test only)

export type SimulatedChoice = 'approve' | 'decline' | 'abort' | 'timeout';
export const SIMULATED_CHOICES: SimulatedChoice[] = ['approve', 'decline', 'abort', 'timeout'];

/**
 * Fake result of the simulated terminal — clearly marked TEST everywhere it is shown or stored. `sequence` only makes
 * trace and receipt numbers differ between attempts.
 */
export function simulatedOutcome(choice: SimulatedChoice, kind: CardTransactionKind | 'Reversal', amountMinor: number, sequence: number): TerminalOutcome {
  const n = ((sequence % 9999) + 9999) % 9999 + 1;
  const base = { test: true, terminalId: 'TEST0001', traceNumber: String(n).padStart(6, '0') };
  const euros = `${Math.floor(amountMinor / 100)},${String(amountMinor % 100).padStart(2, '0')}`;
  switch (choice) {
    case 'approve': {
      const title = kind === 'Refund' ? 'GUTSCHRIFT' : kind === 'Reversal' ? 'STORNO' : 'KARTENZAHLUNG';
      const receipt = (who: string) => ['*** TEST – KEINE ECHTE ZAHLUNG ***', who, title, `EUR ${euros}`, 'TEST girocard', '476173******0010', `Trace ${base.traceNumber}  Beleg ${String(n).padStart(4, '0')}`, '*** TEST ***'].join('\n');
      return {
        ...base,
        status: 'Approved',
        uncertain: false,
        errorCode: null,
        terminalReceiptNumber: String(n).padStart(4, '0'),
        cardName: 'TEST girocard',
        maskedPan: '476173******0010',
        authCode: 'TEST00',
        resultCode: '0x00',
        resultText: 'TEST: genehmigt',
        customerReceiptText: receipt('Kundenbeleg'),
        merchantReceiptText: receipt('Händlerbeleg'),
      };
    }
    case 'decline':
      return { ...base, status: 'Declined', uncertain: false, errorCode: null, resultCode: '0x05', resultText: 'TEST: abgelehnt' };
    case 'abort':
      return { ...base, status: 'Aborted', uncertain: false, errorCode: null, resultCode: '0x6C', resultText: 'TEST: abgebrochen' };
    case 'timeout':
      // Like a real connection loss mid-transaction: nobody knows whether the terminal booked it.
      return { ...base, status: 'Failed', uncertain: true, errorCode: 'simulated', resultText: 'TEST: keine Antwort vom Terminal' };
  }
}

/** The request body for `POST /card-transactions/{id}/result`. */
export function resultRequest(o: TerminalOutcome): CardResultRequest {
  return {
    status: o.status,
    terminalId: o.terminalId ?? null,
    traceNumber: o.traceNumber ?? null,
    terminalReceiptNumber: o.terminalReceiptNumber ?? null,
    cardName: o.cardName ?? null,
    maskedPan: maskPan(o.maskedPan),
    authCode: o.authCode ?? null,
    resultCode: o.resultCode ?? null,
    resultText: clip(`${o.uncertain ? 'Ergebnis unbekannt – am Terminal prüfen. ' : ''}${o.resultText ?? ''}`, 300),
    customerReceiptText: o.customerReceiptText ?? null,
    merchantReceiptText: o.merchantReceiptText ?? null,
  };
}

/** Where a profile sends the terminal traffic (shown on the profile card). */
export function terminalSummary(p: Pick<TerminalProfile, 'connection' | 'host' | 'port' | 'bridgeUrl'>): string {
  return p.connection === 'ZvtBridge' ? `${hostPort(p.host ?? '', p.port ?? DEFAULT_ZVT_PORT)} ← ${bridgeEndpoint(p.bridgeUrl, 'payment').replace(/\/zvt\/payment$/, '')}` : '';
}
