import { Receipt, ReceiptDocument } from '../../../core/api/models';
import { EscPos, columns, encodeText, toPrintable, wrap } from './escpos';
import { PrinterProfile } from './printer-api';
import { connectionSummary, emptyPrinterForm, formFromProfile, printerInput, validBridgeUrl, validHost } from './printer-form';
import { parseEposResponse, parseStarResponse, toEposXml, toEscPos, toHtml, toStarEnvelope, toStarRequest } from './printer-render';
import { eposUrl, findBulkOut, hostPort, isMixedContent, starUrl } from './printer-transports';
import { effectiveProfile, germanT, takesCash } from './printer.service';
import { PrintItem, receiptLayout, testPageLayout } from './receipt-layout';

const hex = (bytes: Uint8Array | number[]) => [...bytes].map((b) => b.toString(16).padStart(2, '0')).join(' ');
const contains = (bytes: Uint8Array, seq: number[]) => hex(bytes).includes(hex(seq));
const texts = (items: PrintItem[]) => items.flatMap((i) => (i.kind === 'text' ? [i.text] : []));

const QR = 'V0;client;Kassenbeleg-V1;Beleg^19.00_0.00_0.00_0.00_0.00^19.00:Bar;12;24;2026-10-18T09:30:00.000Z;2026-10-18T09:30:01.000Z;ecdsa-plain-SHA256;unixTime;SIG==;PK==';

function doc(over: Partial<ReceiptDocument> = {}): ReceiptDocument {
  return {
    title: 'Kassenbeleg', receiptNumber: 42, kind: 'Sale', referencedReceiptNumber: null, issuedAt: '2026-10-25T10:15:00Z', currency: 'EUR',
    registerName: 'Kasse 1',
    seller: { name: 'Handy Müller', legalName: 'Handy Müller GmbH', street: 'Königstraße 1', postalCode: '70173', city: 'Stuttgart', countryCode: 'DE', vatId: 'DE123456789' },
    lines: [
      { description: 'Panzerglas für iPhone 15 Pro Max mit Einbauhilfe und Reinigungstuch', imei: null, quantity: 2, unitPriceMinor: 1250, discountMinor: 0, lineGrossMinor: 2500, taxLabel: 'A', note: 'Montiert' },
      { description: 'iPhone 12 gebraucht', imei: '356938035643809', quantity: 1, unitPriceMinor: 29900, discountMinor: 1000, lineGrossMinor: 28900, taxLabel: '§25a' },
    ],
    vat: [{ label: 'A 19 %', rateBasisPoints: 1900, grossMinor: 2500, netMinor: 2101, vatMinor: 399 }],
    totalGrossMinor: 31400,
    payments: [{ method: 'Cash', amountMinor: 31400, tenderedMinor: 35000 }],
    changeMinor: 3600,
    notes: ['Gebrauchtgegenstände/Sonderregelung (§ 25a UStG)'],
    fiscal: {
      status: 'Signed', notice: null, tseSerialNumber: 'SERIAL-1', transactionNumber: 12, signatureCounter: 24, signature: 'A'.repeat(80),
      startedAt: '2026-10-25T10:15:00Z', finishedAt: '2026-10-25T10:15:01Z', isTest: true, clientId: 'client', signatureAlgorithm: 'ecdsa-plain-SHA256',
      qrPayload: QR,
    },
    ...over,
  } as ReceiptDocument;
}

function profile(over: Partial<PrinterProfile> = {}): PrinterProfile {
  return {
    id: 'p1', registerId: 'r1', name: 'Theke', connection: 'EpsonEpos', paperWidthMm: 80, charsPerLine: 48, autoPrint: true, openDrawerOnCash: true,
    copies: 1, cutPaper: true, printQr: true, codepage: 'Cp858', host: '192.168.1.50', port: null, useHttps: false, eposDeviceId: 'local_printer',
    bridgeUrl: null, usbVendorId: null, usbProductId: null, baudRate: null, isDefault: true, createdAt: '', updatedAt: '', version: 1, ...over,
  };
}

