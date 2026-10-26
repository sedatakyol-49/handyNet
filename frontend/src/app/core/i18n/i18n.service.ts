import { DOCUMENT, Injectable, computed, inject, signal } from '@angular/core';
import { setValidationTranslator } from '../api/api-problem';
import { StorageService } from '../storage.service';
import de from './locales/de.json';

export type Lang = 'de' | 'en' | 'tr' | 'es' | 'ru' | 'ar' | 'fa';

export interface LanguageOption {
  code: Lang;
  label: string;
  dir: 'ltr' | 'rtl';
}

export const LANGUAGES: LanguageOption[] = [
  { code: 'de', label: 'Deutsch', dir: 'ltr' },
  { code: 'en', label: 'English', dir: 'ltr' },
  { code: 'tr', label: 'Türkçe', dir: 'ltr' },
  { code: 'es', label: 'Español', dir: 'ltr' },
  { code: 'ru', label: 'Русский', dir: 'ltr' },
  { code: 'ar', label: 'العربية', dir: 'rtl' },
  { code: 'fa', label: 'فارسی', dir: 'rtl' },
];

type Dictionary = { [key: string]: string | Dictionary };

/** Lazily loaded dictionaries; German is bundled because it is the default and the SSR language. */
const LOADERS: Record<Lang, () => Promise<Dictionary>> = {
  de: () => Promise.resolve(de as Dictionary),
  en: () => import('./locales/en.json').then((m) => m.default as Dictionary),
  tr: () => import('./locales/tr.json').then((m) => m.default as Dictionary),
  es: () => import('./locales/es.json').then((m) => m.default as Dictionary),
  ru: () => import('./locales/ru.json').then((m) => m.default as Dictionary),
  ar: () => import('./locales/ar.json').then((m) => m.default as Dictionary),
  fa: () => import('./locales/fa.json').then((m) => m.default as Dictionary),
};

const STORAGE_KEY = 'hn.lang';

/**
 * Formatting locales for dates and numbers. The business runs in Germany: dates are Gregorian and use Western digits
 * in every language (plain `fa` would format in the Solar Hijri calendar with Persian digits, e.g. "۲۶ مهر ۱۴۰۵",
 * which does not match invoices, receipts and the de-DE money amounts). English uses the European day-month order
 * and the 24-hour clock.
 */
export const FORMAT_LOCALES: Record<Lang, string> = {
  de: 'de-DE',
  en: 'en-GB',
  tr: 'tr-TR',
  es: 'es-ES',
  ru: 'ru-RU',
  ar: 'ar-u-ca-gregory-nu-latn',
  fa: 'fa-u-ca-gregory-nu-latn',
};

export function flatten(dict: Dictionary, prefix = '', out: Record<string, string> = {}): Record<string, string> {
  for (const [k, v] of Object.entries(dict)) {
    const key = prefix ? `${prefix}.${k}` : k;
    if (typeof v === 'string') out[key] = v;
    else flatten(v, key, out);
  }
  return out;
}

/**
 * Minimal signal-based translation layer. `t()` reads signals, so templates and computed()s
 * re-evaluate automatically when the language changes. Missing keys fall back to German, then to the key.
 */
@Injectable({ providedIn: 'root' })
export class I18nService {
  private readonly storage = inject(StorageService);
  private readonly document = inject(DOCUMENT);
  private readonly fallback = flatten(de as Dictionary);
  private readonly messages = signal<Record<string, string>>(this.fallback);

  readonly lang = signal<Lang>('de');
  readonly dir = computed(() => LANGUAGES.find((l) => l.code === this.lang())?.dir ?? 'ltr');
  readonly languages = LANGUAGES;
  /** BCP 47 tag used for date/number formatting (money is always de-DE). See FORMAT_LOCALES. */
  readonly locale = computed(() => FORMAT_LOCALES[this.lang()]);

  constructor() {
    setValidationTranslator((code, params) => (this.has('errors.' + code) ? this.t('errors.' + code, params) : null));
    const saved = this.storage.get(STORAGE_KEY) as Lang | null;
    if (saved && saved !== 'de' && LANGUAGES.some((l) => l.code === saved)) void this.use(saved);
    else this.applyDocumentAttributes();
  }

  async use(lang: Lang): Promise<void> {
    const dict = await LOADERS[lang]();
    this.messages.set({ ...this.fallback, ...flatten(dict) });
    this.lang.set(lang);
    this.storage.set(STORAGE_KEY, lang);
    this.applyDocumentAttributes();
  }

  t(key: string, params?: Record<string, string | number | null | undefined>): string {
    let text = this.messages()[key] ?? key;
    if (params) {
      for (const [name, value] of Object.entries(params)) text = text.split(`{${name}}`).join(String(value ?? ''));
    }
    return text;
  }

  /** Translates `prefix.value` if such a key exists, otherwise returns the raw value (for API enums). */
  enum(prefix: string, value: string | null | undefined): string {
    if (value === null || value === undefined || value === '') return '';
    const key = `${prefix}.${value}`;
    return this.messages()[key] ?? value;
  }

  has(key: string): boolean {
    return key in this.messages();
  }

  private applyDocumentAttributes(): void {
    const html = this.document.documentElement;
    html.setAttribute('lang', this.lang());
    html.setAttribute('dir', this.dir());
  }
}
