import { AdminModerationOffer, AdminModerationQuery, OfferStatus } from '../../../core/api/models';

/** Same limits as the API (OfferModeration.MaxReasonLength / MaxBatchSize). */
export const MAX_REASON_LENGTH = 500;
export const MAX_BATCH = 500;

export type BlockedFilter = '' | 'blocked' | 'visible';

export interface ModerationFilters {
  sellerId: string;
  brand: string;
  category: string;
  status: OfferStatus | '';
  blocked: BlockedFilter;
  search: string;
}

export const EMPTY_FILTERS: ModerationFilters = { sellerId: '', brand: '', category: '', status: '', blocked: '', search: '' };

/** API query for the filter form: blank fields are left out, the blocked select maps to true/false. */
export function toQuery(f: ModerationFilters, page: number, pageSize: number): AdminModerationQuery {
  const text = (v: string) => (v.trim() ? v.trim() : null);
  return {
    sellerId: f.sellerId || null,
    brand: text(f.brand),
    category: text(f.category),
    status: f.status || null,
    blocked: f.blocked === 'blocked' ? true : f.blocked === 'visible' ? false : null,
    search: text(f.search),
    page,
    pageSize,
  };
}

/** Adds or removes one id. */
export function toggleId(selected: ReadonlySet<string>, id: string): Set<string> {
  const next = new Set(selected);
  if (next.has(id)) next.delete(id);
  else next.add(id);
  return next;
}

/** Header checkbox: selects every row of the page unless all are selected already (then deselects them). */
export function togglePage(selected: ReadonlySet<string>, pageIds: readonly string[]): Set<string> {
  const next = new Set(selected);
  const all = pageIds.length > 0 && pageIds.every((id) => next.has(id));
  for (const id of pageIds) {
    if (all) next.delete(id);
    else next.add(id);
  }
  return next;
}

/** State of the header checkbox for the current page. */
export function pageSelection(selected: ReadonlySet<string>, pageIds: readonly string[]): 'none' | 'some' | 'all' {
  const count = pageIds.filter((id) => selected.has(id)).length;
  if (count === 0) return 'none';
  return count === pageIds.length ? 'all' : 'some';
}

/**
 * Splits the selection into offers a bulk "block" or "unblock" would change. Selected ids not on the current page are
 * kept as they are (the API ignores offers already in the target state), so a cross-page selection still works.
 */
export function bulkTargets(
  selected: ReadonlySet<string>,
  pageRows: readonly Pick<AdminModerationOffer, 'offerId' | 'moderation'>[],
): { block: string[]; unblock: string[] } {
  const byId = new Map(pageRows.map((r) => [r.offerId, r]));
  const block: string[] = [];
  const unblock: string[] = [];
  for (const id of selected) {
    const row = byId.get(id);
    if (!row) {
      block.push(id);
      unblock.push(id);
    } else if (row.moderation === 'Blocked') unblock.push(id);
    else block.push(id);
  }
  return { block, unblock };
}

/** Splits ids into API-sized chunks (at most {@link MAX_BATCH} per call). */
export function chunk<T>(ids: readonly T[], size = MAX_BATCH): T[][] {
  const out: T[][] = [];
  for (let i = 0; i < ids.length; i += size) out.push(ids.slice(i, i + size));
  return out;
}

/** i18n key of a reason validation error, or null. The reason itself is optional. */
export function validateReason(reason: string): string | null {
  return reason.trim().length > MAX_REASON_LENGTH ? 'moderation.dialog.reasonTooLong' : null;
}

/** Trimmed reason or null (the API stores null for an empty reason). */
export function normalizeReason(reason: string): string | null {
  const trimmed = reason.trim();
  return trimmed ? trimmed : null;
}

/** Replaces rows by id (after a block/unblock returned the updated offers). */
export function mergeRows(rows: readonly AdminModerationOffer[], updated: readonly AdminModerationOffer[]): AdminModerationOffer[] {
  const byId = new Map(updated.map((u) => [u.offerId, u]));
  return rows.map((r) => byId.get(r.offerId) ?? r);
}