describe('ESC/POS encoder', () => {
  it('encodes German umlauts and the euro sign in CP858', () => {
    expect(encodeText('ÄÖÜäöüß€', 'Cp858')).toEqual([0x8e, 0x99, 0x9a, 0x84, 0x94, 0x81, 0xe1, 0xd5]);
    expect(encodeText('§', 'Cp858')).toEqual([0xf5]);
  });

  it('PC437 has umlauts but no euro or section sign: they are transliterated', () => {
    expect(encodeText('ä€', 'Pc437')).toEqual([0x84, 0x45, 0x55, 0x52]);
    expect(toPrintable('§ 25a', 'Pc437')).toBe('Par. 25a');
  });

  it('transliterates other characters and drops control characters', () => {
    expect(toPrintable('Şahin – İstanbul…', 'Cp858')).toBe('Sahin - Istanbul...');
    expect(toPrintable('12,50\u00a0€', 'Cp858')).toBe('12,50 €');
    expect(toPrintable('a\u0007b\nc', 'Cp858')).toBe('ab\nc');
    expect(toPrintable('Привет', 'Cp858')).toBe('??????');
  });

  it('builds init, code page, alignment, bold, size, cut and drawer commands', () => {
    const bytes = new EscPos('Cp858').init().align('center').bold(true).size(2, 2).line('X').cut(3).drawer().build();
    expect(hex(bytes)).toBe('1b 40 1b 74 13 1b 61 01 1b 45 01 1d 21 11 58 0a 1d 56 42 03 1b 70 00 19 fa');
  });

  it('encodes a QR code with GS ( k (model 2, size, error correction, store, print)', () => {
    const e = new EscPos();
    expect(e.qr('ABC', 4)).toBe(true);
    expect(hex(e.build())).toBe(
      '1d 28 6b 04 00 31 41 32 00 1d 28 6b 03 00 31 43 04 1d 28 6b 03 00 31 45 31 1d 28 6b 06 00 31 50 30 41 42 43 1d 28 6b 03 00 31 51 30',
    );
    expect(new EscPos().qr('')).toBe(false);
    expect(new EscPos().qr('Ä')).toBe(false);
  });

  it('stores long QR payloads with a two-byte length', () => {
    const e = new EscPos();
    e.qr('x'.repeat(300));
    expect(contains(e.build(), [0x1d, 0x28, 0x6b, 0x2f, 0x01, 0x31, 0x50, 0x30])).toBe(true); // 303 = 0x012f
  });

  it('wraps words and breaks words longer than a line', () => {
    expect(wrap('Panzerglas für iPhone 15', 10)).toEqual(['Panzerglas', 'für iPhone', '15']);
    expect(wrap('ABCDEFGHIJKL', 5)).toEqual(['ABCDE', 'FGHIJ', 'KL']);
    expect(wrap('', 10)).toEqual(['']);
  });

  it('right-aligns amounts on the last line or on a line of their own', () => {
    expect(columns('Summe', '12,50 €', 20)).toEqual(['Summe        12,50 €']);
    expect(columns('Ein sehr langer Text', '12,50 €', 20)).toEqual(['Ein sehr langer Text', '             12,50 €']);
  });
});

