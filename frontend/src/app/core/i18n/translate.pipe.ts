import { Pipe, PipeTransform, inject } from '@angular/core';
import { I18nService } from './i18n.service';

/** `{{ 'nav.marketplace' | t }}` / `{{ 'cart.items' | t: { count: 3 } }}` */
@Pipe({ name: 't', pure: false })
export class TranslatePipe implements PipeTransform {
  private readonly i18n = inject(I18nService);

  transform(key: string, params?: Record<string, string | number | null | undefined>): string {
    return this.i18n.t(key, params);
  }
}

/** `{{ order.status | tEnum: 'orderStatus' }}` – translates API enum values, falls back to the raw value. */
@Pipe({ name: 'tEnum', pure: false })
export class TranslateEnumPipe implements PipeTransform {
  private readonly i18n = inject(I18nService);

  transform(value: string | null | undefined, prefix: string): string {
    return this.i18n.enum(prefix, value);
  }
}
