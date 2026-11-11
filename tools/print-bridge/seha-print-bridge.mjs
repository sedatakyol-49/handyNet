#!/usr/bin/env node
// SEHA Print Bridge — reference implementation of the local HTTP → raw TCP contracts of ADR-0018 (receipt printers,
// port 9100) and of the card terminal feature (ZVT over TCP, default port 20007).
//
// Browsers cannot open TCP sockets, so a till that prints raw ESC/POS to a network printer, or that drives a ZVT card
// terminal, posts to this bridge on the till PC, which talks to the device. No dependencies (Node 20+). Not an installer
// and not a service wrapper: run it with `node seha-print-bridge.mjs` (or register it with the OS's own tools).
//
// ZVT is implemented from the published specification and is NOT VERIFIED ON HARDWARE (see zvt.mjs).
//
// Configuration (environment):
//   BRIDGE_PORT        listening port on 127.0.0.1 (default 9123); the bridge never listens on other interfaces
//   ALLOWED_ORIGINS    comma-separated origins allowed to use the bridge, e.g. "https://app.example.com,http://localhost:4200" (required)
//   ALLOWED_PRINTERS   comma-separated host:port printer targets, e.g. "192.168.1.50:9100,192.168.1.51:9100"
//   ALLOWED_TERMINALS  comma-separated host:port card terminal targets, e.g. "192.168.1.60:20007"
//                      (at least one of ALLOWED_PRINTERS / ALLOWED_TERMINALS is required)
//   BRIDGE_TOKEN       optional shared token; when set, requests need "Authorization: Bearer <token>"
//   ZVT_CONFIG_BYTE    registration config byte in hex (default 8E, see zvt.mjs)
//   ZVT_IDLE_TIMEOUT_S seconds the terminal may stay silent during a transaction (default 180)
//
// Contract:
//   OPTIONS *                CORS preflight (incl. Access-Control-Allow-Private-Network for Chrome's local network access)
//   POST    /print           body = raw ESC/POS bytes (application/octet-stream, max 1 MiB);
//                            headers X-Printer-Host, X-Printer-Port (default 9100) → must be in ALLOWED_PRINTERS
//                            200 {"ok":true} | 4xx/5xx {"ok":false,"error":"..."}
//   GET     /status          200 {"ok":true,"printers":["host:port", …],"terminals":["host:port", …]}
//   POST    /zvt/payment     JSON {host, port?, password?, amountMinor}            → 200 {"ok":true,"result":{…}}
//   POST    /zvt/refund      JSON {host, port?, password?, amountMinor}
//   POST    /zvt/reversal    JSON {host, port?, password?, receiptNumber}
//   POST    /zvt/end-of-day  JSON {host, port?, password?}
//   POST    /zvt/status      JSON {host, port?, password?} → {"ok":true,"busy":true,"intermediate":{code,text}} while a
//                            transaction runs on that terminal, else registers at the terminal and reports
//                            {"ok":true,"busy":false,"result":{…}}
//   POST    /zvt/abort       JSON {host, port?} → {"ok":true,"aborted":true|false} (asks the terminal to abort the running transaction)
//   The target must be in ALLOWED_TERMINALS. One transaction per terminal at a time (409 otherwise).
//   result = {status: "approved"|"declined"|"aborted"|"failed", uncertain, resultCode, resultText, amountMinor,
//             traceNumber, receiptNumber, terminalId, cardName, maskedPan, authCode, customerReceipt, merchantReceipt, …}.
//   "uncertain": the connection broke after a money command was sent — check on the terminal whether it was booked.
import { createServer } from 'node:http';
import { connect } from 'node:net';
import { timingSafeEqual } from 'node:crypto';
import { pathToFileURL } from 'node:url';
import { runZvt } from './zvt.mjs';

const MAX_BYTES = 1024 * 1024;
const MAX_JSON_BYTES = 4 * 1024;
const TCP_TIMEOUT_MS = 10_000;
const ZVT_PATHS = new Set(['/zvt/payment', '/zvt/refund', '/zvt/reversal', '/zvt/end-of-day', '/zvt/status', '/zvt/abort']);

