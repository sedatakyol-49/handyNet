// ZVT (German card terminal protocol) over TCP for the SEHA bridge — dependency-free, Node 20+.
//
// Implemented from the published ZVT specification ("ZVT-Kassenschnittstelle", PA00P015): APDU framing over TCP
// (no DLE/STX framing, default port 20007), registration 06 00, authorization 06 01, reversal 06 30, refund 06 31,
// end-of-day 06 50, abort 06 B0 (ECR → PT) / 06 1E (PT → ECR), status information 04 0F with BMP parsing, intermediate
// status 04 FF, print line 06 D1, print text block 06 D3, completion 06 0F, positive acknowledge 80 00 00.
//
// NOT VERIFIED ON HARDWARE. The byte layouts are unit-tested against a fake terminal socket (zvt.test.mjs) that was
// written from the same specification, so a misreading of the specification would not be caught. Terminals also differ
// in what they send (receipt order, optional BMPs, TLV usage). Test with the terminal's test mode before real payments.

import { connect as tcpConnect } from 'node:net';

// ------------------------------------------------------------------------------------------------ bytes

export const ACK = Buffer.from([0x80, 0x00, 0x00]);

/** Decimal number → packed BCD of `bytes` bytes (left-padded with zeros). */
export function bcd(value, bytes) {
  const digits = String(value).replace(/\D/g, '');
  if (digits.length > bytes * 2) throw new RangeError(`value ${value} does not fit into ${bytes} BCD bytes`);
  return Buffer.from(digits.padStart(bytes * 2, '0'), 'hex');
}

/** Packed BCD → digit string; non-decimal nibbles stay as hex letters (E = masked PAN digit, F = padding). */
export function fromBcd(buf) {
  return Buffer.from(buf).toString('hex').toUpperCase();
}

function bcdNumber(buf) {
  const digits = fromBcd(buf);
  return /^\d+$/.test(digits) ? Number(digits) : null;
}

/** APDU: class, instruction, length (1 byte; 0xFF + 2 bytes little-endian from 255 bytes on), data. */
export function apdu(cls, instr, data = Buffer.alloc(0)) {
  const body = Buffer.from(data);
  const head = body.length < 0xff ? Buffer.from([cls, instr, body.length]) : Buffer.from([cls, instr, 0xff, body.length & 0xff, body.length >> 8]);
  return Buffer.concat([head, body]);
}

/** Reassembles APDUs from a TCP byte stream (chunks may split or join them). */
export class ApduParser {
  #buffer = Buffer.alloc(0);

  push(chunk) {
    this.#buffer = Buffer.concat([this.#buffer, chunk]);
    const out = [];
    for (;;) {
      const b = this.#buffer;
      if (b.length < 3) break;
      let length = b[2];
      let header = 3;
      if (length === 0xff) {
        if (b.length < 5) break;
        length = b[3] | (b[4] << 8);
        header = 5;
      }
      if (b.length < header + length) break;
      out.push({ cls: b[0], instr: b[1], data: b.subarray(header, header + length) });
      this.#buffer = b.subarray(header + length);
    }
    return out;
  }
}

// ------------------------------------------------------------------------------------------------ commands (ECR → PT)

const EUR = 978;

/** Password: 6 digits as 3 BCD bytes. */
export function passwordBytes(password) {
  const p = String(password ?? '000000');
  if (!/^\d{6}$/.test(p)) throw new RangeError('ZVT password must be 6 digits');
  return bcd(p, 3);
}

/**
 * Registration 06 00: password, config byte, currency. Default config byte 0x8E:
 *   0x02 ECR prints payment receipts, 0x04 ECR prints administration receipts, 0x08 ECR wants intermediate status,
 *   0x80 receipts are sent to the ECR as print lines (06 D1) — so the till gets the receipt texts.
 * Bits 0x10 / 0x20 (ECR controls payment / administration functions, i.e. the terminal's own menus are locked) are
 * not set.
 */
export function registration({ password = '000000', configByte = 0x8e, currency = EUR } = {}) {
  return apdu(0x06, 0x00, Buffer.concat([passwordBytes(password), Buffer.from([configByte & 0xff]), bcd(currency, 2)]));
}

function amountBmp(amountMinor) {
  if (!Number.isSafeInteger(amountMinor) || amountMinor <= 0 || amountMinor > 999_999_999_999) throw new RangeError('amount must be positive integer cents');
  return Buffer.concat([Buffer.from([0x04]), bcd(amountMinor, 6)]);
}

