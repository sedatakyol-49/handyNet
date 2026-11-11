// Byte-level tests of the ZVT implementation against a fake terminal TCP socket, and of the bridge's HTTP contract.
// Run: node --test tools/print-bridge/      (or `npm run test:bridge` in frontend/)
//
// The fake terminal follows the same published specification as the implementation — these tests prove that the bytes
// match our reading of it and that every dialogue path ends in a defined result. They do not replace a hardware test.
import assert from 'node:assert/strict';
import { createServer } from 'node:net';
import { test } from 'node:test';
import { createBridge } from './seha-print-bridge.mjs';
import {
  ACK,
  ApduParser,
  abortCommand,
  apdu,
  authorization,
  bcd,
  classifyResultCode,
  cp437,
  endOfDay,
  fromBcd,
  maskPan,
  parseBmps,
  parseIntermediateStatus,
  parsePrintLine,
  parsePrintTextBlock,
  parseStatusInformation,
  parseTlv,
  receiptTexts,
  refund,
  registration,
  resultText,
  reversal,
  runZvt,
} from './zvt.mjs';

const hex = (buf) => Buffer.from(buf).toString('hex');
const bytes = (text) => Buffer.from(text.replace(/\s+/g, ''), 'hex');
const ascii = (text) => Buffer.from(text, 'latin1');

// ------------------------------------------------------------------------------------------------ encoding

test('BCD and APDU framing', () => {
  assert.equal(hex(bcd(1250, 6)), '000000001250');
  assert.equal(hex(bcd(978, 2)), '0978');
  assert.equal(fromBcd(bytes('47 61 EE 0F')), '4761EE0F');
  assert.throws(() => bcd(1234567, 3), RangeError);
  assert.equal(hex(apdu(0x06, 0xb0)), '06b000');
  assert.equal(hex(apdu(0x06, 0x01, bytes('04 00'))), '0601020400');
  // From 255 bytes on: length byte FF followed by the length as two bytes, low byte first.
  const long = apdu(0x06, 0xd3, Buffer.alloc(300, 0x41));
  assert.equal(hex(long.subarray(0, 5)), '06d3ff2c01');
  assert.equal(long.length, 305);
});

test('commands ECR → PT are encoded as in the specification', () => {
  assert.equal(hex(registration()), '060006' + '000000' + '8e' + '0978');
  assert.equal(hex(registration({ password: '123456', configByte: 0xbe })), '060006' + '123456' + 'be' + '0978');
  assert.equal(hex(authorization({ amountMinor: 1250 })), '06010a' + '04' + '000000001250' + '49' + '0978');
  assert.equal(hex(authorization({ amountMinor: 123456789 })), '06010a' + '04' + '000123456789' + '49' + '0978');
  assert.equal(hex(reversal({ receiptNumber: 42 })), '063006' + '000000' + '87' + '0042');
  assert.equal(hex(refund({ password: '654321', amountMinor: 999 })), '06310d' + '654321' + '04' + '000000000999' + '49' + '0978');
  assert.equal(hex(endOfDay()), '065003' + '000000');
  assert.equal(hex(abortCommand()), '06b000');
  assert.equal(hex(ACK), '800000');
  assert.throws(() => authorization({ amountMinor: 0 }), RangeError);
  assert.throws(() => authorization({ amountMinor: 12.5 }), RangeError);
  assert.throws(() => reversal({ receiptNumber: 10000 }), RangeError);
  assert.throws(() => registration({ password: '12345' }), RangeError);
});

