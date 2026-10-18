import { roundCash, settleMinor } from './pos.logic';

describe('cash rounding (mirrors the API CashRounding)', () => {
  it('rounds to the nearest 5 cents: 1–2 cents down, 3–4 cents up', () => {
    expect([1230, 1231, 1232, 1233, 1234, 1235, 1238].map(roundCash)).toEqual([1230, 1230, 1230, 1235, 1235, 1235, 1240]);
  });

  it('rounds only the cash part on rounding registers and keeps card payments exact', () => {
    const cash = settleMinor(1233, 2000, 0, true);
    expect(cash).toMatchObject({ roundingMinor: 2, changeMinor: 765, remainingMinor: 0, error: null });
    expect(settleMinor(1233, 1233, 0, true)).toMatchObject({ remainingMinor: 2, error: 'insufficient' });
    expect(settleMinor(1232, 1230, 0, true)).toMatchObject({ roundingMinor: -2, changeMinor: 0, error: null });
    expect(settleMinor(1233, 500, 1000, true)).toMatchObject({ roundingMinor: 2, changeMinor: 265, error: null });
    expect(settleMinor(1233, 0, 1233, true)).toMatchObject({ roundingMinor: 0, error: null });
    expect(settleMinor(1233, 2000, 0, false)).toMatchObject({ roundingMinor: 0, changeMinor: 767 });
  });
});
