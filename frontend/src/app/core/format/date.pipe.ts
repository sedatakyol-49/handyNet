import { Pipe, PipeTransform, inject } from '@angular/core';
import { I18nService } from '../i18n/i18n.service';

/** Locale-aware date/time display: `{{ value | hnDate }}` or `{{ value | hnDate: 'date' }}`. */
@Pipe({ name: 'hnDate', pure: false })
export class HnDatePipe implements PipeTransform {
  private readonly i18n = inject(I18nService);

  transform(value: string | null | undefined, mode: 'datetime' | 'date' = 'datetime'): string {
    if (!value) return '–';
    const d = new Date(value);
    if (Number.isNaN(d.getTime())) return value;
    const options: Intl.DateTimeFormatOptions =
      mode === 'date' ? { dateStyle: 'medium' } : { dateStyle: 'medium', timeStyle: 'short' };
    return new Intl.DateTimeFormat(this.i18n.locale(), options).format(d);
  }
}