const currencyBmp = (currency) => Buffer.concat([Buffer.from([0x49]), bcd(currency, 2)]);

/** Authorization (payment) 06 01: BMP 04 amount (12 BCD digits, cents), BMP 49 currency. */
export function authorization({ amountMinor, currency = EUR }) {
  return apdu(0x06, 0x01, Buffer.concat([amountBmp(amountMinor), currencyBmp(currency)]));
}

/** Reversal 06 30: password, BMP 87 receipt number (4 BCD digits) of the payment to reverse. */
export function reversal({ password = '000000', receiptNumber }) {
  if (!Number.isInteger(receiptNumber) || receiptNumber < 0 || receiptNumber > 9999) throw new RangeError('receipt number must be 0–9999');
  return apdu(0x06, 0x30, Buffer.concat([passwordBytes(password), Buffer.from([0x87]), bcd(receiptNumber, 2)]));
}

/** Refund 06 31: password, BMP 04 amount, BMP 49 currency. */
export function refund({ password = '000000', amountMinor, currency = EUR }) {
  return apdu(0x06, 0x31, Buffer.concat([passwordBytes(password), amountBmp(amountMinor), currencyBmp(currency)]));
}

/** End of day 06 50: password. */
export function endOfDay({ password = '000000' } = {}) {
  return apdu(0x06, 0x50, passwordBytes(password));
}

/** Abort 06 B0 (ECR → PT), e.g. while the terminal waits for a card. */
export function abortCommand() {
  return apdu(0x06, 0xb0);
}

// ------------------------------------------------------------------------------------------------ text

// CP437 upper half (0x80–0xFF); ZVT texts use it unless another character set was negotiated.
const CP437_HIGH =
  'ÇüéâäàåçêëèïîìÄÅÉæÆôöòûùÿÖÜ¢£¥₧ƒáíóúñÑªº¿⌐¬½¼¡«»░▒▓│┤╡╢╖╕╣║╗╝╜╛┐└┴┬├─┼╞╟╚╔╩╦╠═╬╧╨╤╥╙╘╒╓╫╪┘┌█▄▌▐▀αßΓπΣσµτΦΘΩδ∞φε∩≡±≥≤⌠⌡÷≈°∙·√ⁿ²■ ';

export function cp437(buf) {
  let out = '';
  for (const byte of buf) {
    if (byte === 0) break; // texts may be zero-terminated
    out += byte < 0x80 ? (byte < 0x20 ? ' ' : String.fromCharCode(byte)) : CP437_HIGH[byte - 0x80];
  }
  return out;
}

// ------------------------------------------------------------------------------------------------ BER-TLV (BMP 06)

/** Parses BER-TLV data: [{ tag: 'hex', value: Buffer, children?: [...] }]. Stops silently at malformed data. */
export function parseTlv(buf) {
  const out = [];
  let i = 0;
  while (i < buf.length) {
    const start = i;
    let tag = buf[i++];
    if (tag === 0x00 || tag === 0xff) continue; // padding
    let tagHex = tag.toString(16).padStart(2, '0');
    if ((tag & 0x1f) === 0x1f) {
      do {
        if (i >= buf.length) return out;
        tag = buf[i++];
        tagHex += tag.toString(16).padStart(2, '0');
      } while (tag & 0x80);
    }
    const length = readBerLength(buf, i);
    if (!length) return out;
    i = length.next;
    if (i + length.value > buf.length) return out;
    const value = buf.subarray(i, i + length.value);
    i += length.value;
    const constructed = (buf[start] & 0x20) !== 0;
    out.push({ tag: tagHex.toUpperCase(), value, ...(constructed ? { children: parseTlv(value) } : {}) });
  }
  return out;
}

function readBerLength(buf, i) {
  if (i >= buf.length) return null;
  const first = buf[i];
  if (first < 0x80) return { value: first, next: i + 1 };
  const count = first & 0x7f;
  if (count === 0 || count > 2 || i + count >= buf.length) return null;
  let value = 0;
  for (let k = 1; k <= count; k++) value = (value << 8) | buf[i + k];
  return { value, next: i + 1 + count };
}

function findTlv(nodes, tag) {
  for (const n of nodes) {
    if (n.tag === tag) return n;
    const inner = n.children && findTlv(n.children, tag);
    if (inner) return inner;
  }
  return null;
}

