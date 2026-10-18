import { ChangeDetectionStrategy, Component, computed, effect, input, output, signal, untracked } from '@angular/core';
import { PosCashierOption } from '../../core/api/models';
import { UI } from '../../shared/ui';
import { initials } from './pos.logic';

export const PIN_MIN = 4;
export const PIN_MAX = 6;

/** Appends a digit to a PIN being typed (max 6), or applies backspace/clear. Pure, for the PIN pad and its tests. */
export function pinInput(current: string, key: string): string {
  if (key === 'clear') return '';
  if (key === 'backspace') return current.slice(0, -1);
  return /^[0-9]$/.test(key) && current.length < PIN_MAX ? current + key : current;
}

/** The PIN may be submitted: 4–6 digits and a member chosen. */
export function canSubmitPin(pin: string, userId: string | null): boolean {
  return !!userId && pin.length >= PIN_MIN && pin.length <= PIN_MAX;
}

export interface PinEntry {
  userId: string;
  pin: string;
}

/**
 * PIN pad (Odoo-style): choose a member, enter the 4–6 digit PIN on soft keys (or the keyboard). Used to switch the
 * cashier on a shared device and for a manager approval. The parent calls the API and passes back busy/error.
 */
@Component({
  selector: 'hn-pos-pin-dialog',
  imports: [...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-dialog [title]="title()" [open]="open()" (closed)="closed.emit()" [testId]="testId()">
      <div (keydown)="onKey($event)">
        @if (hint()) {
          <p class="mb-3 text-sm text-slate-600" data-testid="pos-pin-hint">{{ hint() }}</p>
        }
        @if (!members().length) {
          <p class="rounded-md bg-slate-50 px-3 py-4 text-center text-sm text-slate-600" data-testid="pos-pin-nobody">{{ emptyText() }}</p>
        } @else {
          <ul class="mb-4 grid grid-cols-2 gap-2 sm:grid-cols-3" role="radiogroup" [attr.aria-label]="'pos.cashier.member' | t">
            @for (m of members(); track m.userId) {
              <li>
                <button type="button" role="radio" class="pos-mode flex w-full items-center gap-2 px-2 py-1.5 text-start"
                  [attr.aria-pressed]="selected() === m.userId" [attr.aria-checked]="selected() === m.userId" [disabled]="m.locked || busy()"
                  (click)="choose(m.userId)" [attr.data-testid]="'pos-pin-member'" [attr.data-member]="m.displayName">
                  <span class="grid size-8 shrink-0 place-items-center rounded-full bg-pos-key text-xs font-bold" aria-hidden="true">{{ monogram(m) }}</span>
                  <span class="min-w-0 leading-tight">
                    <span class="block truncate text-sm">{{ m.displayName }}</span>
                    <span class="block truncate text-xs font-normal text-pos-muted">{{ m.locked ? ('pos.cashier.locked' | t) : ('role.' + m.role | t) }}</span>
                  </span>
                </button>
              </li>
            }
          </ul>

          <div class="mx-auto max-w-xs">
            <p class="mb-3 flex h-6 items-center justify-center gap-3" aria-live="polite" data-testid="pos-pin-dots">
              <span class="sr-only">{{ 'pos.cashier.digits' | t: { count: pin().length } }}</span>
              @for (i of slots; track i) {
                <span class="size-3.5 rounded-full border-2" aria-hidden="true"
                  [class]="i < pin().length ? 'border-pos-accent bg-pos-accent' : i < minLength ? 'border-slate-400' : 'border-slate-200'"></span>
              }
            </p>
            @if (error()) {
              <p class="mb-3 text-center text-sm font-semibold text-red-700" role="alert" data-testid="pos-pin-error">{{ error() }}</p>
            }
            <div class="grid grid-cols-3 gap-1.5 tabular-nums" dir="ltr" role="group" [attr.aria-label]="'pos.cashier.pinPad' | t">
              @for (k of keys; track k) {
                <button type="button" class="pos-key" [disabled]="busy() || !selected()" (click)="press(k)" [attr.data-testid]="'pos-pin-key-' + k">{{ k }}</button>
              }
              <button type="button" class="pos-key pos-key-fn" [disabled]="busy()" (click)="press('clear')" [attr.aria-label]="'pos.numpad.clear' | t">C</button>
              <button type="button" class="pos-key" [disabled]="busy() || !selected()" (click)="press('0')" data-testid="pos-pin-key-0">0</button>
              <button type="button" class="pos-key pos-key-fn" [disabled]="busy()" (click)="press('backspace')" [attr.aria-label]="'pos.numpad.backspace' | t">⌫</button>
            </div>
            <button hnButton variant="primary" type="button" class="mt-3 min-h-12 w-full" [disabled]="!ready() || busy()" [loading]="busy()" (click)="submit()"
              data-testid="pos-pin-submit">{{ submitLabel() }}</button>
          </div>
        }
        @if (extraAction()) {
          <div class="mt-4 border-t border-slate-100 pt-3 text-center">
            <ng-content />
          </div>
        }
      </div>
    </hn-dialog>
  `,
})
export class PosPinDialogComponent {
  readonly open = input(false);
  readonly title = input.required<string>();
  readonly hint = input<string | null>(null);
  readonly submitLabel = input.required<string>();
  readonly emptyText = input('');
  readonly members = input<PosCashierOption[]>([]);
  readonly busy = input(false);
  readonly error = input<string | null>(null);
  readonly testId = input<string | null>('pos-pin-dialog');
  /** Shows the projected content below the pad (e.g. "continue as …"). */
  readonly extraAction = input(false);
  readonly entered = output<PinEntry>();
  readonly closed = output<void>();

  protected readonly keys = ['1', '2', '3', '4', '5', '6', '7', '8', '9'];
  protected readonly slots = Array.from({ length: PIN_MAX }, (_, i) => i);
  protected readonly minLength = PIN_MIN;
  protected readonly selected = signal<string | null>(null);
  protected readonly pin = signal('');
  protected readonly ready = computed(() => canSubmitPin(this.pin(), this.selected()));

  constructor() {
    // Fresh entry whenever the dialog opens; the only member is preselected.
    effect(() => {
      const open = this.open();
      const members = this.members();
      untracked(() => {
        if (!open) return;
        this.pin.set('');
        const available = members.filter((m) => !m.locked);
        if (!members.some((m) => m.userId === this.selected())) this.selected.set(available.length === 1 ? available[0].userId : null);
      });
    });
    // A refused PIN: clear the dots for the next try.
    effect(() => {
      if (this.error()) untracked(() => this.pin.set(''));
    });
  }

  protected monogram(m: PosCashierOption): string {
    const [first, ...rest] = m.displayName.split(' ');
    return initials(first ?? '', rest.join(' '));
  }

  protected choose(userId: string): void {
    this.selected.set(userId);
    this.pin.set('');
  }

  protected press(key: string): void {
    this.pin.update((p) => pinInput(p, key));
    if (this.pin().length === PIN_MAX && this.ready()) this.submit();
  }

  protected onKey(e: KeyboardEvent): void {
    if (this.busy() || !this.selected()) return;
    if (/^[0-9]$/.test(e.key)) this.press(e.key);
    else if (e.key === 'Backspace') this.press('backspace');
    else if (e.key === 'Enter' && this.ready()) this.submit();
    else return;
    e.preventDefault();
  }

  protected submit(): void {
    const userId = this.selected();
    if (!userId || !this.ready() || this.busy()) return;
    this.entered.emit({ userId, pin: this.pin() });
  }
}
