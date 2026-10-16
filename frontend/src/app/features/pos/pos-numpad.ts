import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { TranslatePipe } from '../../core/i18n/translate.pipe';
import { NumpadKey } from './pos.logic';

/**
 * Touch numpad (keys ≥ 44 px). Digits keep the phone-keypad order in every language (dir="ltr").
 * `variant="cart"`: the fourth column changes the quantity (−/+); `variant="payment"`: it adds +10/+20/+50 €.
 */
@Component({
  selector: 'hn-pos-numpad',
  imports: [TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    <div class="grid grid-cols-4 gap-1.5" dir="ltr" role="group" [attr.aria-label]="label()" data-testid="pos-numpad">
      @for (row of rows; track $index) {
        @for (k of row; track k) {
          <button type="button" class="pos-key" [disabled]="disabled()" (click)="key.emit(k)" [attr.data-testid]="'pos-key-' + keyId(k)">
            {{ k }}
          </button>
        }
        @switch ($index) {
          @case (0) {
            @if (variant() === 'cart') {
              <button type="button" class="pos-key pos-key-fn" [disabled]="disabled() || !canStep()" (click)="step.emit(-1)" [attr.aria-label]="'pos.numpad.minus' | t" data-testid="pos-numpad-minus">−</button>
            } @else {
              <button type="button" class="pos-key pos-key-fn" [disabled]="disabled()" (click)="add.emit(1000)" data-testid="pos-numpad-add10">+10</button>
            }
          }
          @case (1) {
            @if (variant() === 'cart') {
              <button type="button" class="pos-key pos-key-fn" [disabled]="disabled() || !canStep()" (click)="step.emit(1)" [attr.aria-label]="'pos.numpad.plus' | t" data-testid="pos-numpad-plus">+</button>
            } @else {
              <button type="button" class="pos-key pos-key-fn" [disabled]="disabled()" (click)="add.emit(2000)" data-testid="pos-numpad-add20">+20</button>
            }
          }
          @case (2) {
            @if (variant() === 'cart') {
              <button type="button" class="pos-key pos-key-fn row-span-2" [disabled]="disabled()" (click)="key.emit('backspace')" [attr.aria-label]="'pos.numpad.backspace' | t" data-testid="pos-numpad-backspace">
                <svg class="mx-auto size-6" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" aria-hidden="true"><path d="M9 5h11v14H9l-6-7z" /><path d="m12 9 5 6m0-6-5 6" /></svg>
              </button>
            } @else {
              <button type="button" class="pos-key pos-key-fn" [disabled]="disabled()" (click)="add.emit(5000)" data-testid="pos-numpad-add50">+50</button>
            }
          }
        }
      }
      <button type="button" class="pos-key pos-key-fn" [disabled]="disabled()" (click)="key.emit('clear')" [attr.aria-label]="'pos.numpad.clear' | t" data-testid="pos-numpad-clear">C</button>
      <button type="button" class="pos-key" [disabled]="disabled()" (click)="key.emit('0')" data-testid="pos-key-0">0</button>
      <button type="button" class="pos-key" [disabled]="disabled() || !decimals()" (click)="key.emit(',')" [attr.aria-label]="'pos.numpad.comma' | t" data-testid="pos-key-comma">,</button>
      @if (variant() === 'payment') {
        <button type="button" class="pos-key pos-key-fn" [disabled]="disabled()" (click)="key.emit('backspace')" [attr.aria-label]="'pos.numpad.backspace' | t" data-testid="pos-numpad-backspace">
          <svg class="mx-auto size-6" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" aria-hidden="true"><path d="M9 5h11v14H9l-6-7z" /><path d="m12 9 5 6m0-6-5 6" /></svg>
        </button>
      }
    </div>
  `,
})
export class PosNumpadComponent {
  readonly variant = input<'cart' | 'payment'>('cart');
  readonly label = input<string>('');
  readonly disabled = input(false);
  /** The comma key (not for quantities). */
  readonly decimals = input(true);
  readonly canStep = input(true);

  readonly key = output<NumpadKey>();
  readonly step = output<number>();
  /** Payment variant: add this many cents to the selected payment. */
  readonly add = output<number>();

  protected readonly rows: NumpadKey[][] = [
    ['1', '2', '3'],
    ['4', '5', '6'],
    ['7', '8', '9'],
  ];

  protected keyId(k: NumpadKey): string {
    return k === ',' ? 'comma' : k;
  }
}