// ------------------------------------------------------------------------------------------------ BMPs (PT → ECR)

// Length of each bitmap: number = fixed bytes, 'LL' / 'LLL' = variable (length digits as 0xFn bytes), 'TLV' = BER-TLV.
const BMP = {
  0x01: 1, 0x02: 1, 0x03: 1, 0x04: 6, 0x05: 1, 0x06: 'TLV', 0x0b: 3, 0x0c: 3, 0x0d: 2, 0x0e: 2, 0x17: 2, 0x19: 1, 0x22: 'LL', 0x23: 'LL',
  0x24: 'LLL', 0x27: 1, 0x29: 4, 0x2a: 15, 0x2d: 'LL', 0x2e: 'LLL', 0x37: 3, 0x3a: 2, 0x3b: 8, 0x3c: 'LLL', 0x3d: 3, 0x49: 2, 0x60: 'LLL',
  0x87: 2, 0x88: 3, 0x8a: 1, 0x8b: 'LL', 0x8c: 1, 0x92: 'LLL', 0x9a: 'LLL', 0xa0: 1, 0xa7: 'LL', 0xaa: 3, 0xaf: 'LLL', 0xba: 5,
  0xd0: 1, 0xd1: 'LL', 0xd2: 1, 0xd3: 1, 0xe0: 1, 0xe1: 'LL', 0xe2: 'LL', 0xe3: 'LL', 0xe4: 'LL', 0xe5: 'LL', 0xe6: 'LL', 0xe7: 'LL',
  0xe8: 'LL', 0xe9: 1, 0xea: 1, 0xeb: 8, 0xf0: 1, 0xf1: 'LL', 0xf2: 'LL', 0xf3: 'LL', 0xf4: 'LL', 0xf5: 'LL', 0xf6: 'LL', 0xf7: 'LL',
  0xf8: 'LL', 0xf9: 1, 0xfa: 1, 0xfb: 1, 0xfc: 1,
};

/** Splits BMP data into { tag → Buffer }. Stops at the first unknown tag (its length is unknown); `rest` holds what was left. */
export function parseBmps(data) {
  const fields = new Map();
  let i = 0;
  while (i < data.length) {
    const tag = data[i];
    const kind = BMP[tag];
    if (kind === undefined) break;
    i++;
    let length;
    if (typeof kind === 'number') length = kind;
    else if (kind === 'TLV') {
      const l = readBerLength(data, i);
      if (!l) break;
      i = l.next;
      length = l.value;
    } else {
      const n = kind === 'LL' ? 2 : 3;
      if (i + n > data.length) break;
      let digits = '';
      for (let k = 0; k < n; k++) digits += (data[i + k] & 0x0f).toString();
      i += n;
      length = Number(digits);
    }
    if (i + length > data.length) break;
    fields.set(tag, data.subarray(i, i + length));
    i += length;
  }
  return { fields, rest: data.subarray(i) };
}

/** Masks a PAN to first 6 + last 4 digits; digits the terminal already masked (nibble E) stay masked. */
export function maskPan(pan) {
  const digits = String(pan ?? '').toUpperCase().replace(/F+$/, '').replace(/[^0-9E*]/g, '').replace(/E/g, '*');
  if (!digits) return null;
  if (digits.length <= 10) return '*'.repeat(digits.length);
  return digits.slice(0, 6) + '*'.repeat(digits.length - 10) + digits.slice(-4);
}

/** Status information 04 0F: the transaction result. */
export function parseStatusInformation(data) {
  const { fields, rest } = parseBmps(data);
  const get = (tag) => fields.get(tag);
  const text = (tag) => (get(tag) ? cp437(get(tag)).trim() || null : null);
  const digits = (tag) => (get(tag) ? fromBcd(get(tag)) : null);
  const tlv = get(0x06) ? parseTlv(get(0x06)) : [];
  const aid = get(0x3b) ?? null;
  const out = {
    resultCode: get(0x27) ? get(0x27)[0] : null,
    amountMinor: get(0x04) ? bcdNumber(get(0x04)) : null,
    traceNumber: digits(0x0b),
    originalTraceNumber: digits(0x37),
    time: digits(0x0c), // HHMMSS
    date: digits(0x0d), // MMDD
    expiry: null, // BMP 0E is deliberately not passed on
    cardSequenceNumber: digits(0x17),
    paymentType: get(0x19) ? get(0x19)[0] : null,
    maskedPan: get(0x22) ? maskPan(fromBcd(get(0x22))) : null,
    terminalId: digits(0x29),
    authCode: aid ? printableOrHex(aid) : null,
    currency: digits(0x49),
    receiptNumber: get(0x87) ? bcdNumber(get(0x87)) : null,
    turnoverNumber: digits(0x88),
    cardType: get(0x8a) ? get(0x8a)[0] : null,
    cardName: text(0x8b),
    vuNumber: text(0x2a),
    additionalText: text(0x3c),
    extendedErrorText: findTlv(tlv, '1F17') ? cp437(findTlv(tlv, '1F17').value).trim() || null : null,
    applicationId: findTlv(tlv, '43') ? findTlv(tlv, '43').value.toString('hex').toUpperCase() : null,
    unparsed: rest.length ? rest.toString('hex') : null,
  };
  return out;
}

