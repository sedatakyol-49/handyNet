import { HttpErrorResponse } from '@angular/common/http';

/** Normalized RFC 7807 problem returned by the HandyNet API (or synthesized for network errors). */
export interface ApiProblem {
  status: number;
  title: string;
  code: string | null;
  /** Field errors keyed by lower-cased property path, e.g. `organization.postalcode`. */
  errors: Record<string, string[]>;
}

export function isApiProblem(value: unknown): value is ApiProblem {
  return !!value && typeof value === 'object' && 'status' in value && 'title' in value && 'errors' in value;
}

export function normalizeFieldKey(key: string): string {
  return key.replace(/\[(\d+)\]/g, '.$1').toLowerCase();
}

/** Converts any error thrown by HttpClient into an ApiProblem. */
export function toApiProblem(error: unknown): ApiProblem {
  if (isApiProblem(error)) return error;
  if (error instanceof HttpErrorResponse) {
    const body = error.error;
    if (body && typeof body === 'object') {
      const errors: Record<string, string[]> = {};
      const raw = (body as { errors?: Record<string, string[]> }).errors;
      if (raw && typeof raw === 'object') {
        for (const [k, v] of Object.entries(raw)) errors[normalizeFieldKey(k)] = Array.isArray(v) ? v : [String(v)];
      }
      return {
        status: error.status,
        title: (body as { title?: string }).title ?? error.statusText ?? 'Error',
        code: (body as { code?: string }).code ?? null,
        errors,
      };
    }
    if (error.status === 0) {
      return { status: 0, title: '', code: 'network', errors: {} };
    }
    return { status: error.status, title: error.statusText || `HTTP ${error.status}`, code: null, errors: {} };
  }
  return { status: -1, title: error instanceof Error ? error.message : String(error), code: null, errors: {} };
}

/** Returns the messages for a field (matches exact path or the last path segment). */
export function fieldErrors(problem: ApiProblem | null | undefined, field: string): string[] {
  if (!problem) return [];
  const key = field.toLowerCase();
  if (problem.errors[key]) return problem.errors[key];
  const hit = Object.entries(problem.errors).find(([k]) => k.endsWith('.' + key));
  return hit ? hit[1] : [];
}
