import { localIsoDate, shiftDay } from './daily-report.page';

describe('daily report dates', () => {
  it('moves by calendar days across month and year ends', () => {
    expect(shiftDay('2026-10-31', 1)).toBe('2026-11-01');
    expect(shiftDay('2026-01-01', -1)).toBe('2025-12-31');
    expect(shiftDay('2028-02-28', 1)).toBe('2028-02-29');
  });

  it('formats the local calendar day', () => {
    expect(localIsoDate(new Date(2026, 9, 5, 23, 59))).toBe('2026-10-05');
  });
});