const list = (value) => new Set(String(value ?? '').split(',').map((s) => s.trim().toLowerCase()).filter(Boolean));

/** Reads the bridge configuration from an environment object. */
export function configFromEnv(env = process.env) {
  return {
    port: Number(env.BRIDGE_PORT ?? 9123),
    origins: new Set(String(env.ALLOWED_ORIGINS ?? '').split(',').map((s) => s.trim()).filter(Boolean)),
    printers: list(env.ALLOWED_PRINTERS),
    terminals: list(env.ALLOWED_TERMINALS),
    token: env.BRIDGE_TOKEN ?? '',
    zvtConfigByte: parseInt(env.ZVT_CONFIG_BYTE ?? '8E', 16),
    zvtIdleTimeoutMs: Number(env.ZVT_IDLE_TIMEOUT_S ?? 180) * 1000,
  };
}

function sendToPrinter(host, port, bytes) {
  return new Promise((resolve, reject) => {
    const socket = connect({ host, port, timeout: TCP_TIMEOUT_MS }, () => socket.end(bytes));
    socket.on('timeout', () => socket.destroy(new Error('printer timeout')));
    socket.on('error', reject);
    socket.on('close', (hadError) => !hadError && resolve());
  });
}

function readBody(req, limit) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let size = 0;
    req.on('data', (c) => {
      size += c.length;
      if (size > limit) {
        reject(Object.assign(new Error('body too large'), { status: 413 }));
        req.destroy();
      } else chunks.push(c);
    });
    req.on('end', () => resolve(Buffer.concat(chunks)));
    req.on('error', reject);
  });
}

/**
 * Creates the bridge's HTTP server (not listening yet). `config.zvt` may replace the ZVT runner (tests).
 */
