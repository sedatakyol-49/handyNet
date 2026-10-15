import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

export type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger';

const BASE =
  'inline-flex items-center justify-center gap-1.5 rounded-md font-medium whitespace-nowrap transition-colors ' +
  'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent-600 ' +
  'disabled:cursor-not-allowed disabled:opacity-50 aria-disabled:pointer-events-none aria-disabled:opacity-50';

const VARIANTS: Record<ButtonVariant, string> = {
  primary: 'bg-accent-700 text-white shadow-xs hover:bg-accent-800',
  secondary: 'border border-slate-300 bg-white text-slate-800 shadow-xs hover:bg-slate-50',
  ghost: 'text-slate-700 hover:bg-slate-100',
  danger: 'bg-red-700 text-white shadow-xs hover:bg-red-800',
};

const SIZES = { sm: 'px-2 py-1 text-xs', md: 'px-3 py-1.5 text-sm' };

/** `<button hnButton variant="primary" [loading]="saving()">Speichern</button>` */
@Component({
  selector: 'button[hnButton], a[hnButton]',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '[class]': 'classes()',
    '[attr.aria-busy]': 'loading() || null',
  },
  template: `
    @if (loading()) {
      <span class="size-3.5 animate-spin rounded-full border-2 border-current border-r-transparent" aria-hidden="true"></span>
    }
    <ng-content />
  `,
})
export class ButtonComponent {
  readonly variant = input<ButtonVariant>('secondary');
  readonly size = input<'sm' | 'md'>('md');
  readonly loading = input(false);
  protected readonly classes = computed(() => `${BASE} ${VARIANTS[this.variant()]} ${SIZES[this.size()]}`);
}