function printableOrHex(buf) {
  const trimmed = Buffer.from(buf).filter((b) => b !== 0x00);
  if (!trimmed.length) return null;
  const printable = [...trimmed].every((b) => b >= 0x20 && b < 0x7f);
  return printable ? Buffer.from(trimmed).toString('latin1').trim() || null : Buffer.from(buf).toString('hex').toUpperCase();
}

// Intermediate status 04 FF: what the terminal is doing (texts per the specification's status byte table).
const INTERMEDIATE = {
  0x00: 'Terminal wartet auf Betragsbestätigung',
  0x01: 'Bitte Anzeigen auf dem PIN-Pad beachten',
  0x02: 'Bitte Anzeigen auf dem PIN-Pad beachten',
  0x03: 'Vorgang nicht möglich',
  0x04: 'Terminal wartet auf Antwort des Netzbetreibers',
  0x05: 'Terminal sendet Autostorno',
  0x06: 'Terminal sendet Nachbuchungen',
  0x07: 'Karte nicht zugelassen',
  0x08: 'Karte unbekannt',
  0x09: 'Karte verfallen',
  0x0a: 'Bitte Karte einstecken',
  0x0b: 'Bitte Karte entnehmen',
  0x0c: 'Karte nicht lesbar',
  0x0d: 'Vorgang abgebrochen',
  0x0e: 'Vorgang wird bearbeitet, bitte warten',
  0x0f: 'Terminal leitet einen automatischen Kassenabschluss ein',
  0x10: 'Karte ungültig',
  0x11: 'Guthabenanzeige',
  0x12: 'Systemfehler',
  0x13: 'Zahlung nicht möglich',
  0x14: 'Guthaben nicht ausreichend',
  0x15: 'Geheimzahl falsch',
  0x16: 'Limit nicht ausreichend',
  0x17: 'Bitte warten',
  0x18: 'Geheimzahl zu oft falsch',
  0x19: 'Kartendaten falsch',
  0x1a: 'Servicemodus',
  0x1c: 'Zahlung erfolgt',
  0x1d: 'Autorisierung nicht möglich',
};

export function parseIntermediateStatus(data) {
  const code = data.length ? data[0] : 0xff;
  // After the status byte: optional timeout byte and a TLV container (06 …) that may carry a display text (tag 24 / 07).
  let text = INTERMEDIATE[code] ?? null;
  const tlvStart = data.indexOf(0x06, 1);
  if (!text && tlvStart > 0) {
    const l = readBerLength(data, tlvStart + 1);
    if (l && l.next + l.value <= data.length) {
      const node = findTlv(parseTlv(data.subarray(l.next, l.next + l.value)), '07');
      if (node) text = cp437(node.value).trim() || null;
    }
  }
  return { code, text: text ?? `Terminalstatus 0x${code.toString(16).toUpperCase().padStart(2, '0')}` };
}

/** Print line 06 D1: attribute byte + text. Attribute 0xFn = n line feeds; bit 7 on a text line = last line of a receipt. */
export function parsePrintLine(data) {
  if (!data.length) return { text: '', last: false, feeds: 0 };
  const attribute = data[0];
  if ((attribute & 0xf0) === 0xf0) return { text: '', last: false, feeds: attribute & 0x0f };
  return { text: cp437(data.subarray(1)).replace(/\s+$/, ''), last: (attribute & 0x80) !== 0, feeds: 0 };
}

