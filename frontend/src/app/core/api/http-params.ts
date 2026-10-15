import { HttpParams } from '@angular/common/http';

/** Builds query params, skipping null/undefined/empty values. */
export function queryParams(values: object): HttpParams {
  let params = new HttpParams();
  for (const [key, value] of Object.entries(values)) {
    if (value === null || value === undefined || value === '') continue;
    params = params.set(key, String(value));
  }
  return params;
}
