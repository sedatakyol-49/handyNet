import { ChangeDetectionStrategy, Component, Directive, computed, input } from '@angular/core';

/** Styles native inputs/textarea: `<input hnInput id="email" />` */
@Directive({
  selector: 'input[hnInput], textarea[hnInput]',
  host: { class: 'hn-control' },
})
export class InputDirective {}

/** Styles native selects: `<select hnSelect>` */
@Directive({
  selector: 'select[hnSelect]',
  host: { class: 'hn-control pe-8' },
})
export class SelectDirective {}

/**
 * Label + control + hint + error messages. The control is projected; pass the control's id as `for`
 * and reference `{for}-error` from the control's aria-describedby when it has an error.
 */
@Component({
  selector: 'hn-field',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    <label [attr.for]="for()" class="mb-1 block text-xs font-medium text-slate-700">
      {{ label() }}
      @if (required()) {
        <span class="text-red-700" aria-hidden="true">*</span>
      }
    </label>
    <ng-content />
    @if (hint() && !errorText()) {
      <p class="mt-1 text-xs text-slate-500" [id]="for() + '-hint'">{{ hint() }}</p>
    }
    @if (errorText()) {
      <p class="mt-1 text-xs text-red-700" [id]="for() + '-error'" role="alert">{{ errorText() }}</p>
    }
  `,
})
export class FieldComponent {
  readonly label = input.required<string>();
  readonly for = input.required<string>();
  readonly hint = input<string | null>(null);
  readonly required = input(false);
  readonly errors = input<string[] | string | null>(null);
  protected readonly errorText = computed(() => {
    const e = this.errors();
    return Array.isArray(e) ? e.join(' ') : (e ?? '');
  });
}
