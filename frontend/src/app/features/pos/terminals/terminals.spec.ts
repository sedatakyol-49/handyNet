import { parseOrdersState } from '../pos.logic';
import {
  DEVICE_MANUAL,
  OrderCard,
  afterSaleFailure,
  cardAmount,
  cardLabel,
  cardStep,
  effectiveTerminal,
  isOrderCard,
  orderCardFrom,
  outcomeMessageKey,
  receiptCardLines,
  reversalReceiptNumber,
  reversible,
  withCardTransaction,
} from './card-flow';
import { TerminalProfile } from './terminal-api';
import { emptyTerminalForm, formFromTerminal, terminalInput } from './terminal-form';
import {
  TerminalOutcome,
  bridgeBody,
  bridgeEndpoint,
  bridgeProgress,
  callBridge,
  mapBridgeResult,
  maskPan,
  resultRequest,
  simulatedOutcome,
  terminalSummary,
} from './terminal-transports';

function profile(over: Partial<TerminalProfile> = {}): TerminalProfile {
  return {
    id: 't1', registerId: 'r1', name: 'Theke', connection: 'ZvtBridge', host: '192.168.1.60', port: 20007, bridgeUrl: 'http://localhost:9123', zvtPassword: '123456',
    isDefault: true, createdAt: '', updatedAt: '', version: 1, ...over,
  };
}

const approved: TerminalOutcome = {
  status: 'Approved', uncertain: false, errorCode: null, test: false, terminalId: '52500021', traceNumber: '000123', terminalReceiptNumber: '42',
  cardName: 'girocard', maskedPan: '476173******0010', authCode: '123456', resultCode: '0x00', resultText: null,
};

describe('terminal transports', () => {
  it('builds the bridge endpoint from whatever URL the profile holds', () => {
    expect(bridgeEndpoint('http://localhost:9123', 'payment')).toBe('http://localhost:9123/zvt/payment');
    expect(bridgeEndpoint('http://localhost:9123/', 'reversal')).toBe('http://localhost:9123/zvt/reversal');
    expect(bridgeEndpoint('http://localhost:9123/print', 'status')).toBe('http://localhost:9123/zvt/status'); // copied from a printer profile
    expect(bridgeEndpoint('http://localhost:9123/zvt/payment', 'abort')).toBe('http://localhost:9123/zvt/abort');
    expect(bridgeEndpoint(null, 'end-of-day')).toBe('http://localhost:9123/zvt/end-of-day');
  });

  it('sends host, port and password of the profile with the request', () => {
    expect(bridgeBody(profile(), { amountMinor: 1250 })).toEqual({ host: '192.168.1.60', port: 20007, password: '123456', amountMinor: 1250 });
    expect(bridgeBody(profile({ port: null, zvtPassword: null }), { receiptNumber: 7 })).toEqual({ host: '192.168.1.60', port: 20007, password: '000000', receiptNumber: 7 });
  });

  it('never lets more than the first 6 and last 4 digits of a card number through', () => {
    expect(maskPan('4761739001010010')).toBe('476173******0010');
    expect(maskPan('4761 7390 0101 0010')).toBe('476173******0010');
    expect(maskPan('476173******0010')).toBe('476173******0010');
    expect(maskPan('476173XXXXXX0010')).toBe('476173******0010');
    expect(maskPan('12345678')).toBe('********');
    expect(maskPan('')).toBeNull();
    expect(maskPan(null)).toBeNull();
  });

  it('maps an approved bridge result to the outcome and the API request', () => {
    const outcome = mapBridgeResult(
      {
        status: 'approved', uncertain: false, error: null, resultCode: 0, resultText: 'Kein Fehler (0x00)', amountMinor: 1250, traceNumber: '000123', receiptNumber: 42,
        terminalId: '52500021', cardName: 'girocard', maskedPan: '4761739001010010', authCode: '123456', customerReceipt: 'K', merchantReceipt: 'H', intermediate: [],
      },
      'payment',
    );
    expect(outcome).toEqual({
      status: 'Approved', uncertain: false, errorCode: null, test: false, terminalId: '52500021', traceNumber: '000123', terminalReceiptNumber: '42', cardName: 'girocard',
      maskedPan: '476173******0010', authCode: '123456', resultCode: '0x00', resultText: 'Kein Fehler (0x00)', customerReceiptText: 'K', merchantReceiptText: 'H',
    });
    expect(resultRequest(outcome)).toMatchObject({ status: 'Approved', maskedPan: '476173******0010', terminalReceiptNumber: '42', resultText: 'Kein Fehler (0x00)' });
  });

  it('maps declined, aborted, unreachable and uncertain results', () => {
    expect(mapBridgeResult({ status: 'declined', resultCode: 0x78, resultText: 'Karte verfallen (0x78)' }, 'payment')).toMatchObject({ status: 'Declined', uncertain: false, errorCode: null, resultCode: '0x78' });
    expect(mapBridgeResult({ status: 'aborted', resultCode: 0x6c }, 'payment')).toMatchObject({ status: 'Aborted', errorCode: null });
    expect(mapBridgeResult({ status: 'failed', uncertain: false, error: 'terminal not reachable: ECONNREFUSED' }, 'payment')).toMatchObject({ status: 'Failed', uncertain: false, errorCode: 'terminalUnreachable' });
    const lost = mapBridgeResult({ status: 'failed', uncertain: true, error: 'connection closed by the terminal' }, 'payment');
    expect(lost).toMatchObject({ status: 'Failed', uncertain: true, errorCode: 'timeout' });
    expect(resultRequest(lost).resultText).toContain('Ergebnis unbekannt');
    expect(mapBridgeResult({ status: 'failed', uncertain: false, resultCode: 0xa3 }, 'payment')).toMatchObject({ status: 'Failed', errorCode: 'terminal', resultCode: '0xA3' });
  });

  it('treats a malformed bridge answer as a failure — uncertain for money operations', () => {
    expect(mapBridgeResult(null, 'payment')).toMatchObject({ status: 'Failed', uncertain: true, errorCode: 'bridgeError' });
    expect(mapBridgeResult({ status: 'paid' }, 'refund')).toMatchObject({ status: 'Failed', uncertain: true });
    expect(mapBridgeResult('nonsense', 'status')).toMatchObject({ status: 'Failed', uncertain: false });
  });

  it('simulated terminal: fake data marked TEST, a timeout is uncertain', () => {
    const ok = simulatedOutcome('approve', 'Payment', 1250, 41);
    expect(ok).toMatchObject({ status: 'Approved', test: true, cardName: 'TEST girocard', maskedPan: '476173******0010', traceNumber: '000042', terminalReceiptNumber: '0042', uncertain: false });
    expect(ok.customerReceiptText).toContain('TEST');
    expect(ok.customerReceiptText).toContain('EUR 12,50');
    expect(simulatedOutcome('approve', 'Refund', 500, 1).customerReceiptText).toContain('GUTSCHRIFT');
    expect(simulatedOutcome('decline', 'Payment', 1250, 1)).toMatchObject({ status: 'Declined', test: true, uncertain: false });
    expect(simulatedOutcome('abort', 'Payment', 1250, 1)).toMatchObject({ status: 'Aborted', test: true });
    expect(simulatedOutcome('timeout', 'Payment', 1250, 1)).toMatchObject({ status: 'Failed', uncertain: true, errorCode: 'simulated' });
  });

  it('summarises where a ZVT profile connects', () => {
    expect(terminalSummary(profile())).toBe('192.168.1.60:20007 ← http://localhost:9123');
    expect(terminalSummary(profile({ connection: 'Manual' }))).toBe('');
  });
});

