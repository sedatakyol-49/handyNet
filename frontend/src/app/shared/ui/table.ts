import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { TranslatePipe } from '../../core/i18n/translate.pipe';

/**
 * Card + horizontal scroll container for a dense data table. Project a `<table class="hn-table">`.
 * Shows a slim progress bar while `loading` is true and keeps the previous rows visible.
 */
@Component({
  selector: 'hn-table',
  imports: [TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    <div class="hn-card relative overflow-hidden" [attr.aria-busy]="loading()">
      @if (loading()) {
        <div class="absolute inset-x-0 top-0 z-10 h-0.5 animate-pulse bg-accent-600" role="progressbar" [attr.aria-label]="'common.loading' | t"></div>
      }
      <ng-content select="[toolbar]" />
      <div class="overflow-x-auto" [style.max-height]="scrollY() ? '70vh' : null" tabindex="-1">
        <ng-content />
      </div>
      <ng-content select="[footer]" />
    </div>
  `,
})
export class TableComponent {
  readonly loading = input(false);
  readonly scrollY = input(false);
}