test('ApduParser reassembles split and joined chunks, including the extended length', () => {
  const parser = new ApduParser();
  const a = apdu(0x04, 0xff, bytes('0a'));
  const b = apdu(0x06, 0xd3, Buffer.alloc(260, 0x42));
  const stream = Buffer.concat([a, b, ACK]);
  const out = [];
  for (let i = 0; i < stream.length; i += 7) out.push(...parser.push(stream.subarray(i, i + 7)));
  assert.equal(out.length, 3);
  assert.deepEqual([out[0].cls, out[0].instr, hex(out[0].data)], [0x04, 0xff, '0a']);
  assert.equal(out[1].data.length, 260);
  assert.deepEqual([out[2].cls, out[2].instr, out[2].data.length], [0x80, 0x00, 0]);
  assert.deepEqual(parser.push(Buffer.from([0x06])), []); // incomplete header stays buffered
});

// ------------------------------------------------------------------------------------------------ decoding

const STATUS_APPROVED = Buffer.concat([
  bytes('27 00'), // result code
  bytes('04 00 00 00 00 12 50'), // amount 12,50
  bytes('0B 00 01 23'), // trace number
  bytes('0C 14 30 15'), // time
  bytes('0D 11 11'), // date MMDD
  bytes('0E 29 12'), // expiry (never passed on)
  bytes('22 F1 F0 47 61 73 EE EE EE 00 10 FF FF'), // PAN, LLVAR 10 bytes, masked by the terminal, F padding
  bytes('29 52 50 00 21'), // terminal id
  bytes('3B'), ascii('123456'), bytes('00 00'), // authorisation attribute
  bytes('49 09 78'),
  bytes('87 00 42'), // receipt number
  bytes('8A 05'),
  bytes('8B F0 F9'), ascii('girocard'), bytes('00'), // card name, LLVAR 9 bytes
  bytes('06 0B 1F 17 04'), ascii('TEST'), bytes('43 02 A0 00'), // TLV container (11 bytes)
]);

test('status information 04 0F: BMPs → result fields', () => {
  const s = parseStatusInformation(STATUS_APPROVED);
  assert.deepEqual(s, {
    resultCode: 0,
    amountMinor: 1250,
    traceNumber: '000123',
    originalTraceNumber: null,
    time: '143015',
    date: '1111',
    expiry: null,
    cardSequenceNumber: null,
    paymentType: null,
    maskedPan: '476173******0010',
    terminalId: '52500021',
    authCode: '123456',
    currency: '0978',
    receiptNumber: 42,
    turnoverNumber: null,
    cardType: 5,
    cardName: 'girocard',
    vuNumber: null,
    additionalText: null,
    extendedErrorText: 'TEST',
    applicationId: 'A000',
    unparsed: null,
  });
});

test('status information: an unknown BMP stops the parse but keeps what came before', () => {
  const s = parseStatusInformation(Buffer.concat([bytes('27 78 0B 00 00 07'), bytes('C7 01 02 03')]));
  assert.equal(s.resultCode, 0x78);
  assert.equal(s.traceNumber, '000007');
  assert.equal(s.unparsed, 'c7010203');
  const { fields } = parseBmps(bytes('3C F0 F0 F5 48 61 6C 6C 6F 27 00')); // LLLVAR 5 bytes "Hallo", then result code
  assert.equal(cp437(fields.get(0x3c)), 'Hallo');
  assert.equal(fields.get(0x27)[0], 0);
});

test('a full PAN is always reduced to first 6 + last 4; short values are fully masked', () => {
  assert.equal(maskPan('4761739001010010'), '476173******0010');
  assert.equal(maskPan('476173EEEEEE0010FFFF'), '476173******0010');
  assert.equal(maskPan('6799998900000060018'), '679999*********0018');
  assert.equal(maskPan('12345678'), '********');
  assert.equal(maskPan(''), null);
});