describe('receipt layout', () => {
  it('keeps every line within the characters per line (48 and 32)', () => {
    for (const width of [48, 32, 24]) {
      const layout = receiptLayout(doc(), { width, codepage: 'Cp858', t: germanT, locale: 'de-DE', cashier: 'Murat' });
      for (const item of layout.items) {
        if (item.kind === 'text') expect(item.text.length).toBeLessThanOrEqual(item.size === 2 ? Math.floor(width / 2) : width);
      }
    }
  });

  it('prints the stored document: seller, number, lines, totals, payments, VAT, notes and TSE data with QR', () => {
    const layout = receiptLayout(doc(), {
      width: 48, codepage: 'Cp858', t: germanT, locale: 'de-DE', cashier: 'Murat', copy: 2, headerLines: ['Willkommen'], footerLines: ['Danke!'],
    });
    const lines = texts(layout.items);
    const all = lines.join('\n');
    expect(lines[0]).toBe('KOPIE 2');
    expect(all).toContain('Willkommen');
    expect(all).toContain('Handy Müller GmbH');
    expect(all).toContain('USt-IdNr.: DE123456789');
    expect(lines.some((l) => l.startsWith('Beleg-Nr.') && l.endsWith('42'))).toBe(true);
    expect(lines.some((l) => l.startsWith('Kassierer/in') && l.endsWith('Murat'))).toBe(true);
    expect(all).toContain('IMEI 356938035643809');
    expect(lines.some((l) => l.includes('2 x 12,50 €') && l.endsWith('25,00 € A'))).toBe(true);
    expect(all).toContain('Rabatt -10,00 €');
    expect(lines.some((l) => l.startsWith('Summe') && l.endsWith('314,00 €'))).toBe(true);
    expect(lines.some((l) => l.startsWith('Bar gegeben') && l.endsWith('350,00 €'))).toBe(true);
    expect(lines.some((l) => l.startsWith('Rückgeld') && l.endsWith('36,00 €'))).toBe(true);
    expect(all).toContain('§ 25a UStG');
    expect(all).toContain('Test-TSE - nicht für den Echtbetrieb');
    expect(all).toContain('Signaturzähler: 24');
    expect(all).toContain('Signatur: AAAAAAAAAAAA...AAAAAAAAAAAA');
    expect(all).toContain('Danke!');
    expect(layout.items.find((i) => i.kind === 'qr')).toEqual({ kind: 'qr', data: QR });
  });

  it('marks unsigned receipts and leaves out the QR code when switched off', () => {
    const d = doc({ fiscal: { ...doc().fiscal, status: 'NotSigned', notice: 'TSE nicht angebunden – Testbetrieb', qrPayload: null } });
    const layout = receiptLayout(d, { width: 32, codepage: 'Cp858', t: germanT, locale: 'de-DE', qr: false });
    expect(texts(layout.items)[0]).toBe('TSE nicht angebunden -');
    expect(layout.items.some((i) => i.kind === 'qr')).toBe(false);
  });

  it('uses the code page for column widths (EUR on PC437)', () => {
    const layout = receiptLayout(doc(), { width: 32, codepage: 'Pc437', t: germanT, locale: 'de-DE' });
    const lines = texts(layout.items);
    expect(lines.some((l) => l.startsWith('Summe') && l.endsWith('314,00 EUR'))).toBe(true);
    expect(lines.every((l) => l.length <= 32)).toBe(true);
  });

  it('test page has a ruler of exactly the configured width', () => {
    const layout = testPageLayout({ width: 42, codepage: 'Cp858', t: germanT, profileName: 'Theke', connectionLabel: 'USB', paperWidthMm: 80, printedAt: 'jetzt', qr: true });
    expect(texts(layout.items)).toContain('123456789012345678901234567890123456789012');
    expect(layout.items.some((i) => i.kind === 'qr')).toBe(true);
  });
});

