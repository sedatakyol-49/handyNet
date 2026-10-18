import { AdminModerationOffer } from '../../../core/api/models';
import {
  EMPTY_FILTERS,
  MAX_BATCH,
  MAX_REASON_LENGTH,
  bulkTargets,
  chunk,
  mergeRows,
  normalizeReason,
  pageSelection,
  toQuery,
  toggleId,
  togglePage,
  validateReason,
} from './moderation.logic';

const row = (offerId: string, moderation: 'None' | 'Blocked' = 'None'): AdminModerationOffer =>
  ({ offerId, moderation, blocked: moderation === 'Blocked', blockedBy: moderation === 'Blocked' ? 'offer' : null }) as AdminModerationOffer;

describe('selection', () => {
  it('toggles single ids without mutating the input', () => {
    const start = new Set(['a']);
    expect([...toggleId(start, 'b')].sort()).toEqual(['a', 'b']);
    expect([...toggleId(start, 'a')]).toEqual([]);
    expect([...start]).toEqual(['a']);
  });

  it('selects the whole page, then deselects it, keeping other pages', () => {
    const other = new Set(['x']);
    const all = togglePage(other, ['a', 'b']);
    expect([...all].sort()).toEqual(['a', 'b', 'x']);
    expect([...togglePage(all, ['a', 'b'])]).toEqual(['x']);
    // Partly selected page → select the rest.
    expect([...togglePage(new Set(['a']), ['a', 'b'])].sort()).toEqual(['a', 'b']);
    expect([...togglePage(new Set(), [])]).toEqual([]);
  });

  it('reports the header checkbox state', () => {
    expect(pageSelection(new Set(), ['a', 'b'])).toBe('none');
    expect(pageSelection(new Set(['a', 'x']), ['a', 'b'])).toBe('some');
    expect(pageSelection(new Set(['a', 'b']), ['a', 'b'])).toBe('all');
    expect(pageSelection(new Set(['x']), [])).toBe('none');
  });
});

describe('bulk targets', () => {
  it('blocks visible and unblocks blocked rows of the selection', () => {
    const rows = [row('a'), row('b', 'Blocked'), row('c')];
    expect(bulkTargets(new Set(['a', 'b']), rows)).toEqual({ block: ['a'], unblock: ['b'] });
    expect(bulkTargets(new Set(), rows)).toEqual({ block: [], unblock: [] });
  });

  it('keeps selected ids from other pages for both actions (the API skips unchanged offers)', () => {
    expect(bulkTargets(new Set(['z']), [row('a')])).toEqual({ block: ['z'], unblock: ['z'] });
  });

  it('splits large selections into API-sized chunks', () => {
    const ids = Array.from({ length: MAX_BATCH * 2 + 1 }, (_, i) => String(i));
    const parts = chunk(ids);
    expect(parts.map((p) => p.length)).toEqual([MAX_BATCH, MAX_BATCH, 1]);
    expect(parts.flat()).toEqual(ids);
    expect(chunk([])).toEqual([]);
  });

  it('merges updated rows by id', () => {
    const merged = mergeRows([row('a'), row('b')], [row('b', 'Blocked')]);
    expect(merged.map((r) => r.moderation)).toEqual(['None', 'Blocked']);
  });
});

describe('reason', () => {
  it('is optional, trimmed and limited', () => {
    expect(validateReason('')).toBeNull();
    expect(validateReason('x'.repeat(MAX_REASON_LENGTH))).toBeNull();
    expect(validateReason('x'.repeat(MAX_REASON_LENGTH + 1))).toBe('moderation.dialog.reasonTooLong');
    expect(normalizeReason('   ')).toBeNull();
    expect(normalizeReason('  Fälschung ')).toBe('Fälschung');
  });
});

describe('filters', () => {
  it('drops blank fields and maps the blocked select', () => {
    expect(toQuery(EMPTY_FILTERS, 1, 50)).toEqual({
      sellerId: null, brand: null, category: null, status: null, blocked: null, search: null, page: 1, pageSize: 50,
    });
    const q = toQuery({ sellerId: 's1', brand: ' Apple ', category: '', status: 'Published', blocked: 'blocked', search: ' 4012 ' }, 3, 25);
    expect(q).toEqual({ sellerId: 's1', brand: 'Apple', category: null, status: 'Published', blocked: true, search: '4012', page: 3, pageSize: 25 });
    expect(toQuery({ ...EMPTY_FILTERS, blocked: 'visible' }, 1, 50).blocked).toBe(false);
  });
});