export function createBridge(config) {
  const { origins, printers, terminals, token } = config;
  const zvt = config.zvt ?? runZvt;
  /** Running ZVT dialogues by "host:port" (one per terminal). */
  const sessions = new Map();

  function cors(req, res) {
    const origin = req.headers.origin;
    if (origin && origins.has(origin)) {
      res.setHeader('Access-Control-Allow-Origin', origin);
      res.setHeader('Vary', 'Origin');
      res.setHeader('Access-Control-Allow-Methods', 'POST, GET, OPTIONS');
      res.setHeader('Access-Control-Allow-Headers', 'Content-Type, X-Printer-Host, X-Printer-Port, Authorization');
      res.setHeader('Access-Control-Allow-Private-Network', 'true');
      res.setHeader('Access-Control-Max-Age', '600');
    }
    return !origin || origins.has(origin);
  }

  function reply(res, status, body) {
    if (res.writableEnded) return;
    res.writeHead(status, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
    res.end(JSON.stringify(body));
  }

  function authorized(req) {
    if (!token) return true;
    const given = Buffer.from(String(req.headers.authorization ?? ''));
    const expected = Buffer.from(`Bearer ${token}`);
    return given.length === expected.length && timingSafeEqual(given, expected);
  }

  async function print(req, res) {
    const host = String(req.headers['x-printer-host'] ?? '').trim().toLowerCase();
    const port = Number(req.headers['x-printer-port'] ?? 9100);
    if (!printers.has(`${host}:${port}`)) return reply(res, 403, { ok: false, error: `printer ${host}:${port} not in ALLOWED_PRINTERS` });
    let bytes;
    try {
      bytes = await readBody(req, MAX_BYTES);
    } catch (e) {
      return reply(res, e.status ?? 400, { ok: false, error: e.status === 413 ? 'print job too large' : 'bad request' });
    }
    try {
      await sendToPrinter(host, port, bytes);
      reply(res, 200, { ok: true });
    } catch (e) {
      reply(res, 502, { ok: false, error: `printer not reachable: ${e.message}` });
    }
  }

  async function zvtRequest(path, req, res) {
    let body;
    try {
      body = JSON.parse((await readBody(req, MAX_JSON_BYTES)).toString('utf8') || '{}');
    } catch (e) {
      return reply(res, e.status ?? 400, { ok: false, error: e.status === 413 ? 'request too large' : 'invalid JSON' });
    }
    if (!body || typeof body !== 'object') return reply(res, 400, { ok: false, error: 'invalid JSON' });
    const host = String(body.host ?? '').trim().toLowerCase();
    const port = body.port === undefined || body.port === null ? 20007 : Number(body.port);
    const target = `${host}:${port}`;
    // The bridge is not an open TCP relay: only configured terminals.
    if (!host || !Number.isInteger(port) || !terminals.has(target)) return reply(res, 403, { ok: false, error: `terminal ${target} not in ALLOWED_TERMINALS` });
    const command = path.slice('/zvt/'.length);
    const active = sessions.get(target);

    if (command === 'abort') return reply(res, 200, { ok: true, aborted: !!active?.session?.abort() });
    if (command === 'status' && active) return reply(res, 200, { ok: true, busy: true, intermediate: active.session?.latest() ?? null });
    if (active) return reply(res, 409, { ok: false, error: 'a transaction is already running on this terminal' });

    const password = body.password === undefined || body.password === null || body.password === '' ? '000000' : String(body.password);
    if (!/^\d{6}$/.test(password)) return reply(res, 400, { ok: false, error: 'password must be 6 digits' });
    const options = { host, port, command, password, configByte: config.zvtConfigByte, idleTimeoutMs: config.zvtIdleTimeoutMs };
    if (command === 'payment' || command === 'refund') {
      if (!Number.isSafeInteger(body.amountMinor) || body.amountMinor <= 0 || body.amountMinor > 999_999_999_999) return reply(res, 400, { ok: false, error: 'amountMinor must be positive integer cents' });
      options.amountMinor = body.amountMinor;
    }
    if (command === 'reversal') {
      if (!Number.isInteger(body.receiptNumber) || body.receiptNumber < 0 || body.receiptNumber > 9999) return reply(res, 400, { ok: false, error: 'receiptNumber must be 0–9999' });
      options.receiptNumber = body.receiptNumber;
    }

    const entry = { session: null };
    sessions.set(target, entry);
    options.onSession = (session) => (entry.session = session);
    try {
      const result = await zvt(options);
      reply(res, 200, command === 'status' ? { ok: true, busy: false, result } : { ok: true, result });
    } catch (e) {
      reply(res, 500, { ok: false, error: `bridge error: ${e.message}` });
    } finally {
      sessions.delete(target);
    }
  }

  return createServer((req, res) => {
    const url = new URL(req.url ?? '/', 'http://localhost');
    // Requests from origins that are not allowed get no CORS headers (the browser rejects them) and a 403.
    if (!cors(req, res)) return reply(res, 403, { ok: false, error: 'origin not allowed' });
    if (req.method === 'OPTIONS') return res.writeHead(204).end();
    if (req.method === 'GET' && url.pathname === '/status') return reply(res, 200, { ok: true, printers: [...printers], terminals: [...terminals] });
    const isPrint = url.pathname === '/print';
    if (req.method !== 'POST' || (!isPrint && !ZVT_PATHS.has(url.pathname))) return reply(res, 404, { ok: false, error: 'not found' });
    if (!authorized(req)) return reply(res, 401, { ok: false, error: 'token required' });
    void (isPrint ? print(req, res) : zvtRequest(url.pathname, req, res));
  });
}

// Started directly (not imported by the tests): read the environment and listen on loopback only.
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const config = configFromEnv();
  if (!config.origins.size || (!config.printers.size && !config.terminals.size)) {
    console.error('Set ALLOWED_ORIGINS and ALLOWED_PRINTERS and/or ALLOWED_TERMINALS (see the header of this file).');
    process.exit(1);
  }
  createBridge(config).listen(config.port, '127.0.0.1', () => {
    console.log(`SEHA Print Bridge on http://127.0.0.1:${config.port}`);
    if (config.printers.size) console.log(`  printers:  ${[...config.printers].join(', ')}`);
    if (config.terminals.size) console.log(`  terminals: ${[...config.terminals].join(', ')} (ZVT, not verified on hardware)`);
  });
}
