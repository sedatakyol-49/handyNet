import { ChangeDetectionStrategy, Component, ElementRef, HostListener, inject, input, signal } from '@angular/core';
import { TranslatePipe } from '../../core/i18n/translate.pipe';

/**
 * Secondary page actions. From `md` up the projected buttons are shown inline; on a phone they collapse into a "⋯"
 * overflow menu (a dropdown panel with full-width, 44 px items). Put the primary action next to it, outside the menu.
 *
 * `<hn-actions-menu><button hnButton>Export</button><button hnButton>Import</button></hn-actions-menu>`
 */
@Component({
  selector: 'hn-actions-menu',
  imports: [TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'relative inline-flex md:contents', '(keydown.escape)': 'open.set(false)' },
  template: `
    <button
      type="button"
      class="hn-icon-btn size-[44px] border border-slate-300 bg-white md:hidden"
      aria-haspopup="true"
      [attr.aria-expanded]="open()"
      [attr.aria-label]="'common.moreActions' | t"
      [title]="'common.moreActions' | t"
      (click)="open.set(!open())"
      data-testid="actions-menu-toggle"
    >
      <svg class="size-5" viewBox="0 0 20 20" fill="currentColor" aria-hidden="true">
        <circle cx="4" cy="10" r="1.6" /><circle cx="10" cy="10" r="1.6" /><circle cx="16" cy="10" r="1.6" />
      </svg>
    </button>
    <div
      class="hn-actions-panel"
      [class.hn-actions-open]="open()"
      (click)="onPanelClick($event)"
      data-testid="actions-menu-panel"
    >
      <ng-content />
    </div>
  `,
})
export class ActionsMenuComponent {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  protected readonly open = signal(false);

  @HostListener('document:click', ['$event'])
  protected onDocumentClick(event: Event): void {
    if (this.open() && !this.host.nativeElement.contains(event.target as Node)) this.open.set(false);
  }

  /** Choosing an action closes the menu. */
  protected onPanelClick(event: Event): void {
    if ((event.target as HTMLElement).closest('button, a')) this.open.set(false);
  }
}

/**
 * Filter bar: always visible from `md` up; on a phone it is collapsed behind a "Filter" toggle so the list is not
 * pushed below the fold. Wrap the existing filter `<form>`.
 */
@Component({
  selector: 'hn-filter-bar',
  imports: [TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    <button
      type="button"
      class="mb-2 inline-flex min-h-[44px] items-center gap-2 rounded-md border border-slate-300 bg-white px-3 text-sm font-medium text-slate-800 md:hidden"
      [attr.aria-expanded]="open()"
      [attr.aria-controls]="panelId"
      (click)="open.set(!open())"
      data-testid="filter-toggle"
    >
      <svg class="size-4" viewBox="0 0 20 20" fill="currentColor" aria-hidden="true"><path d="M3 4h14l-5.5 6.5V16l-3-1.5v-4L3 4Z" /></svg>
      {{ 'common.filters' | t }}
      @if (activeCount() > 0) {
        <span class="rounded-full bg-accent-700 px-1.5 text-[11px] font-semibold text-white">{{ activeCount() }}</span>
      }
    </button>
    <div [id]="panelId" class="max-md:hidden" [class.max-md:block!]="open()">
      <ng-content />
    </div>
  `,
})
export class FilterBarComponent {
  private static nextId = 0;
  protected readonly panelId = `hn-filters-${FilterBarComponent.nextId++}`;
  /** Number of active filters, shown as a badge on the phone toggle. */
  readonly activeCount = input(0);
  protected readonly open = signal(false);
}