describe('callBridge (fetch replaced)', () => {
  const json = (status: number, body: unknown) => Promise.resolve(new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }));

  it('posts JSON to the operation endpoint and maps the result', async () => {
    const calls: { url: string; body: unknown }[] = [];
    const fetchFn = ((url: string, init: RequestInit) => {
      calls.push({ url, body: JSON.parse(String(init.body)) });
      return json(200, { ok: true, result: { status: 'approved', resultCode: 0, receiptNumber: 42 } });
    }) as unknown as typeof fetch;
    const outcome = await callBridge(profile(), 'payment', { amountMinor: 1250 }, fetchFn);
    expect(calls).toEqual([{ url: 'http://localhost:9123/zvt/payment', body: { host: '192.168.1.60', port: 20007, password: '123456', amountMinor: 1250 } }]);
    expect(outcome).toMatchObject({ status: 'Approved', terminalReceiptNumber: '42' });
  });

  it('bridge not running: failed, nothing reached the terminal', async () => {
    const fetchFn = (() => Promise.reject(new TypeError('Failed to fetch'))) as unknown as typeof fetch;
    expect(await callBridge(profile(), 'payment', { amountMinor: 100 }, fetchFn)).toMatchObject({ status: 'Failed', uncertain: false, errorCode: 'bridgeUnreachable' });
  });

  it('bridge answers 403 / 409 / 500', async () => {
    const refused = await callBridge(profile(), 'payment', { amountMinor: 100 }, (() => json(403, { ok: false, error: 'terminal 192.168.1.60:20007 not in ALLOWED_TERMINALS' })) as unknown as typeof fetch);
    expect(refused).toMatchObject({ status: 'Failed', uncertain: false, errorCode: 'bridgeRefused', resultText: 'terminal 192.168.1.60:20007 not in ALLOWED_TERMINALS' });
    const busy = await callBridge(profile(), 'payment', { amountMinor: 100 }, (() => json(409, { ok: false, error: 'busy' })) as unknown as typeof fetch);
    expect(busy).toMatchObject({ errorCode: 'bridgeBusy', uncertain: false });
    const broken = await callBridge(profile(), 'payment', { amountMinor: 100 }, (() => json(500, { ok: false, error: 'bridge error: x' })) as unknown as typeof fetch);
    expect(broken).toMatchObject({ errorCode: 'bridgeError', uncertain: true });
  });

  it('status while a transaction runs gives the terminal\'s progress text; abort reports whether it was asked', async () => {
    const busy = (() => json(200, { ok: true, busy: true, intermediate: { code: 10, text: 'Bitte Karte einstecken' } })) as unknown as typeof fetch;
    expect(await bridgeProgress(profile(), busy)).toBe('Bitte Karte einstecken');
    const idle = (() => json(200, { ok: true, busy: false, result: { status: 'approved' } })) as unknown as typeof fetch;
    expect(await bridgeProgress(profile(), idle)).toBeNull();
    expect(await callBridge(profile(), 'abort', {}, (() => json(200, { ok: true, aborted: true })) as unknown as typeof fetch)).toMatchObject({ status: 'Aborted' });
    expect(await callBridge(profile(), 'abort', {}, (() => json(200, { ok: true, aborted: false })) as unknown as typeof fetch)).toMatchObject({ status: 'Failed' });
  });
});