describe('printer languages', () => {
  const layout = receiptLayout(doc(), { width: 48, codepage: 'Cp858', t: germanT, locale: 'de-DE' });

  it('ESC/POS: drawer once at the start, cut after every copy, QR in the stream', () => {
    const bytes = toEscPos(layout, { copies: 2, cut: true, drawer: true, paperWidthMm: 80 });
    expect(hex(bytes.slice(0, 10))).toBe('1b 40 1b 74 13 1b 70 00 19 fa');
    expect(hex(bytes).split('1d 56 42 04').length - 1).toBe(2);
    expect(hex(bytes).split('1b 70 00 19 fa').length - 1).toBe(1);
    expect(contains(bytes, [0x1d, 0x28, 0x6b, 0x03, 0x00, 0x31, 0x51, 0x30])).toBe(true);
  });

  it('ESC/POS drawer-only job', () => {
    expect(hex(toEscPos(null, { copies: 1, cut: true, drawer: true, paperWidthMm: 80 }))).toBe('1b 40 1b 74 13 1b 70 00 19 fa');
  });

  it('ePOS-Print XML: escaped text, alignment, emphasis, QR symbol, cut and pulse', () => {
    const xml = toEposXml(receiptLayout(doc({ title: 'A & <B>' }), { width: 48, codepage: 'Cp858', t: germanT, locale: 'de-DE' }), {
      copies: 1, cut: true, drawer: true, paperWidthMm: 80,
    });
    expect(xml).toContain('<epos-print xmlns="http://www.epson-pos.com/schemas/2011/03/epos-print">');
    expect(xml).toContain('<pulse drawer="drawer_1" time="pulse_100"/>');
    expect(xml).toContain('<text align="center" em="true" dw="false" dh="false">A &amp; &lt;B&gt;&#10;</text>');
    expect(xml).toContain('<symbol type="qrcode_model_2" level="level_m" width="5">');
    expect(xml).toContain('<cut type="feed"/>');
    expect(parseEposResponse('<response success="true" code="" status="251658262"/>')).toEqual({ ok: true, code: null });
    expect(parseEposResponse('<response success="false" code="EPTR_COVER_OPEN" status="0"/>')).toEqual({ ok: false, code: 'EPTR_COVER_OPEN' });
  });

  it('Star WebPRNT: builder elements inside root, escaped into the Request element', () => {
    const request = toStarRequest(layout, { copies: 1, cut: true, drawer: true, paperWidthMm: 58 });
    expect(request.startsWith('<root><initialization/><peripheral channel="1" on="200" off="200"/>')).toBe(true);
    expect(request).toContain('<cutpaper feed="true" type="partial"/>');
    expect(request).toContain('<qrcode model="model2" level="level_m" cell="4">');
    expect(request).toContain('\\x0a</text>');
    const envelope = toStarEnvelope(request);
    expect(envelope).toContain('<Request>&lt;root&gt;&lt;initialization/&gt;');
    expect(parseStarResponse('<Response><success>true</success><code>OK</code></Response>').ok).toBe(true);
    expect(parseStarResponse('<Response><success>false</success><code>PRINTER_OFFLINE</code></Response>')).toEqual({ ok: false, code: 'PRINTER_OFFLINE' });
  });

  it('HTML for the print iframe is sized to the paper and escapes text', () => {
    const html = toHtml(receiptLayout(doc({ title: '<script>' }), { width: 32, codepage: 'Cp858', t: germanT, locale: 'de-DE' }), { paperWidthMm: 58, copies: 1 }, 'Test');
    expect(html).toContain('@page{size:58mm auto');
    expect(html).toContain('&lt;script&gt;');
    expect(html).not.toContain('<script>');
    expect(html).toContain('<svg');
  });
});

describe('transports', () => {
  it('builds printer URLs (IPv6 in brackets, optional port, https)', () => {
    expect(eposUrl(profile())).toBe('http://192.168.1.50/cgi-bin/epos/service.cgi?devid=local_printer&timeout=10000');
    expect(eposUrl(profile({ host: 'fe80::1', port: 8443, useHttps: true }))).toBe('https://[fe80::1]:8443/cgi-bin/epos/service.cgi?devid=local_printer&timeout=10000');
    expect(starUrl(profile({ connection: 'StarWebPrnt', useHttps: true }))).toBe('https://192.168.1.50/StarWebPRNT/SendMessage');
    expect(hostPort('10.0.0.5', 9100)).toBe('10.0.0.5:9100');
  });

  it('detects mixed content (https page → http printer), loopback excepted', () => {
    expect(isMixedContent('https:', 'http://192.168.1.50/cgi-bin/epos/service.cgi')).toBe(true);
    expect(isMixedContent('https:', 'https://192.168.1.50/x')).toBe(false);
    expect(isMixedContent('https:', 'http://localhost:9123/print')).toBe(false);
    expect(isMixedContent('https:', 'http://127.0.0.1:9123/print')).toBe(false);
    expect(isMixedContent('http:', 'http://192.168.1.50/x')).toBe(false);
  });

  it('finds the bulk OUT endpoint, preferring the printer class', () => {
    const device = {
      configuration: {
        interfaces: [
          { interfaceNumber: 0, alternates: [{ alternateSetting: 0, interfaceClass: 255, endpoints: [{ endpointNumber: 3, direction: 'out' as const, type: 'bulk' as const }] }] },
          { interfaceNumber: 1, alternates: [{ alternateSetting: 0, interfaceClass: 7, endpoints: [{ endpointNumber: 1, direction: 'in' as const, type: 'bulk' as const }, { endpointNumber: 2, direction: 'out' as const, type: 'bulk' as const }] }] },
        ],
      },
    };
    expect(findBulkOut(device)).toEqual({ interfaceNumber: 1, alternate: 0, endpoint: 2 });
    expect(findBulkOut({ configuration: null })).toBeNull();
  });
});