test('texts: CP437 umlauts, intermediate status, print lines, text blocks, TLV', () => {
  assert.equal(cp437(bytes('48 84 6E 64 6C 65 72 20 94 81 E1 8E 99 9A')), 'Händler öüßÄÖÜ');
  assert.equal(cp437(bytes('41 00 42')), 'A'); // zero-terminated
  assert.deepEqual(parseIntermediateStatus(bytes('0A')), { code: 0x0a, text: 'Bitte Karte einstecken' });
  assert.deepEqual(parseIntermediateStatus(bytes('C9')), { code: 0xc9, text: 'Terminalstatus 0xC9' });
  assert.deepEqual(parsePrintLine(Buffer.concat([bytes('00'), ascii('  EUR 12,50  ')])), { text: '  EUR 12,50', last: false, feeds: 0 });
  assert.deepEqual(parsePrintLine(Buffer.concat([bytes('81'), ascii('ENDE')])), { text: 'ENDE', last: true, feeds: 0 });
  assert.deepEqual(parsePrintLine(bytes('F3')), { text: '', last: false, feeds: 3 });
  const tlv = parseTlv(bytes('1F 07 01 02 25 08 07 02 41 42 07 02 43 44'));
  assert.deepEqual(tlv.map((n) => n.tag), ['1F07', '25']);
  assert.deepEqual(tlv[1].children.map((n) => n.value.toString('latin1')), ['AB', 'CD']);
  assert.deepEqual(parsePrintTextBlock(bytes('06 0E 1F 07 01 02 25 08 07 02 41 42 07 02 43 44')), { receiptType: 2, lines: ['AB', 'CD'] });
  assert.deepEqual(parseTlv(bytes('1F 07 05 01')), []); // truncated value: nothing, no exception
});

test('result codes: text and class', () => {
  assert.equal(classifyResultCode(0x00), 'approved');
  assert.equal(classifyResultCode(0x6c), 'aborted');
  assert.equal(classifyResultCode(0x78), 'declined');
  assert.equal(classifyResultCode(0x05), 'declined'); // authorisation system
  assert.equal(classifyResultCode(0xa3), 'failed');
  assert.equal(resultText(0x78), 'Karte verfallen (0x78)');
  assert.equal(resultText(0x05), 'Ablehnung durch das Autorisierungssystem (0x05)');
  assert.equal(resultText(0xd7), 'Terminalfehler 0xD7');
});

test('receipt texts: typed blocks, keyword detection and the fallback order', () => {
  assert.deepEqual(receiptTexts([{ type: 1, lines: ['H'] }, { type: 2, lines: ['K'] }]), { customerReceipt: 'K', merchantReceipt: 'H' });
  assert.deepEqual(receiptTexts([{ type: null, lines: ['** Kundenbeleg **', 'x'] }, { type: null, lines: ['Händlerbeleg'] }]), {
    customerReceipt: '** Kundenbeleg **\nx',
    merchantReceipt: 'Händlerbeleg',
  });
  assert.deepEqual(receiptTexts([{ type: null, lines: ['nur einer'] }]), { customerReceipt: 'nur einer', merchantReceipt: null });
  assert.deepEqual(receiptTexts([{ type: null, lines: ['erster'] }, { type: null, lines: ['zweiter'] }]), { customerReceipt: 'zweiter', merchantReceipt: 'erster' });
  assert.deepEqual(receiptTexts([{ type: null, lines: ['', ' '] }]), { customerReceipt: null, merchantReceipt: null });
});

// ------------------------------------------------------------------------------------------------ fake terminal

/**
 * A scripted terminal on a real TCP socket. The script receives `t`:
 *   t.read()  → next APDU from the till as hex (ACKs included),   t.send(...buffers) → writes bytes to the till.
 */
async function fakeTerminal(script) {
  const state = { connections: 0, error: null };
  const server = createServer((socket) => {
    state.connections++;
    const parser = new ApduParser();
    const queue = [];
    const waiters = [];
    socket.on('data', (chunk) => {
      for (const m of parser.push(chunk)) {
        const item = hex(apdu(m.cls, m.instr, m.data));
        const w = waiters.shift();
        if (w) w(item);
        else queue.push(item);
      }
    });
    socket.on('error', () => undefined);
    const t = {
      read: () => (queue.length ? Promise.resolve(queue.shift()) : new Promise((resolve) => waiters.push(resolve))),
      send: (...parts) => socket.write(Buffer.concat(parts)),
      close: () => socket.destroy(),
      socket,
    };
    Promise.resolve(script(t)).catch((e) => {
      state.error = e;
      socket.destroy();
    });
  });
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  return {
    port: server.address().port,
    state,
    close: () => new Promise((resolve) => server.close(resolve)),
  };
}