describe('terminal profile form', () => {
  it('manual and simulated profiles need only a name; ZVT fields are cleared', () => {
    const f = { ...emptyTerminalForm(), name: ' Theke ', connection: 'Simulated' as const, host: '1.2.3.4' };
    expect(terminalInput(f, null)).toEqual({ errors: {}, input: { name: 'Theke', connection: 'Simulated', host: null, port: null, bridgeUrl: null, zvtPassword: null, isDefault: null, version: null } });
    expect(terminalInput({ ...emptyTerminalForm(), name: '' }, null).errors).toEqual({ name: 'pos.terminals.form.errors.name' });
  });

  it('validates the ZVT fields like the API', () => {
    const base = { ...emptyTerminalForm(), name: 'ZVT', connection: 'ZvtBridge' as const };
    expect(terminalInput({ ...base, host: 'http://1.2.3.4', port: '70000', bridgeUrl: 'localhost', zvtPassword: '12ab' }, 3).errors).toEqual({
      host: 'pos.terminals.form.errors.host', port: 'pos.terminals.form.errors.port', bridgeUrl: 'pos.terminals.form.errors.bridgeUrl', zvtPassword: 'pos.terminals.form.errors.password',
    });
    expect(terminalInput({ ...base, host: '192.168.1.60', port: '', zvtPassword: '', isDefault: true }, 3).input).toEqual({
      name: 'ZVT', connection: 'ZvtBridge', host: '192.168.1.60', port: 20007, bridgeUrl: 'http://localhost:9123', zvtPassword: '000000', isDefault: true, version: 3,
    });
  });

  it('round-trips a stored profile', () => {
    expect(terminalInput(formFromTerminal(profile({ isDefault: false })), 1).input).toMatchObject({ host: '192.168.1.60', port: 20007, zvtPassword: '123456', isDefault: null, version: 1 });
  });
});

