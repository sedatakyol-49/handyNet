import { Injectable, PLATFORM_ID, inject } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';

/** localStorage wrapper that is a no-op during server-side rendering and tolerates blocked storage. */
@Injectable({ providedIn: 'root' })
export class StorageService {
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));

  get isBrowser(): boolean {
    return this.browser;
  }

  get(key: string): string | null {
    if (!this.browser) return null;
    try {
      return localStorage.getItem(key);
    } catch {
      return null;
    }
  }

  set(key: string, value: string | null): void {
    if (!this.browser) return;
    try {
      if (value === null) localStorage.removeItem(key);
      else localStorage.setItem(key, value);
    } catch {
      /* storage unavailable (private mode / quota) */
    }
  }

  getJson<T>(key: string): T | null {
    const raw = this.get(key);
    if (!raw) return null;
    try {
      return JSON.parse(raw) as T;
    } catch {
      return null;
    }
  }

  setJson(key: string, value: unknown): void {
    this.set(key, value === null || value === undefined ? null : JSON.stringify(value));
  }
}
