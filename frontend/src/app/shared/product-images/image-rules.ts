/** Client-side mirror of the API's image limits (ADR-0011); the API re-checks (magic bytes, dimensions). */
export const MAX_IMAGE_BYTES = 5 * 1024 * 1024;
export const MAX_IMAGES_PER_PRODUCT = 8;
export const ACCEPTED_IMAGE_TYPES = ['image/jpeg', 'image/png', 'image/webp'];

export type ImageFileProblem = 'type' | 'size' | 'empty';

export function checkImageFile(file: Pick<File, 'type' | 'size'>): ImageFileProblem | null {
  if (!ACCEPTED_IMAGE_TYPES.includes(file.type)) return 'type';
  if (file.size === 0) return 'empty';
  if (file.size > MAX_IMAGE_BYTES) return 'size';
  return null;
}

/** Moves the item at `from` to index `to` (clamped); returns a new array. */
export function moveItem<T>(items: readonly T[], from: number, to: number): T[] {
  const result = [...items];
  if (from < 0 || from >= result.length) return result;
  const target = Math.max(0, Math.min(result.length - 1, to));
  const [item] = result.splice(from, 1);
  result.splice(target, 0, item);
  return result;
}

export function sameOrder(a: readonly string[], b: readonly string[]): boolean {
  return a.length === b.length && a.every((v, i) => v === b[i]);
}