describe('card payment flow rules', () => {
  const card: OrderCard = orderCardFrom('ct1', 1250, profile(), approved, true);

  it('picks the device\'s terminal: own choice, register default, manual', () => {
    const sim = profile({ id: 't2', name: 'Sim', connection: 'Simulated', isDefault: false });
    const manual = profile({ id: 't3', name: 'Hand', connection: 'Manual', isDefault: false });
    expect(effectiveTerminal([profile(), sim], '')?.id).toBe('t1');
    expect(effectiveTerminal([profile(), sim], 't2')?.id).toBe('t2');
    expect(effectiveTerminal([profile(), sim], 'gone')?.id).toBe('t1'); // deleted profile → register default
    expect(effectiveTerminal([profile(), sim], DEVICE_MANUAL)).toBeNull();
    expect(effectiveTerminal([profile(), manual], 't3')).toBeNull(); // a Manual profile = no terminal run
    expect(effectiveTerminal([], '')).toBeNull();
  });

  it('decides what validating has to do about the card', () => {
    expect(cardStep(null, 0, profile())).toBe('none');
    expect(cardStep(null, 1250, null)).toBe('manual');
    expect(cardStep(null, 1250, profile())).toBe('charge');
    expect(cardStep(null, 1250, profile(), true)).toBe('manual'); // outcome was unknown, cashier checked the terminal
    expect(cardStep(card, 1250, profile())).toBe('reuse');
    expect(cardStep(card, 1000, profile())).toBe('mismatch');
    expect(cardStep(card, 0, null)).toBe('orphan');
  });

  it('puts the transaction on the card payment only', () => {
    const payments = [{ method: 'Cash' as const, amountMinor: 500 }, { method: 'Card' as const, amountMinor: 1250 }];
    expect(cardAmount(payments)).toBe(1250);
    expect(withCardTransaction(payments, 'ct1')).toEqual([{ method: 'Cash', amountMinor: 500 }, { method: 'Card', amountMinor: 1250, cardTransactionId: 'ct1' }]);
    expect(withCardTransaction(payments, null)).toEqual(payments);
  });

  it('after a failed sale: keep the payment when the outcome is unknown, reverse when the API refused', () => {
    expect([0, 500, 502, -1].map(afterSaleFailure)).toEqual(['keep', 'keep', 'keep', 'keep']);
    expect([400, 403, 409, 422].map(afterSaleFailure)).toEqual(['reverse', 'reverse', 'reverse', 'reverse']);
  });

  it('only approved payments without a receipt may be reversed from the till', () => {
    expect(reversible({ status: 'Approved', receiptId: null, kind: 'Payment' })).toBe(true);
    expect(reversible({ status: 'Approved', receiptId: 'r1', kind: 'Payment' })).toBe(false);
    expect(reversible({ status: 'Reversed', receiptId: null, kind: 'Payment' })).toBe(false);
    expect(reversible({ status: 'Approved', receiptId: null, kind: 'Refund' })).toBe(false);
    expect(reversalReceiptNumber('0042')).toBe(42);
    expect(reversalReceiptNumber(' 7 ')).toBe(7);
    expect(reversalReceiptNumber('12345')).toBeNull();
    expect(reversalReceiptNumber(null)).toBeNull();
  });

  it('labels and messages', () => {
    expect(cardLabel(card)).toBe('girocard ····0010');
    expect(cardLabel({ cardName: null, maskedPan: null })).toBe('');
    expect(outcomeMessageKey({ status: 'Declined', uncertain: false, errorCode: null })).toBe('pos.terminals.flow.declined');
    expect(outcomeMessageKey({ status: 'Aborted', uncertain: false, errorCode: null })).toBe('pos.terminals.flow.aborted');
    expect(outcomeMessageKey({ status: 'Failed', uncertain: true, errorCode: 'timeout' })).toBe('pos.terminals.flow.uncertain');
    expect(outcomeMessageKey({ status: 'Failed', uncertain: false, errorCode: 'bridgeUnreachable' })).toBe('pos.terminals.errors.bridgeUnreachable');
  });

  it('an approved payment travels with the parked ticket (reload) and an unreported one keeps its result', () => {
    const unreported = orderCardFrom('ct2', 990, profile(), approved, false);
    expect(unreported.pendingResult).toEqual(approved);
    expect(card.pendingResult).toBeNull();
    expect(isOrderCard(JSON.parse(JSON.stringify(unreported)))).toBe(true);
    expect(isOrderCard({ transactionId: 'x', amountMinor: 0, reported: true })).toBe(false);
    const stored = { nextSeq: 2, activeId: 'o1', orders: [{ id: 'o1', seq: 1, createdAt: 'now', lines: [], payments: [], pending: null, card: unreported, cardManual: false }] };
    expect(parseOrdersState(JSON.parse(JSON.stringify(stored)))?.orders[0].card?.transactionId).toBe('ct2');
    expect(parseOrdersState({ ...stored, orders: [{ ...stored.orders[0], card: { bogus: true } }] })).toBeNull(); // a damaged ticket is dropped, not half-read
  });

  it('prints the terminal data under a card payment, marking test payments', () => {
    const t = (key: string, params?: Record<string, string | number | null | undefined>) => `${key.split('.').pop()}=${Object.values(params ?? {}).join('')}`;
    expect(receiptCardLines({ cardName: 'girocard', maskedPan: '476173******0010', traceNumber: '000123', authCode: '123456', terminalId: '52500021', terminalReceiptNumber: '0042' }, t)).toEqual([
      'girocard ····0010',
      'terminal=52500021 · trace=000123 · receipt=0042 · auth=123456',
    ]);
    expect(receiptCardLines({ cardName: 'TEST girocard', maskedPan: null, traceNumber: null, authCode: null, terminalId: null, terminalReceiptNumber: null, isTest: true }, t)).toEqual(['test=', 'TEST girocard']);
    expect(receiptCardLines(null, t)).toEqual([]);
  });
});