/** Print text block 06 D3: TLV container with tag 1F07 (receipt type: 1 merchant, 2 customer, 3 administration) and text lines (tag 07). */
export function parsePrintTextBlock(data) {
  let payload = data;
  if (data[0] === 0x06) {
    const l = readBerLength(data, 1);
    if (l) payload = data.subarray(l.next, l.next + l.value);
  }
  const nodes = parseTlv(payload);
  const type = findTlv(nodes, '1F07');
  const lines = [];
  (function collect(list) {
    for (const n of list) {
      if (n.tag === '07') lines.push(cp437(n.value).replace(/\s+$/, ''));
      else if (n.children) collect(n.children);
    }
  })(nodes);
  return { receiptType: type && type.value.length ? type.value[0] : null, lines };
}

// Result codes of status information (BMP 27) and abort 06 1E, per the specification's error table (selection).
const RESULT_TEXT = {
  0x00: 'Kein Fehler',
  0x64: 'Karte nicht lesbar',
  0x65: 'Kartendaten nicht vorhanden',
  0x66: 'Verarbeitungsfehler',
  0x67: 'Funktion für ec-/Maestro-Karten nicht zulässig',
  0x68: 'Funktion für Kredit- und Tankkarten nicht zulässig',
  0x6a: 'Umsatzdatei voll',
  0x6b: 'Funktion deaktiviert (Terminal nicht angemeldet)',
  0x6c: 'Abbruch durch Zeitüberschreitung oder Abbruchtaste',
  0x6e: 'Karte in Sperrliste',
  0x6f: 'Falsche Währung',
  0x71: 'Guthaben nicht ausreichend',
  0x72: 'Chipfehler',
  0x73: 'Kartendaten fehlerhaft',
  0x77: 'Kassenschnitt nicht möglich',
  0x78: 'Karte verfallen',
  0x79: 'Karte noch nicht gültig',
  0x7a: 'Karte unbekannt',
  0x7d: 'Kommunikationsfehler',
  0x83: 'Funktion nicht möglich',
  0x85: 'Schlüssel fehlt',
  0x89: 'PIN-Pad defekt',
  0x9a: 'ZVT-Protokollfehler',
  0x9b: 'Fehler bei der Datenübertragung zum Netzbetreiber',
  0x9c: 'Bitte warten',
  0xa0: 'Empfänger nicht bereit',
  0xa1: 'Gegenstelle antwortet nicht',
  0xa3: 'Keine Verbindung',
  0xb2: 'Speicher voll',
  0xb4: 'Bereits storniert',
  0xb5: 'Storno nicht möglich',
  0xc2: 'Diagnose erforderlich',
  0xc3: 'Maximalbetrag überschritten',
  0xc5: 'Zahlungsart nicht unterstützt',
  0xc8: 'Betrag zu klein',
  0xcc: 'Drucker nicht bereit',
  0xe2: 'Kartenleser antwortet nicht',
  0xe4: 'Terminalaktivierung erforderlich',
  0xec: 'PIN-Verarbeitung nicht möglich',
  0xf0: 'Offener Kassenschnitt vorhanden',
  0xff: 'Systemfehler',
};

// Aborts that are technical faults rather than a refusal of the card (shown as "failed", not "declined").
const TECHNICAL = new Set([0x66, 0x6a, 0x6b, 0x77, 0x7d, 0x83, 0x85, 0x89, 0x9a, 0x9b, 0x9c, 0xa0, 0xa1, 0xa3, 0xb2, 0xc2, 0xcc, 0xe2, 0xe4, 0xf0, 0xff]);

export function resultText(code) {
  if (code === null || code === undefined) return null;
  const hex = `0x${code.toString(16).toUpperCase().padStart(2, '0')}`;
  if (RESULT_TEXT[code]) return `${RESULT_TEXT[code]} (${hex})`;
  if (code >= 0x01 && code <= 0x63) return `Ablehnung durch das Autorisierungssystem (${hex})`;
  return `Terminalfehler ${hex}`;
}

/** approved | declined | aborted | failed for an abort / result code. */
export function classifyResultCode(code) {
  if (code === 0x00) return 'approved';
  if (code === 0x6c) return 'aborted';
  return TECHNICAL.has(code) ? 'failed' : 'declined';
}

// ------------------------------------------------------------------------------------------------ dialogue

