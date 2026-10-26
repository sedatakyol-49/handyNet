import { HttpErrorResponse } from '@angular/common/http';

/** Normalized RFC 7807 problem returned by the HandyNet API (or synthesized for network errors). */
export interface ApiProblem {
  status: number;
  title: string;
  code: string | null;
  /** Field errors keyed by lower-cased property path, e.g. `organization.postalcode`. */
  errors: Record<string, string[]>;
  /** Stable per-message codes (+ parameters) parallel to `errors`; lets the UI translate server validation text. */
  errorCodes?: Record<string, FieldErrorCode[]>;
}

export interface FieldErrorCode {
  code: string | null;
  params?: Record<string, string>;
}

type ValidationTranslator = (code: string, params?: Record<string, string>) => string | null;
let translator: ValidationTranslator | null = null;

/** Registered by I18nService: returns the localized text for `errors.<code>` or null when no such key exists. */
export function setValidationTranslator(fn: ValidationTranslator | null): void {
  translator = fn;
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
      const rawCodes = (body as { errorCodes?: Record<string, FieldErrorCode[]> }).errorCodes;
      const errorCodes: Record<string, FieldErrorCode[]> = {};
      if (rawCodes && typeof rawCodes === 'object') {
        for (const [k, v] of Object.entries(rawCodes)) if (Array.isArray(v)) errorCodes[normalizeFieldKey(k)] = v;
      }
      return {
        status: error.status,
        title: (body as { title?: string }).title ?? error.statusText ?? 'Error',
        code: (body as { code?: string }).code ?? null,
        errors,
        errorCodes,
      };
    }
    if (error.status === 0) {
      return { status: 0, title: '', code: 'network', errors: {} };
    }
    return { status: error.status, title: error.statusText || `HTTP ${error.status}`, code: null, errors: {} };
  }
  return { status: -1, title: error instanceof Error ? error.message : String(error), code: null, errors: {} };
}

/** Returns the messages for a field (exact path or last path segment), localized where a code is known. */
export function fieldErrors(problem: ApiProblem | null | undefined, field: string): string[] {
  if (!problem) return [];
  const key = field.toLowerCase();
  const match = problem.errors[key] ? key : Object.keys(problem.errors).find((k) => k.endsWith('.' + key));
  if (!match) return [];
  const codes = problem.errorCodes?.[match];
  return problem.errors[match].map((text, i) => {
    const c = codes?.[i];
    return (c?.code && translator?.(c.code, c.params)) || text;
  });
}
