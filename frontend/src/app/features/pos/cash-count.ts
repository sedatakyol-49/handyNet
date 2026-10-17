import { DenominationCount } from '../../core/api/models';

/** Euro notes and coins in cents, largest first (API `CashCount.EuroDenominations`). */
export const EURO_NOTES: readonly number[] = [50_000, 20_000, 10_000, 5_000, 2_000, 1_000, 500];
export const EURO_COINS: readonly number[] = [200, 100, 50, 20, 10, 5, 2, 1];
export const EURO_DENOMINATIONS: readonly number[] = [...EURO_NOTES, ...EURO_COINS];

/** API limit per denomination. */
export const MAX_COUNT_PER_DENOMINATION = 100_000;

/** Counted pieces per denomination value (cents → count). Missing = 0. */
export type CashCounts = Readonly<Record<number, number>>;

/** "12" → 12; empty → 0; null for anything else (negative, decimals, above the API limit). */
export function parseCount(input: string | null | undefined): number | null {
  const s = (input ?? '').trim();
  if (s === '') return 0;
  if (!/^\d{1,6}$/.test(s)) return null;
  const n = Number(s);
  return n > MAX_COUNT_PER_DENOMINATION ? null : n;
}

/** Σ value × count in cents (integers only). */
export function countTotal(counts: CashCounts): number {
  let total = 0;
  for (const value of EURO_DENOMINATIONS) total += value * (counts[value] ?? 0);
  return total;
}

/** Sets one denomination, clamped to 0..limit; unknown values are ignored. */
export function setCount(counts: CashCounts, valueMinor: number, count: number): CashCounts {
  if (!EURO_DENOMINATIONS.includes(valueMinor) || !Number.isInteger(count)) return counts;
  const c = Math.min(Math.max(count, 0), MAX_COUNT_PER_DENOMINATION);
  return { ...counts, [valueMinor]: c };
}

export function stepCount(counts: CashCounts, valueMinor: number, delta: number): CashCounts {
  return setCount(counts, valueMinor, (counts[valueMinor] ?? 0) + delta);
}

/** Request body: non-zero counts, largest denomination first (each value once, like the API normalizes). */
export function toDenominations(counts: CashCounts): DenominationCount[] {
  return EURO_DENOMINATIONS.filter((v) => (counts[v] ?? 0) > 0).map((v) => ({ valueMinor: v, count: counts[v] }));
}

export function fromDenominations(list: readonly DenominationCount[] | null | undefined): CashCounts {
  const out: Record<number, number> = {};
  for (const d of list ?? []) if (EURO_DENOMINATIONS.includes(d.valueMinor)) out[d.valueMinor] = d.count;
  return out;
}

/** Notes "50 €", coins "2 €" / "50 ct" (labels are the same in every language). */
export function denominationLabel(valueMinor: number): string {
  return valueMinor >= 100 ? `${valueMinor / 100} €` : `${valueMinor} ct`;
}

export function isNote(valueMinor: number): boolean {
  return valueMinor >= 500;
}

export function piecesCounted(counts: CashCounts): number {
  return EURO_DENOMINATIONS.reduce((n, v) => n + (counts[v] ?? 0), 0);
}
