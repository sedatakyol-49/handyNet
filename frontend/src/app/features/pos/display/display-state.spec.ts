import { CartLine } from '../pos.logic';
import { displayDark, displayLang } from './customer-display.page';
import {
  DISPLAY_LIMITS,
  TillSnapshot,
  displayChannelName,
  displayStateBytes,
  displayStateFromTill,
  idleDisplayState,
  isHttpUrl,
  messageFromReceiptText,
  parseDisplayMessage,
  parseDisplayState,
} from './display-state';
import { displayLinkUrl, localDisplayPath } from './pos-display-dialog';
import { pollBackoffMs, pollErrorOutcome } from './public-display.page';

function cartLine(over: Partial<CartLine> = {}): CartLine {
  return { key: 'p1', kind: 'Product', productId: 'p1', imei: null, description: 'Displayschutz', quantity: 2, priceInput: '12,50', discountInput: '', available: 5, taxTreatment: 'Standard19', ...over };
}

function snapshot(over: Partial<TillSnapshot> = {}): TillSnapshot {
  return { sessionOpen: true, screen: 'products', lines: [], locale: 'de', ...over };
}

describe('customer display state (pure reducer from the till)', () => {
  it('is idle without a session and with an empty cart, with the shop text as message', () => {
    expect(displayStateFromTill(snapshot({ sessionOpen: false, lines: [cartLine()] }))).toEqual(idleDisplayState('de'));
    expect(displayStateFromTill(snapshot({ idleMessage: ' Handyshop Mitte \n' }))).toEqual({
      phase: 'idle', lines: [], totalMinor: 0, discountTotalMinor: 0, currency: 'EUR', locale: 'de', message: 'Handyshop Mitte',
    });
  });

  it('shows the cart with integer cents: line totals, discounts and the total', () => {
    const state = displayStateFromTill(
      snapshot({ lines: [cartLine(), cartLine({ key: 'p2', productId: 'p2', description: 'Ladekabel', quantity: 1, priceInput: '9,99', discountInput: '10', discountKind: 'percent' })] }),
    );
    expect(state.phase).toBe('cart');
    expect(state.lines).toEqual([
      { description: 'Displayschutz', quantity: 2, unitPriceMinor: 1250, discountMinor: 0, lineGrossMinor: 2500 },
      { description: 'Ladekabel', quantity: 1, unitPriceMinor: 999, discountMinor: 100, lineGrossMinor: 899 },
    ]);
    expect(state.totalMinor).toBe(3399);
    expect(state.discountTotalMinor).toBe(100);
    expect(state.payment).toBeUndefined();
  });

  it('never carries notes, IMEIs or customer data', () => {
    const state = displayStateFromTill(snapshot({ lines: [cartLine({ note: 'für Frau Meier, Tel. 0170', imei: '356938035643809' })] }));
    const json = JSON.stringify(state);
    expect(json).not.toContain('Meier');
    expect(json).not.toContain('356938035643809');
    expect(Object.keys(state.lines[0]).sort()).toEqual(['description', 'discountMinor', 'lineGrossMinor', 'quantity', 'unitPriceMinor']);
  });

  it('a line without a valid price counts as 0 and does not break the total', () => {
    const state = displayStateFromTill(snapshot({ lines: [cartLine({ priceInput: '' }), cartLine({ key: 'p2', priceInput: '3,00', quantity: 1 })] }));
    expect(state.lines[0]).toMatchObject({ unitPriceMinor: 0, lineGrossMinor: 0 });
    expect(state.totalMinor).toBe(300);
  });

  it('payment phase: due incl. cash rounding, tendered, change and the method', () => {
    const lines = [cartLine({ priceInput: '12,33', quantity: 1 })];
    const cash = displayStateFromTill(snapshot({ screen: 'payment', lines, settlement: { paidMinor: 2000, changeMinor: 765, cashMinor: 2000, cardMinor: 0, roundingMinor: 2 } }));
    expect(cash.phase).toBe('payment');
    expect(cash.payment).toEqual({ dueMinor: 1235, tenderedMinor: 2000, changeMinor: 765, method: 'Cash' });
    const split = displayStateFromTill(snapshot({ screen: 'payment', lines, settlement: { paidMinor: 1233, changeMinor: 0, cashMinor: 233, cardMinor: 1000 } }));
    expect(split.payment).toEqual({ dueMinor: 1233, tenderedMinor: 1233, changeMinor: 0, method: 'Mixed' });
    const none = displayStateFromTill(snapshot({ screen: 'payment', lines, settlement: null }));
    expect(none.payment).toEqual({ dueMinor: 1233, tenderedMinor: 0, changeMinor: 0, method: null });
  });

  it('done phase comes from the issued receipt, with change, the footer text and the e-receipt link', () => {
    const state = displayStateFromTill(
      snapshot({
        screen: 'receipt',
        lines: [],
        receipt: {
          totalGrossMinor: 2500,
          changeMinor: 2500,
          lines: [{ description: 'Displayschutz', quantity: 2, unitPriceMinor: 1250, discountMinor: 0, lineGrossMinor: 2500 }],
          payments: [{ method: 'Cash', amountMinor: 2500, tenderedMinor: 5000 }],
        },
        receiptUrl: 'https://app.example/beleg/abc',
        doneMessage: 'Bis bald!',
      }),
    );
    expect(state).toEqual({
      phase: 'done',
      lines: [{ description: 'Displayschutz', quantity: 2, unitPriceMinor: 1250, discountMinor: 0, lineGrossMinor: 2500 }],
      totalMinor: 2500,
      discountTotalMinor: 0,
      payment: { dueMinor: 2500, tenderedMinor: 5000, changeMinor: 2500, method: 'Cash' },
      receiptUrl: 'https://app.example/beleg/abc',
      message: 'Bis bald!',
      currency: 'EUR',
      locale: 'de',
    });
  });

  it('receipt screen without a receipt falls back to the cart/idle state', () => {
    expect(displayStateFromTill(snapshot({ screen: 'receipt', receipt: null })).phase).toBe('idle');
  });

  it('keeps the newest lines and clips long texts so the state stays far below 16 KiB', () => {
    const lines = Array.from({ length: 200 }, (_, i) => cartLine({ key: 'p' + i, productId: 'p' + i, description: `Artikel ${i} ` + 'x'.repeat(300), quantity: 1, priceInput: '1,00' }));
    const state = displayStateFromTill(snapshot({ lines, idleMessage: 'y'.repeat(1000) }));
    expect(state.lines).toHaveLength(DISPLAY_LIMITS.lines);
    expect(state.lines.at(-1)!.description.startsWith('Artikel 199 ')).toBe(true);
    expect(state.lines[0].description.length).toBe(DISPLAY_LIMITS.description);
    expect(state.totalMinor).toBe(20000); // the total covers all 200 lines
    expect(displayStateBytes(state)).toBeLessThan(DISPLAY_LIMITS.bytes);
  });
});