const COMPLETION = apdu(0x06, 0x0f);
const fast = { connectTimeoutMs: 1000, ackTimeoutMs: 500, idleTimeoutMs: 800 };

/** Registration as every dialogue starts: the till registers, the terminal acknowledges and completes. */
async function acceptRegistration(t, expected = '060006' + '000000' + '8e' + '0978') {
  assert.equal(await t.read(), expected);
  t.send(ACK, COMPLETION);
  assert.equal(await t.read(), '800000');
}

async function run(terminal, options) {
  try {
    const result = await runZvt({ host: '127.0.0.1', port: terminal.port, ...fast, ...options });
    if (terminal.state.error) throw terminal.state.error;
    return result;
  } finally {
    await terminal.close();
  }
}

test('payment approved: registration, authorization, intermediate status, status information, receipts, completion', async () => {
  const terminal = await fakeTerminal(async (t) => {
    await acceptRegistration(t);
    assert.equal(await t.read(), '06010a' + '04' + '000000001250' + '49' + '0978');
    t.send(ACK);
    t.send(apdu(0x04, 0xff, bytes('0a')));
    assert.equal(await t.read(), '800000');
    t.send(apdu(0x04, 0xff, bytes('0e')));
    assert.equal(await t.read(), '800000');
    t.send(apdu(0x04, 0x0f, STATUS_APPROVED));
    assert.equal(await t.read(), '800000');
    // merchant receipt, then customer receipt, as print lines (the last line of each has bit 7 set)
    for (const [attribute, text] of [[0x00, 'Haendlerbeleg'], [0x00, 'EUR 12,50'], [0x80, 'Zahlung erfolgt'], [0x00, 'Kundenbeleg'], [0xf1, ''], [0x80, 'Zahlung erfolgt']]) {
      t.send(apdu(0x06, 0xd1, Buffer.concat([Buffer.from([attribute]), ascii(text)])));
      assert.equal(await t.read(), '800000');
    }
    t.send(COMPLETION);
    assert.equal(await t.read(), '800000');
  });
  const seen = [];
  const result = await run(terminal, { command: 'payment', amountMinor: 1250, onIntermediate: (s) => seen.push(s.code) });
  assert.deepEqual(seen, [0x0a, 0x0e]);
  assert.deepEqual(result, {
    status: 'approved',
    uncertain: false,
    error: null,
    resultCode: 0,
    resultText: 'TEST',
    amountMinor: 1250,
    traceNumber: '000123',
    receiptNumber: 42,
    terminalId: '52500021',
    cardName: 'girocard',
    maskedPan: '476173******0010',
    authCode: '123456',
    applicationId: 'A000',
    date: '1111',
    time: '143015',
    customerReceipt: 'Kundenbeleg\n\nZahlung erfolgt',
    merchantReceipt: 'Haendlerbeleg\nEUR 12,50\nZahlung erfolgt',
    intermediate: [
      { code: 0x0a, text: 'Bitte Karte einstecken' },
      { code: 0x0e, text: 'Vorgang wird bearbeitet, bitte warten' },
    ],
  });
});

test('payment declined: status information with an error code, then abort 06 1E', async () => {
  const terminal = await fakeTerminal(async (t) => {
    await acceptRegistration(t);
    await t.read();
    t.send(ACK, apdu(0x04, 0x0f, bytes('27 78 0B 00 00 09')));
    assert.equal(await t.read(), '800000');
    t.send(apdu(0x06, 0x1e, bytes('78')));
    assert.equal(await t.read(), '800000');
  });
  const result = await run(terminal, { command: 'payment', amountMinor: 500 });
  assert.equal(result.status, 'declined');
  assert.equal(result.uncertain, false);
  assert.equal(result.resultCode, 0x78);
  assert.equal(result.resultText, 'Karte verfallen (0x78)');
  assert.equal(result.traceNumber, '000009');
});

