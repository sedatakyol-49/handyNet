import { ChangeDetectionStrategy, Component, ElementRef, computed, inject, input, output, signal, viewChild } from '@angular/core';
import { I18nService } from '../../core/i18n/i18n.service';
import { TranslatePipe } from '../../core/i18n/translate.pipe';

export interface MultiSelectOption {
  value: string;
  label: string;
  /** Facet count; omitted = no count shown. */
  count?: number | null;
  /** Secondary text, e.g. the seller's city. */
  hint?: string | null;
}

/**
 * Dropdown with checkboxes (facet filters). The trigger is a button (label it with `<label for="{id}">`); the panel is a
 * group of native checkboxes, so Tab/Space work as usual. Esc closes and returns focus to the trigger; a filter input
 * appears for long lists. Selected values that are no longer in the options stay visible (count 0).
 */
@Component({
  selector: 'hn-multi-select',
  imports: [TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'relative block', '(focusout)': 'onFocusOut($event)', '(keydown.escape)': 'onEscape($event)' },
  template: `
    <button
      #trigger
      type="button"
      class="hn-control flex items-center justify-between gap-2 text-start"
      [id]="controlId()"
      aria-haspopup="true"
      [attr.aria-expanded]="open()"
      [attr.aria-controls]="controlId() + '-panel'"
      [attr.data-testid]="testId()"
      (click)="toggle()"
    >
      <span class="truncate" [class.text-slate-500]="selected().length === 0">{{ summary() }}</span>
      <span aria-hidden="true" class="text-xs text-slate-500" [class.rotate-180]="open()">▾</span>
    </button>
    @if (open()) {
      <div
        [id]="controlId() + '-panel'"
        role="group"
        tabindex="-1"
        [attr.aria-label]="label()"
        class="absolute start-0 z-30 mt-1 max-h-80 w-max max-w-[min(26rem,calc(100vw-2rem))] min-w-full overflow-auto rounded-md border border-slate-200 bg-white p-1 shadow-lg"
      >
        @if (allOptions().length > 8) {
          <div class="p-1">
            <input
              #filterBox
              class="hn-control py-1 text-sm"
              type="search"
              autocomplete="off"
              [attr.aria-label]="'common.filterOptions' | t"
              [placeholder]="'common.filterOptions' | t"
              [value]="filter()"
              (input)="filter.set($any($event.target).value)"
            />
          </div>
        }
        @for (o of visible(); track o.value) {
          <label class="flex cursor-pointer items-center gap-2 rounded px-2 py-1 text-sm hover:bg-slate-100 has-checked:bg-accent-50 has-checked:hover:bg-accent-100 has-focus-visible:outline-2 has-focus-visible:-outline-offset-2 has-focus-visible:outline-accent-600">
            <input type="checkbox" class="hn-checkbox" [checked]="isSelected(o.value)" (change)="setValue(o.value, $any($event.target).checked)" />
            <span class="min-w-0 flex-1 break-words">
              {{ o.label }}
              @if (o.hint) {
                <span class="text-xs text-slate-500">· {{ o.hint }}</span>
              }
            </span>
            @if (o.count !== undefined && o.count !== null) {
              <span class="text-xs text-slate-500 tabular-nums">{{ o.count }}</span>
            }
          </label>
        } @empty {
          <p class="px-2 py-2 text-sm text-slate-500">{{ 'common.noOptions' | t }}</p>
        }
        @if (selected().length > 0) {
          <div class="border-t border-slate-100 p-1">
            <button type="button" class="rounded px-2 py-1 text-xs text-accent-700 underline hover:bg-accent-50 hover:text-accent-800" (click)="clear()">
              {{ 'common.clearSelection' | t }}
            </button>
          </div>
        }
      </div>
    }
  `,
})
export class MultiSelectComponent {
  private readonly i18n = inject(I18nService);
  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly trigger = viewChild.required<ElementRef<HTMLButtonElement>>('trigger');

  readonly controlId = input.required<string>();
  readonly label = input.required<string>();
  readonly options = input<MultiSelectOption[]>([]);
  readonly selected = input<string[]>([]);
  readonly placeholder = input<string>('');
  readonly testId = input<string | null>(null);
  readonly selectedChange = output<string[]>();

  protected readonly open = signal(false);
  protected readonly filter = signal('');

  /** Options plus selected values that the facets no longer list (count 0). */
  protected readonly allOptions = computed<MultiSelectOption[]>(() => {
    const opts = this.options();
    const known = new Set(opts.map((o) => o.value));
    const missing = this.selected()
      .filter((v) => !known.has(v))
      .map((v) => ({ value: v, label: v, count: 0 }));
    return [...missing, ...opts];
  });

  protected readonly visible = computed(() => {
    const f = this.filter().trim().toLowerCase();
    return f ? this.allOptions().filter((o) => o.label.toLowerCase().includes(f)) : this.allOptions();
  });

  protected readonly summary = computed(() => {
    const sel = this.selected();
    if (sel.length === 0) return this.placeholder() || this.i18n.t('common.all');
    if (sel.length === 1) return this.allOptions().find((o) => o.value === sel[0])?.label ?? sel[0];
    return this.i18n.t('common.selectedCount', { count: sel.length });
  });

  protected isSelected(value: string): boolean {
    return this.selected().includes(value);
  }

  protected toggle(): void {
    this.open.update((o) => !o);
    if (!this.open()) this.filter.set('');
  }

  protected setValue(value: string, on: boolean): void {
    const current = this.selected();
    const next = on ? (current.includes(value) ? current : [...current, value]) : current.filter((v) => v !== value);
    this.selectedChange.emit(next);
  }

  protected clear(): void {
    this.selectedChange.emit([]);
  }

  protected onEscape(event: Event): void {
    if (!this.open()) return;
    event.stopPropagation(); // do not close a surrounding dialog
    event.preventDefault();
    this.open.set(false);
    this.filter.set('');
    this.trigger().nativeElement.focus();
  }

  protected onFocusOut(event: FocusEvent): void {
    const next = event.relatedTarget as Node | null;
    if (next && (this.host.nativeElement as HTMLElement).contains(next)) return;
    // Focus left the component (Tab away or click elsewhere): close.
    this.open.set(false);
    this.filter.set('');
  }
}