describe('customer display: parsing what arrives', () => {
  const valid = { phase: 'cart', lines: [{ description: 'A', quantity: 1, unitPriceMinor: 100, discountMinor: 0, lineGrossMinor: 100 }], totalMinor: 100, discountTotalMinor: 0, currency: 'EUR', locale: 'tr' };

  it('accepts a valid state and drops unknown fields', () => {
    expect(parseDisplayState({ ...valid, customerName: 'Meier', lines: [{ ...valid.lines[0], note: 'x' }] })).toEqual(valid);
  });

  it('rejects malformed states (the display keeps its screen)', () => {
    expect(parseDisplayState(null)).toBeNull();
    expect(parseDisplayState('cart')).toBeNull();
    expect(parseDisplayState({ ...valid, phase: 'refund' })).toBeNull();
    expect(parseDisplayState({ ...valid, totalMinor: 1.5 })).toBeNull();
    expect(parseDisplayState({ ...valid, lines: [{ description: 'A', quantity: '1', unitPriceMinor: 1, lineGrossMinor: 1 }] })).toBeNull();
    expect(parseDisplayState({ ...valid, lines: 'none' })).toBeNull();
  });

  it('keeps payment and loyalty only when complete, and only http(s) receipt links', () => {
    const s = parseDisplayState({ ...valid, payment: { dueMinor: 100, tenderedMinor: 200, changeMinor: 100, method: 'Cash' }, loyalty: { points: 5 }, receiptUrl: 'javascript:alert(1)' })!;
    expect(s.payment).toEqual({ dueMinor: 100, tenderedMinor: 200, changeMinor: 100, method: 'Cash' });
    expect(s.loyalty).toBeUndefined();
    expect(s.receiptUrl).toBeUndefined();
    expect(parseDisplayState({ ...valid, receiptUrl: 'https://x.test/beleg/1' })!.receiptUrl).toBe('https://x.test/beleg/1');
    expect(isHttpUrl('ftp://x')).toBe(false);
    expect(isHttpUrl('https://x/' + 'a'.repeat(600))).toBe(false);
  });

  it('channel messages: hello and state', () => {
    expect(displayChannelName('r1')).toBe('hn.pos.display.r1');
    expect(parseDisplayMessage({ type: 'hello' })).toEqual({ type: 'hello' });
    expect(parseDisplayMessage({ type: 'state', state: valid })).toEqual({ type: 'state', state: valid });
    expect(parseDisplayMessage({ type: 'state', state: { phase: 'x' } })).toBeNull();
    expect(parseDisplayMessage({ type: 'other' })).toBeNull();
  });

  it('receipt texts become a short message', () => {
    expect(messageFromReceiptText(' Zeile 1 \n\nZeile 2\nZeile 3\nZeile 4')).toBe('Zeile 1\nZeile 2\nZeile 3');
    expect(messageFromReceiptText('  \n ')).toBeNull();
    expect(messageFromReceiptText(null)).toBeNull();
  });
});

describe('customer display pages: helpers', () => {
  it('theme: the till choice wins over the system', () => {
    expect(displayDark('dark', false)).toBe(true);
    expect(displayDark('light', true)).toBe(false);
    expect(displayDark(null, true)).toBe(true);
    expect(displayDark('system', false)).toBe(false);
  });

  it('language of a state, only when the app has it', () => {
    expect(displayLang('ar')).toBe('ar');
    expect(displayLang('de-DE')).toBe('de');
    expect(displayLang('xx')).toBeNull();
    expect(displayLang(undefined)).toBeNull();
  });

  it('paths and link URLs', () => {
    expect(localDisplayPath('r1')).toBe('/kasse/r1/kundendisplay');
    expect(displayLinkUrl('https://app.example/', '/kundendisplay/tok')).toBe('https://app.example/kundendisplay/tok');
    expect(displayLinkUrl('https://app.example', 'kundendisplay/tok')).toBe('https://app.example/kundendisplay/tok');
  });

  it('remote polling: 404 ends the display, other errors back off up to 15 s', () => {
    expect(pollErrorOutcome(404)).toBe('gone');
    expect(pollErrorOutcome(410)).toBe('gone');
    expect([0, 429, 500, 503].map(pollErrorOutcome)).toEqual(['retry', 'retry', 'retry', 'retry']);
    expect([1, 2, 3, 4, 10].map(pollBackoffMs)).toEqual([2000, 4000, 8000, 15000, 15000]);
  });
});
