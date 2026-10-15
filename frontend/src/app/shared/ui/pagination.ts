import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { TranslatePipe } from '../../core/i18n/translate.pipe';
import { ButtonComponent } from './button';

@Component({
  selector: 'hn-pagination',
  imports: [TranslatePipe, ButtonComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (totalCount() > 0) {
      <nav class="flex flex-wrap items-center justify-between gap-2 px-1 py-2 text-xs text-slate-600" [attr.aria-label]="'pagination.label' | t">
        <span>{{ 'pagination.range' | t: { from: from(), to: to(), total: totalCount() } }}</span>
        <span class="flex items-center gap-1">
          <button hnButton size="sm" type="button" [disabled]="page() <= 1" (click)="pageChange.emit(page() - 1)">
            {{ 'pagination.previous' | t }}
          </button>
          <span class="px-2">{{ 'pagination.page' | t: { page: page(), pages: pages() } }}</span>
          <button hnButton size="sm" type="button" [disabled]="page() >= pages()" (click)="pageChange.emit(page() + 1)">
            {{ 'pagination.next' | t }}
          </button>
        </span>
      </nav>
    }
  `,
})
export class PaginationComponent {
  readonly page = input.required<number>();
  readonly pageSize = input.required<number>();
  readonly totalCount = input.required<number>();
  readonly pageChange = output<number>();

  protected readonly pages = computed(() => Math.max(1, Math.ceil(this.totalCount() / this.pageSize())));
  protected readonly from = computed(() => (this.page() - 1) * this.pageSize() + 1);
  protected readonly to = computed(() => Math.min(this.totalCount(), this.page() * this.pageSize()));
}
