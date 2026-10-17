import {
  EURO_DENOMINATIONS,
  MAX_COUNT_PER_DENOMINATION,
  countTotal,
  denominationLabel,
  fromDenominations,
  isNote,
  parseCount,
  piecesCounted,
  setCount,
  stepCount,
  toDenominations,
} from './cash-count';

describe('denomination totals', () => {
  it('knows the 15 euro notes and coins, largest first', () => {
    expect(EURO_DENOMINATIONS).toEqual([50_000, 20_000, 10_000, 5_000, 2_000, 1_000, 500, 200, 100, 50, 20, 10, 5, 2, 1]);
    expect(isNote(500)).toBe(true);
    expect(isNote(200)).toBe(false);
  });

  it('adds up value × count in cents exactly', () => {
    // 2 × 50 € + 3 × 10 € + 4 × 2 € + 7 × 0,10 € + 3 × 0,01 € = 138,73 €
    const counts = { 5_000: 2, 1_000: 3, 200: 4, 10: 7, 1: 3 };
    expect(countTotal(counts)).toBe(13_873);
    expect(piecesCounted(counts)).toBe(19);
    expect(countTotal({})).toBe(0);
    // Counts of unknown values are ignored.
    expect(countTotal({ 300: 5 } as Record<number, number>)).toBe(0);
  });

  it('steps and sets counts within 0..limit and ignores unknown denominations', () => {
    let c = stepCount({}, 2_000, 1);
    c = stepCount(c, 2_000, 1);
    expect(c[2_000]).toBe(2);
    expect(stepCount(c, 2_000, -5)[2_000]).toBe(0);
    expect(setCount(c, 2_000, MAX_COUNT_PER_DENOMINATION + 1)[2_000]).toBe(MAX_COUNT_PER_DENOMINATION);
    expect(setCount(c, 300, 4)).toBe(c);
    expect(setCount(c, 100, 1.5)).toBe(c);
  });

  it('parses typed counts strictly', () => {
    expect(parseCount('')).toBe(0);
    expect(parseCount(' 12 ')).toBe(12);
    expect(parseCount('-1')).toBeNull();
    expect(parseCount('1,5')).toBeNull();
    expect(parseCount('100001')).toBeNull();
  });

  it('maps to the API body (non-zero, largest first) and back', () => {
    const body = toDenominations({ 1: 3, 5_000: 2, 100: 0, 10: 7 });
    expect(body).toEqual([
      { valueMinor: 5_000, count: 2 },
      { valueMinor: 10, count: 7 },
      { valueMinor: 1, count: 3 },
    ]);
    expect(countTotal(fromDenominations(body))).toBe(10_073);
    expect(fromDenominations(null)).toEqual({});
  });

  it('labels notes in euros and small coins in cents', () => {
    expect(denominationLabel(50_000)).toBe('500 €');
    expect(denominationLabel(200)).toBe('2 €');
    expect(denominationLabel(50)).toBe('50 ct');
  });
});