const COMMANDS = {
  payment: (o) => authorization({ amountMinor: o.amountMinor }),
  reversal: (o) => reversal({ password: o.password, receiptNumber: o.receiptNumber }),
  refund: (o) => refund({ password: o.password, amountMinor: o.amountMinor }),
  'end-of-day': (o) => endOfDay({ password: o.password }),
  status: () => null, // registration only
};

/** Receipts collected from print lines / text blocks → customer and merchant text. */
export function receiptTexts(blocks) {
  const text = (b) => b.lines.join('\n').replace(/\n{3,}/g, '\n\n').trim();
  const filled = blocks.filter((b) => b.lines.some((l) => l.trim()));
  let customer = filled.find((b) => b.type === 2) ?? null;
  let merchant = filled.find((b) => b.type === 1) ?? null;
  const untyped = filled.filter((b) => b.type === null);
  for (const b of untyped) {
    const t = text(b).toLowerCase();
    if (!customer && /kundenbeleg|karteninhaber|customer/.test(t)) customer = b;
    else if (!merchant && /h(ä|ae)ndlerbeleg|merchant/.test(t)) merchant = b;
  }
  // No marker: a single receipt is the customer's; of two, terminals commonly print the merchant receipt first.
  const rest = untyped.filter((b) => b !== customer && b !== merchant);
  if (!customer && !merchant && rest.length === 1) customer = rest[0];
  else {
    if (!merchant && rest.length && customer !== rest[0] && (customer || rest.length > 1)) merchant = rest.shift();
    if (!customer && rest.length) customer = rest.shift();
  }
  return { customerReceipt: customer ? text(customer).slice(0, 4000) : null, merchantReceipt: merchant ? text(merchant).slice(0, 4000) : null };
}

/**
 * Runs one ZVT dialogue: connect, register, send the command, acknowledge everything the terminal sends until
 * completion (06 0F) or abort (06 1E).
 *
 * Result: { status: 'approved' | 'declined' | 'aborted' | 'failed', uncertain, resultCode, resultText, …status
 * information fields…, customerReceipt, merchantReceipt, intermediate: [{code, text}] }.
 * `uncertain: true` means the connection broke or timed out after a money command was sent and before the terminal
 * answered: the terminal may or may not have booked it — a person must check on the terminal.
 */