describe('profile choice', () => {
  const a = profile({ id: 'a', isDefault: true });
  const b = profile({ id: 'b', isDefault: false });

  it('uses the device choice, else the register default, else the print dialog', () => {
    expect(effectiveProfile([a, b], 'b')).toBe(b);
    expect(effectiveProfile([a, b], '')).toBe(a);
    expect(effectiveProfile([a, b], 'deleted-id')).toBe(a);
    expect(effectiveProfile([a, b], 'browser')).toBeNull();
    expect(effectiveProfile([], '')).toBeNull();
  });

  it('opens the drawer only for cash', () => {
    expect(takesCash({ payments: [{ method: 'Card', amountMinor: 100, tenderedMinor: null }] } as unknown as Receipt)).toBe(false);
    expect(takesCash({ payments: [{ method: 'Cash', amountMinor: 100, tenderedMinor: 100 }] } as unknown as Receipt)).toBe(true);
  });
});

describe('printer form', () => {
  it('validates hosts and bridge URLs like the API', () => {
    expect(validHost('192.168.1.50')).toBe(true);
    expect(validHost('fe80::1')).toBe(true);
    expect(validHost('http://192.168.1.50')).toBe(false);
    expect(validHost('192.168.1.50:80')).toBe(false);
    expect(validBridgeUrl('http://localhost:9123/print')).toBe(true);
    expect(validBridgeUrl('http://u:p@localhost:9123/print')).toBe(false);
    expect(validBridgeUrl('ftp://localhost')).toBe(false);
  });

  it('builds the API body and drops fields of other connection types', () => {
    const f = { ...emptyPrinterForm(), name: ' Theke ', connection: 'EscPosBridge' as const, host: '10.0.0.5', usbVendorId: '04B8' };
    const { input, errors } = printerInput(f, null);
    expect(errors).toEqual({});
    expect(input).toMatchObject({ name: 'Theke', host: '10.0.0.5', port: null, bridgeUrl: 'http://localhost:9123/print', usbVendorId: null, eposDeviceId: null });
  });

  it('reports field errors', () => {
    const { input, errors } = printerInput({ ...emptyPrinterForm(), connection: 'EpsonEpos', charsPerLine: '99', copies: '0', host: 'http://x', usbVendorId: 'zz' }, 3);
    expect(input).toBeNull();
    expect(Object.keys(errors).sort()).toEqual(['charsPerLine', 'copies', 'host', 'name']);
  });

  it('parses hex USB ids and keeps them through a round trip', () => {
    const { input } = printerInput({ ...emptyPrinterForm(), name: 'USB', connection: 'WebUsb', usbVendorId: '04B8', usbProductId: '0x0202' }, 7);
    expect(input).toMatchObject({ usbVendorId: 0x04b8, usbProductId: 0x0202, host: null, version: 7 });
    expect(formFromProfile(profile({ connection: 'WebUsb', usbVendorId: 0x04b8, usbProductId: 0x202 })).usbVendorId).toBe('04B8');
    expect(connectionSummary(profile({ connection: 'WebSerial', usbVendorId: 0x04b8, usbProductId: 0x202, baudRate: 19200 }))).toBe('USB 04B8:0202 · 19200 Bd');
  });
});
