import { HttpParams } from '@angular/common/http';

/**
 * Builds query params, skipping null/undefined/empty values. Arrays become repeated parameters
 * (`brand=Apple&brand=Samsung`), which is how the API binds list filters.
 */
export function queryParams(values: object): HttpParams {
  let params = new HttpParams();
  for (const [key, value] of Object.entries(values)) {
    if (Array.isArray(value)) {
      for (const item of value) {
        if (item === null || item === undefined || item === '') continue;
        params = params.append(key, String(item));
      }
      continue;
    }
    if (value === null || value === undefined || value === '') continue;
    params = params.set(key, String(value));
  }
  return params;
}
