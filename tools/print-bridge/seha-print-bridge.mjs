#!/usr/bin/env node
// SEHA Print Bridge — reference implementation of the local HTTP → raw TCP (port 9100) contract of ADR-0018.
//
// Browsers cannot open TCP sockets, so a till that prints raw ESC/POS to a network printer posts the bytes to this bridge
// on the till PC, which forwards them to the printer. No dependencies (Node 20+). Not an installer and not a service
// wrapper: run it with `node seha-print-bridge.mjs` (or register it with the OS's own tools).
//
// Configuration (environment):
//   BRIDGE_PORT       listening port on 127.0.0.1 (default 9123); the bridge never listens on other interfaces
//   ALLOWED_ORIGINS   comma-separated origins allowed to print, e.g. "https://app.example.com,http://localhost:4200" (required)
//   ALLOWED_PRINTERS  comma-separated host:port targets, e.g. "192.168.1.50:9100,192.168.1.51:9100" (required)
//   BRIDGE_TOKEN      optional shared token; when set, requests need "Authorization: Bearer <token>"
//
// Contract:
//   OPTIONS /print           CORS preflight (incl. Access-Control-Allow-Private-Network for Chrome's local network access)
//   POST    /print           body = raw ESC/POS bytes (application/octet-stream, max 1 MiB);
//                            headers X-Printer-Host, X-Printer-Port (default 9100) → must be in ALLOWED_PRINTERS
//                            200 {"ok":true} | 4xx/5xx {"ok":false,"error":"..."}
//   GET     /status          200 {"ok":true,"printers":["host:port", …]}
import { createServer } from 'node:http';
import { connect } from 'node:net';
import { timingSafeEqual } from 'node:crypto';

const PORT = Number(process.env.BRIDGE_PORT ?? 9123);
const ORIGINS = new Set((process.env.ALLOWED_ORIGINS ?? '').split(',').map((s) => s.trim()).filter(Boolean));
const PRINTERS = new Set((process.env.ALLOWED_PRINTERS ?? '').split(',').map((s) => s.trim().toLowerCase()).filter(Boolean));
const TOKEN = process.env.BRIDGE_TOKEN ?? '';
const MAX_BYTES = 1024 * 1024;
const TCP_TIMEOUT_MS = 10_000;

if (!ORIGINS.size || !PRINTERS.size) {
  console.error('Set ALLOWED_ORIGINS and ALLOWED_PRINTERS (see the header of this file).');
  process.exit(1);
}

function cors(req, res) {
  const origin = req.headers.origin;
  if (origin && ORIGINS.has(origin)) {
    res.setHeader('Access-Control-Allow-Origin', origin);
    res.setHeader('Vary', 'Origin');
    res.setHeader('Access-Control-Allow-Methods', 'POST, GET, OPTIONS');
    res.setHeader('Access-Control-Allow-Headers', 'Content-Type, X-Printer-Host, X-Printer-Port, Authorization');
    res.setHeader('Access-Control-Allow-Private-Network', 'true');
    res.setHeader('Access-Control-Max-Age', '600');
  }
  return !origin || ORIGINS.has(origin);
}

function reply(res, status, body) {
  res.writeHead(status, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
  res.end(JSON.stringify(body));
}

function authorized(req) {
  if (!TOKEN) return true;
  const given = Buffer.from(String(req.headers.authorization ?? ''));
  const expected = Buffer.from(`Bearer ${TOKEN}`);
  return given.length === expected.length && timingSafeEqual(given, expected);
}

function send(host, port, bytes) {
  return new Promise((resolve, reject) => {
    const socket = connect({ host, port, timeout: TCP_TIMEOUT_MS }, () => socket.end(bytes));
    socket.on('timeout', () => socket.destroy(new Error('printer timeout')));
    socket.on('error', reject);
    socket.on('close', (hadError) => !hadError && resolve());
  });
}

const server = createServer((req, res) => {
  const url = new URL(req.url ?? '/', 'http://localhost');
  // Requests from origins that are not allowed get no CORS headers (the browser rejects them) and a 403.
  if (!cors(req, res)) return reply(res, 403, { ok: false, error: 'origin not allowed' });
  if (req.method === 'OPTIONS') return res.writeHead(204).end();
  if (req.method === 'GET' && url.pathname === '/status') return reply(res, 200, { ok: true, printers: [...PRINTERS] });
  if (req.method !== 'POST' || url.pathname !== '/print') return reply(res, 404, { ok: false, error: 'not found' });
  if (!authorized(req)) return reply(res, 401, { ok: false, error: 'token required' });

  const host = String(req.headers['x-printer-host'] ?? '').trim().toLowerCase();
  const port = Number(req.headers['x-printer-port'] ?? 9100);
  if (!PRINTERS.has(`${host}:${port}`)) return reply(res, 403, { ok: false, error: `printer ${host}:${port} not in ALLOWED_PRINTERS` });

  const chunks = [];
  let size = 0;
  req.on('data', (c) => {
    size += c.length;
    if (size > MAX_BYTES) {
      reply(res, 413, { ok: false, error: 'print job too large' });
      req.destroy();
    } else chunks.push(c);
  });
  req.on('end', async () => {
    if (res.writableEnded) return;
    try {
      await send(host, port, Buffer.concat(chunks));
      reply(res, 200, { ok: true });
    } catch (e) {
      reply(res, 502, { ok: false, error: `printer not reachable: ${e.message}` });
    }
  });
});

server.listen(PORT, '127.0.0.1', () => console.log(`SEHA Print Bridge on http://127.0.0.1:${PORT} → ${[...PRINTERS].join(', ')}`));
