import { validBridgeUrl, validHost } from '../printing/printer-form';
import { TerminalConnection, TerminalProfile, TerminalProfileInput } from './terminal-api';
import { DEFAULT_TERMINAL_BRIDGE_URL, DEFAULT_ZVT_PORT } from './terminal-transports';

/** Limits mirrored from the API (terminal profile rules). */
export const TERMINAL_LIMITS = { name: 100, host: 253, bridgeUrl: 500 } as const;

/** Form state: numbers as text (inputs). */
export interface TerminalForm {
  name: string;
  connection: TerminalConnection;
  host: string;
  port: string;
  bridgeUrl: string;
  zvtPassword: string;
  isDefault: boolean;
}

export type TerminalFormErrors = Partial<Record<keyof TerminalForm, string>>;

export function emptyTerminalForm(): TerminalForm {
  return { name: '', connection: 'Manual', host: '', port: String(DEFAULT_ZVT_PORT), bridgeUrl: DEFAULT_TERMINAL_BRIDGE_URL, zvtPassword: '000000', isDefault: false };
}

export function formFromTerminal(p: TerminalProfile): TerminalForm {
  return {
    name: p.name,
    connection: p.connection,
    host: p.host ?? '',
    port: String(p.port ?? DEFAULT_ZVT_PORT),
    bridgeUrl: p.bridgeUrl ?? DEFAULT_TERMINAL_BRIDGE_URL,
    zvtPassword: p.zvtPassword ?? '000000',
    isDefault: p.isDefault,
  };
}

/** Validates the form (values are keys of pos.terminals.form.errors.*) and builds the API body. */
export function terminalInput(f: TerminalForm, version: number | null): { input: TerminalProfileInput | null; errors: TerminalFormErrors } {
  const e: TerminalFormErrors = {};
  const name = f.name.trim();
  if (!name || name.length > TERMINAL_LIMITS.name) e.name = 'pos.terminals.form.errors.name';
  const zvt = f.connection === 'ZvtBridge';
  const host = f.host.trim();
  const portText = f.port.trim();
  let port: number | null = null;
  const bridgeUrl = f.bridgeUrl.trim();
  const password = f.zvtPassword.trim() || '000000';
  if (zvt) {
    if (!host || !validHost(host)) e.host = 'pos.terminals.form.errors.host';
    port = portText ? (/^\d{1,5}$/.test(portText) ? Number(portText) : NaN) : DEFAULT_ZVT_PORT;
    if (!Number.isInteger(port) || port < 1 || port > 65535) e.port = 'pos.terminals.form.errors.port';
    if (!validBridgeUrl(bridgeUrl)) e.bridgeUrl = 'pos.terminals.form.errors.bridgeUrl';
    if (!/^\d{6}$/.test(password)) e.zvtPassword = 'pos.terminals.form.errors.password';
  }
  if (Object.keys(e).length) return { input: null, errors: e };
  return {
    errors: e,
    input: {
      name,
      connection: f.connection,
      host: zvt ? host : null,
      port: zvt ? port : null,
      bridgeUrl: zvt ? bridgeUrl : null,
      zvtPassword: zvt ? password : null,
      isDefault: f.isDefault || null,
      version,
    },
  };
}