test('the cashier aborts while the terminal waits for the card: 06 B0, the terminal answers 06 1E 6C', async () => {
  let session = null;
  const terminal = await fakeTerminal(async (t) => {
    await acceptRegistration(t);
    await t.read();
    t.send(ACK, apdu(0x04, 0xff, bytes('0a')));
    assert.equal(await t.read(), '800000');
    assert.equal(session.latest().code, 0x0a);
    assert.equal(session.abort(), true);
    assert.equal(await t.read(), '06b000');
    t.send(ACK, apdu(0x06, 0x1e, bytes('6c')));
    assert.equal(await t.read(), '800000');
  });
  const result = await run(terminal, { command: 'payment', amountMinor: 500, onSession: (s) => (session = s ?? session) });
  assert.equal(result.status, 'aborted');
  assert.equal(result.uncertain, false);
  assert.equal(result.resultCode, 0x6c);
});

test('connection lost after the authorization was sent: failed and UNCERTAIN (the terminal may have booked it)', async () => {
  const terminal = await fakeTerminal(async (t) => {
    await acceptRegistration(t);
    await t.read();
    t.send(ACK, apdu(0x04, 0xff, bytes('0e')));
    await t.read();
    t.close();
  });
  const result = await run(terminal, { command: 'payment', amountMinor: 500 });
  assert.equal(result.status, 'failed');
  assert.equal(result.uncertain, true);
  assert.match(result.error, /connection/);
});

test('terminal silent after the authorization: idle timeout, failed and uncertain', async () => {
  const terminal = await fakeTerminal(async (t) => {
    await acceptRegistration(t);
    await t.read();
    t.send(ACK);
    await new Promise((resolve) => setTimeout(resolve, 1500));
  });
  const result = await run(terminal, { command: 'payment', amountMinor: 500, idleTimeoutMs: 300 });
  assert.equal(result.status, 'failed');
  assert.equal(result.uncertain, true);
});

test('terminal not reachable: failed, not uncertain (nothing was sent)', async () => {
  const terminal = await fakeTerminal(() => undefined);
  const port = terminal.port;
  await terminal.close();
  const result = await runZvt({ host: '127.0.0.1', port, command: 'payment', amountMinor: 500, ...fast });
  assert.equal(result.status, 'failed');
  assert.equal(result.uncertain, false);
  assert.match(result.error, /not reachable/);
});

test('registration refused with a negative acknowledge (e.g. wrong password): failed, command never sent', async () => {
  const terminal = await fakeTerminal(async (t) => {
    assert.equal(await t.read(), '060006' + '111111' + '8e' + '0978');
    t.send(bytes('84 83 00'));
  });
  const result = await run(terminal, { command: 'payment', amountMinor: 500, password: '111111' });
  assert.equal(result.status, 'failed');
  assert.equal(result.uncertain, false);
  assert.equal(result.resultCode, 0x83);
});