export function runZvt(options) {
  const { host, port = 20007, command, password = '000000', configByte = 0x8e } = options;
  const connectTimeoutMs = options.connectTimeoutMs ?? 10_000;
  const ackTimeoutMs = options.ackTimeoutMs ?? 5_000;
  const idleTimeoutMs = options.idleTimeoutMs ?? 180_000; // the customer may take a while at the terminal
  const connect = options.connect ?? ((o) => tcpConnect(o));
  let commandApdu;
  let registrationApdu;
  try {
    // Invalid input is refused before any byte is sent.
    if (!Object.hasOwn(COMMANDS, command)) throw new RangeError(`unknown ZVT command ${command}`);
    commandApdu = COMMANDS[command]({ ...options, password });
    registrationApdu = registration({ password, configByte });
  } catch (e) {
    return Promise.reject(e);
  }
  const moneyCommand = command === 'payment' || command === 'refund' || command === 'reversal';

  return new Promise((resolve) => {
    const parser = new ApduParser();
    const intermediate = [];
    const blocks = [];
    let current = { type: null, lines: [] };
    let info = null;
    // connecting → registering (wait ACK) → registered (wait 06 0F) → sending (wait ACK) → running → done
    let phase = 'connecting';
    let commandSent = false;
    let done = false;
    let timer = null;
    const socket = connect({ host, port });

    const arm = (ms, reason) => {
      clearTimeout(timer);
      timer = setTimeout(() => finish({ status: 'failed', error: reason }), ms);
    };

    const finish = (outcome) => {
      if (done) return;
      done = true;
      clearTimeout(timer);
      if (options.onSession) options.onSession(null);
      socket.destroy();
      if (current.lines.length) blocks.push(current);
      const uncertain = outcome.status === 'failed' && moneyCommand && commandSent && !outcome.terminalAnswered;
      const code = outcome.resultCode ?? info?.resultCode ?? null;
      resolve({
        status: outcome.status,
        uncertain,
        error: outcome.error ?? null,
        resultCode: code,
        resultText: info?.extendedErrorText || outcome.text || resultText(code),
        amountMinor: info?.amountMinor ?? null,
        traceNumber: info?.traceNumber ?? null,
        receiptNumber: info?.receiptNumber ?? null,
        terminalId: info?.terminalId ?? null,
        cardName: info?.cardName ?? null,
        maskedPan: info?.maskedPan ?? null,
        authCode: info?.authCode ?? null,
        applicationId: info?.applicationId ?? null,
        date: info?.date ?? null,
        time: info?.time ?? null,
        ...receiptTexts(blocks),
        intermediate,
      });
    };

    const send = (bytes) => socket.write(bytes);

    const sendCommand = () => {
      if (!commandApdu) return finish({ status: 'approved', terminalAnswered: true, text: 'Terminal erreichbar und angemeldet' });
      phase = 'sending';
      commandSent = true;
      send(commandApdu);
      arm(ackTimeoutMs, 'no acknowledge for the command');
    };

    const onApdu = ({ cls, instr, data }) => {
      const key = (cls << 8) | instr;
      // Positive / negative acknowledge of something we sent.
      if (cls === 0x80 && instr === 0x00) {
        if (phase === 'registering') {
          phase = 'registered';
          arm(idleTimeoutMs, 'terminal did not complete the registration');
        } else if (phase === 'sending') {
          phase = 'running';
          arm(idleTimeoutMs, 'terminal did not finish in time');
        }
        return; // also the ACK for an abort request (06 B0)
      }
      if (cls === 0x84) {
        // Negative acknowledge: the terminal refused the command, nothing was started.
        const during = phase === 'registering' ? 'registration' : 'command';
        return finish({ status: 'failed', terminalAnswered: true, resultCode: instr, text: `${resultText(instr)} – Terminal lehnt ${during === 'registration' ? 'die Anmeldung' : 'den Befehl'} ab`, error: `${during} refused` });
      }
      send(ACK); // every terminal message is acknowledged
      switch (key) {
        case 0x04ff: {
          const s = parseIntermediateStatus(data);
          intermediate.push(s);
          if (intermediate.length > 50) intermediate.shift();
          if (options.onIntermediate) options.onIntermediate(s);
          break;
        }
        case 0x040f:
          if (phase === 'running') info = parseStatusInformation(data);
          break;
        case 0x06d1: {
          const line = parsePrintLine(data);
          for (let k = 0; k < line.feeds; k++) current.lines.push('');
          if (!line.feeds) current.lines.push(line.text);
          if (line.last) {
            blocks.push(current);
            current = { type: null, lines: [] };
          }
          break;
        }
        case 0x06d3: {
          const block = parsePrintTextBlock(data);
          blocks.push({ type: block.receiptType, lines: block.lines });
          break;
        }
        case 0x060f:
          if (phase === 'registered') return sendCommand();
          if (phase === 'running') {
            // Completion = the command was executed. A status information with an error code still wins.
            const code = info?.resultCode ?? 0x00;
            return finish({ status: classifyResultCode(code), terminalAnswered: true, resultCode: code });
          }
          break;
        case 0x061e: {
          const code = data.length ? data[0] : (info?.resultCode ?? 0xff);
          if (phase === 'registered' || phase === 'registering') return finish({ status: 'failed', terminalAnswered: true, resultCode: code, error: 'registration aborted' });
          const status = classifyResultCode(code === 0x00 ? 0xff : code);
          return finish({ status, terminalAnswered: true, resultCode: code });
        }
        default:
          break; // unknown message: acknowledged and ignored
      }
      if (phase === 'running' || phase === 'registered') arm(idleTimeoutMs, 'terminal did not finish in time');
    };

    arm(connectTimeoutMs, 'terminal not reachable (connect timeout)');
    socket.on('connect', () => {
      phase = 'registering';
      if (options.onSession) {
        options.onSession({
          abort: () => {
            if (!done && phase === 'running') send(abortCommand());
            return !done && phase === 'running';
          },
          latest: () => intermediate.at(-1) ?? null,
        });
      }
      send(registrationApdu);
      arm(ackTimeoutMs, 'no acknowledge for the registration');
    });
    socket.on('data', (chunk) => {
      for (const message of parser.push(chunk)) {
        if (done) return;
        onApdu(message);
      }
    });
    socket.on('error', (e) => finish({ status: 'failed', error: phase === 'connecting' ? `terminal not reachable: ${e.message}` : `connection lost: ${e.message}` }));
    socket.on('close', () => finish({ status: 'failed', error: phase === 'connecting' ? 'terminal not reachable' : 'connection closed by the terminal' }));
  });
}
