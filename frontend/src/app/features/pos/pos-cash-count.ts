import { ChangeDetectionStrategy, Component, computed, input, model } from '@angular/core';
import { UI } from '../../shared/ui';
import { CashCounts, EURO_COINS, EURO_NOTES, MAX_COUNT_PER_DENOMINATION, countTotal, denominationLabel, parseCount, setCount, stepCount } from './cash-count';

/**
 * Cash count by denomination (opening / closing of a session): one row per euro note and coin with −/+ buttons and a
 * count field, the subtotal per row and the live total. Digits keep their order in RTL languages (dir="ltr" cells).
 */
@Component({
  selector: 'hn-pos-cash-count',
  imports: [...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block @container', '[attr.data-testid]': 'testId()' },
  template: `
    <div class="grid gap-3 @2xl:grid-cols-2" role="group" [attr.aria-label]="'pos.cashCount.label' | t">
      @for (group of groups; track group.key) {
        <fieldset class="min-w-0">
          <legend class="mb-1 text-xs font-semibold tracking-wide text-slate-500 uppercase">{{ 'pos.cashCount.' + group.key | t }}</legend>
          <ul class="space-y-1">
            @for (v of group.values; track v) {
              @let n = counts()[v] ?? 0;
              <li class="grid grid-cols-[4rem_auto_minmax(3.5rem,1fr)_auto_minmax(5.5rem,auto)] items-center gap-1.5" [attr.data-denomination]="v">
                <label [for]="idPrefix() + v" class="text-sm font-semibold tabular-nums" dir="ltr">{{ label(v) }}</label>
                <button type="button" class="pos-icon-btn border border-slate-200" [disabled]="!n" (click)="step(v, -1)"
                  [attr.aria-label]="'pos.cashCount.minus' | t: { value: label(v) }" data-testid="pos-count-minus">−</button>
                <input [id]="idPrefix() + v" class="hn-control h-11 min-w-0 text-center text-base tabular-nums" inputmode="numeric" autocomplete="off" dir="ltr"
                  [value]="n || ''" placeholder="0" (input)="type(v, $any($event.target))" (focus)="$any($event.target).select()" data-testid="pos-count-input" />
                <button type="button" class="pos-icon-btn border border-slate-200" (click)="step(v, 1)"
                  [attr.aria-label]="'pos.cashCount.plus' | t: { value: label(v) }" data-testid="pos-count-plus">+</button>
                <span class="text-end text-sm tabular-nums" [class.text-slate-400]="!n">{{ v * n | money }}</span>
              </li>
            }
          </ul>
        </fieldset>
      }
    </div>
    <p class="mt-3 flex items-baseline justify-between border-t border-slate-200 pt-2 text-base font-semibold" role="status" aria-live="polite">
      <span>{{ 'pos.cashCount.total' | t }}</span>
      <span class="text-xl tabular-nums" data-testid="pos-count-total">{{ total() | money }}</span>
    </p>
  `,
})
export class PosCashCountComponent {
  readonly counts = model<CashCounts>({});
  readonly idPrefix = input('pos-count-');
  readonly testId = input('pos-cash-count');

  protected readonly groups = [
    { key: 'notes', values: EURO_NOTES },
    { key: 'coins', values: EURO_COINS },
  ];
  protected readonly total = computed(() => countTotal(this.counts()));
  protected readonly label = denominationLabel;

  protected step(value: number, delta: number): void {
    this.counts.set(stepCount(this.counts(), value, delta));
  }

  protected type(value: number, el: HTMLInputElement): void {
    const digits = el.value.replace(/\D/g, '').slice(0, String(MAX_COUNT_PER_DENOMINATION).length);
    if (digits !== el.value) el.value = digits;
    const n = parseCount(digits);
    if (n !== null) this.counts.set(setCount(this.counts(), value, n));
  }
}