test('reversal sends the receipt number; refund the amount; end of day the password; replies arrive in odd chunks', async () => {
  const reversalTerminal = await fakeTerminal(async (t) => {
    await acceptRegistration(t, '060006' + '123456' + '8e' + '0978');
    assert.equal(await t.read(), '063006' + '123456' + '87' + '0042');
    const reply = Buffer.concat([ACK, apdu(0x04, 0x0f, bytes('27 00 87 00 43 0B 00 01 24')), COMPLETION]);
    for (let i = 0; i < reply.length; i += 2) {
      t.send(reply.subarray(i, i + 2));
      await new Promise((resolve) => setTimeout(resolve, 2));
    }
    assert.equal(await t.read(), '800000');
    assert.equal(await t.read(), '800000');
  });
  const reversed = await run(reversalTerminal, { command: 'reversal', receiptNumber: 42, password: '123456' });
  assert.equal(reversed.status, 'approved');
  assert.equal(reversed.receiptNumber, 43);
  assert.equal(reversed.traceNumber, '000124');

  const refundTerminal = await fakeTerminal(async (t) => {
    await acceptRegistration(t);
    assert.equal(await t.read(), '06310d' + '000000' + '04' + '000000000999' + '49' + '0978');
    t.send(ACK, apdu(0x04, 0x0f, bytes('27 00')), COMPLETION);
  });
  assert.equal((await run(refundTerminal, { command: 'refund', amountMinor: 999 })).status, 'approved');

  const eodTerminal = await fakeTerminal(async (t) => {
    await acceptRegistration(t);
    assert.equal(await t.read(), '065003' + '000000');
    t.send(ACK, apdu(0x06, 0xd3, bytes('06 0A 1F 07 01 03 25 04 07 02 5A 31')), COMPLETION);
  });
  const eod = await run(eodTerminal, { command: 'end-of-day' });
  assert.equal(eod.status, 'approved');
  assert.equal(eod.uncertain, false);
});

test('status = registration only', async () => {
  const terminal = await fakeTerminal(async (t) => {
    await acceptRegistration(t);
  });
  const result = await run(terminal, { command: 'status' });
  assert.equal(result.status, 'approved');
  assert.equal(result.resultText, 'Terminal erreichbar und angemeldet');
});

test('invalid input is rejected before any connection is made', async () => {
  await assert.rejects(() => runZvt({ host: '127.0.0.1', port: 1, command: 'payment', amountMinor: -5 }), RangeError);
  await assert.rejects(() => runZvt({ host: '127.0.0.1', port: 1, command: 'nonsense' }), RangeError);
});

// ------------------------------------------------------------------------------------------------ bridge HTTP contract

async function withBridge(config, fn) {
  const server = createBridge({ origins: new Set(['http://till.test']), printers: new Set(), terminals: new Set(), token: '', zvtConfigByte: 0x8e, zvtIdleTimeoutMs: 800, ...config });
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  const post = (path, body, headers = {}) =>
    fetch(base + path, { method: 'POST', headers: { 'Content-Type': 'application/json', Origin: 'http://till.test', ...headers }, body: typeof body === 'string' ? body : JSON.stringify(body) });
  try {
    await fn({ base, post });
  } finally {
    await new Promise((resolve) => server.close(resolve));
  }
}

test('bridge: origin allow-list, terminal allow-list, token, validation', async () => {
  await withBridge({ terminals: new Set(['10.0.0.5:20007']), token: 'secret', zvt: async () => ({ status: 'approved' }) }, async ({ base, post }) => {
    const foreign = await post('/zvt/payment', { host: '10.0.0.5', amountMinor: 100 }, { Origin: 'http://evil.test' });
    assert.equal(foreign.status, 403);
    assert.equal(foreign.headers.get('access-control-allow-origin'), null);

    const preflight = await fetch(base + '/zvt/payment', { method: 'OPTIONS', headers: { Origin: 'http://till.test' } });
    assert.equal(preflight.status, 204);
    assert.equal(preflight.headers.get('access-control-allow-origin'), 'http://till.test');
    assert.equal(preflight.headers.get('access-control-allow-private-network'), 'true');

    assert.equal((await post('/zvt/payment', { host: '10.0.0.5', amountMinor: 100 })).status, 401);
    const auth = { Authorization: 'Bearer secret' };
    const relay = await post('/zvt/payment', { host: '192.168.1.1', port: 22, amountMinor: 100 }, auth);
    assert.equal(relay.status, 403); // not an open TCP relay
    assert.match((await relay.json()).error, /ALLOWED_TERMINALS/);
    assert.equal((await post('/zvt/payment', { host: '10.0.0.5', amountMinor: 12.5 }, auth)).status, 400);
    assert.equal((await post('/zvt/payment', { host: '10.0.0.5', amountMinor: 100, password: 'abc' }, auth)).status, 400);
    assert.equal((await post('/zvt/reversal', { host: '10.0.0.5' }, auth)).status, 400);
    assert.equal((await post('/zvt/payment', '{nope', auth)).status, 400);
    assert.equal((await post('/zvt/unknown', {}, auth)).status, 404);
    const ok = await post('/zvt/payment', { host: '10.0.0.5', amountMinor: 100 }, auth);
    assert.equal(ok.status, 200);
    assert.deepEqual(await ok.json(), { ok: true, result: { status: 'approved' } });
    const status = await (await fetch(base + '/status')).json();
    assert.deepEqual(status, { ok: true, printers: [], terminals: ['10.0.0.5:20007'] });
  });
});

test('bridge → fake terminal: payment over HTTP, busy terminal (409), progress and abort', async () => {
  let release;
  const waitingForCard = new Promise((resolve) => (release = resolve));
  const terminal = await fakeTerminal(async (t) => {
    await acceptRegistration(t);
    assert.equal(await t.read(), '06010a' + '04' + '000000002000' + '49' + '0978');
    t.send(ACK, apdu(0x04, 0xff, bytes('0a')));
    assert.equal(await t.read(), '800000');
    release();
    assert.equal(await t.read(), '06b000');
    t.send(ACK, apdu(0x06, 0x1e, bytes('6c')));
  });
  const target = { host: '127.0.0.1', port: terminal.port };
  try {
    await withBridge({ terminals: new Set([`127.0.0.1:${terminal.port}`]) }, async ({ post }) => {
      const payment = post('/zvt/payment', { ...target, amountMinor: 2000 });
      await waitingForCard;
      const busy = await post('/zvt/payment', { ...target, amountMinor: 1 });
      assert.equal(busy.status, 409);
      assert.deepEqual(await (await post('/zvt/status', target)).json(), { ok: true, busy: true, intermediate: { code: 0x0a, text: 'Bitte Karte einstecken' } });
      assert.deepEqual(await (await post('/zvt/abort', target)).json(), { ok: true, aborted: true });
      const body = await (await payment).json();
      assert.equal(body.ok, true);
      assert.equal(body.result.status, 'aborted');
      assert.equal(body.result.resultCode, 0x6c);
      // nothing is running any more
      assert.deepEqual(await (await post('/zvt/abort', target)).json(), { ok: true, aborted: false });
    });
    if (terminal.state.error) throw terminal.state.error;
  } finally {
    await terminal.close();
  }
});

test('bridge: /print still forwards raw bytes only to allowed printers', async () => {
  const received = [];
  const printer = createServer((socket) => socket.on('data', (c) => received.push(c)));
  await new Promise((resolve) => printer.listen(0, '127.0.0.1', resolve));
  const port = printer.address().port;
  try {
    await withBridge({ printers: new Set([`127.0.0.1:${port}`]) }, async ({ base }) => {
      const headers = { Origin: 'http://till.test', 'Content-Type': 'application/octet-stream', 'X-Printer-Host': '127.0.0.1' };
      const ok = await fetch(base + '/print', { method: 'POST', headers: { ...headers, 'X-Printer-Port': String(port) }, body: Buffer.from([0x1b, 0x40, 0x41]) });
      assert.equal(ok.status, 200);
      const denied = await fetch(base + '/print', { method: 'POST', headers: { ...headers, 'X-Printer-Port': '9100' }, body: Buffer.from([0x41]) });
      assert.equal(denied.status, 403);
    });
    await new Promise((resolve) => setTimeout(resolve, 50));
    assert.equal(hex(Buffer.concat(received)), '1b4041');
  } finally {
    await new Promise((resolve) => printer.close(resolve));
  }
});
