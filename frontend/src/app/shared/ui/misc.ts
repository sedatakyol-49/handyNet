import { ChangeDetectionStrategy, Component, ElementRef, computed, effect, inject, input, output, viewChild } from '@angular/core';
import { ApiProblem, toApiProblem } from '../../core/api/api-problem';
import { I18nService } from '../../core/i18n/i18n.service';
import { TranslatePipe } from '../../core/i18n/translate.pipe';
import { ToastService, describeProblem } from '../../core/ui-state/toast.service';

/** Page title row with optional description and projected actions. */
@Component({
  selector: 'hn-page-header',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'mb-4 flex flex-wrap items-end justify-between gap-3' },
  template: `
    <div class="min-w-0">
      <h1 class="text-xl">{{ title() }}</h1>
      @if (description()) {
        <p class="mt-0.5 text-sm text-slate-500">{{ description() }}</p>
      }
    </div>
    <div class="flex flex-wrap items-center gap-2"><ng-content /></div>
  `,
})
export class PageHeaderComponent {
  readonly title = input.required<string>();
  readonly description = input<string | null>(null);
}

@Component({
  selector: 'hn-empty-state',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    <div class="px-4 py-10 text-center">
      <p class="text-sm font-medium text-slate-800">{{ title() }}</p>
      @if (text()) {
        <p class="mt-1 text-sm text-slate-500">{{ text() }}</p>
      }
      <div class="mt-3 flex justify-center gap-2"><ng-content /></div>
    </div>
  `,
})
export class EmptyStateComponent {
  readonly title = input.required<string>();
  readonly text = input<string | null>(null);
}

/** Inline error/warning/info banner. Pass an API error via `error` (shows the problem title). */
@Component({
  selector: 'hn-banner',
  imports: [TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    @if (visible()) {
      <div [class]="classes()" [attr.role]="tone() === 'danger' ? 'alert' : 'status'">
        <div class="min-w-0 flex-1">
          @if (heading()) {
            <p class="font-semibold">{{ heading() }}</p>
          }
          @if (message()) {
            <p>{{ message() }}</p>
          }
          <ng-content />
        </div>
        @if (dismissible()) {
          <button type="button" class="rounded px-1 text-current/70 hover:text-current" (click)="dismissed.emit()" [attr.aria-label]="'common.close' | t">
            ✕
          </button>
        }
      </div>
    }
  `,
})
export class BannerComponent {
  private readonly i18n = inject(I18nService);
  readonly tone = input<'danger' | 'warning' | 'info' | 'success'>('danger');
  readonly heading = input<string | null>(null);
  readonly text = input<string | null>(null);
  readonly error = input<unknown>(null);
  readonly dismissible = input(false);
  readonly dismissed = output<void>();

  protected readonly message = computed(() => {
    const err = this.error();
    if (err) return describeProblem(this.i18n, toApiProblem(err));
    return this.text();
  });
  protected readonly visible = computed(() => !!(this.message() || this.heading()));
  protected readonly classes = computed(() => {
    const tones = {
      danger: 'border-red-200 bg-red-50 text-red-900',
      warning: 'border-amber-200 bg-amber-50 text-amber-900',
      info: 'border-blue-200 bg-blue-50 text-blue-900',
      success: 'border-emerald-200 bg-emerald-50 text-emerald-900',
    };
    return `flex items-start gap-3 rounded-md border px-3 py-2 text-sm ${tones[this.tone()]}`;
  });
}

@Component({
  selector: 'hn-toast-host',
  imports: [TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="pointer-events-none fixed inset-x-0 bottom-0 z-50 flex flex-col items-center gap-2 p-4 sm:items-end" aria-live="polite">
      @for (toast of toasts.toasts(); track toast.id) {
        <div
          class="pointer-events-auto flex w-full max-w-sm items-start gap-3 rounded-md border px-3 py-2 text-sm shadow-md"
          [class]="
            toast.kind === 'error'
              ? 'border-red-200 bg-red-50 text-red-900'
              : toast.kind === 'success'
                ? 'border-emerald-200 bg-white text-emerald-900'
                : 'border-slate-200 bg-white text-slate-800'
          "
          [attr.role]="toast.kind === 'error' ? 'alert' : 'status'"
        >
          <p class="flex-1">{{ toast.text }}</p>
          <button type="button" class="px-1 opacity-70 hover:opacity-100" (click)="toasts.dismiss(toast.id)" [attr.aria-label]="'common.close' | t">✕</button>
        </div>
      }
    </div>
  `,
})
export class ToastHostComponent {
  protected readonly toasts = inject(ToastService);
}

/** Native <dialog> wrapper (focus trap, Esc to close and backdrop are provided by the browser). */
@Component({
  selector: 'hn-dialog',
  imports: [TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <dialog
      #dialog
      class="m-auto max-h-[calc(100dvh-2rem)] overflow-y-auto rounded-lg border border-slate-200 bg-white p-0 text-slate-900 shadow-xl backdrop:bg-slate-900/40"
      [class]="widthClass()"
      [attr.aria-labelledby]="titleId"
      [attr.data-testid]="testId()"
      (close)="closed.emit()"
    >
      <div class="flex items-center justify-between border-b border-slate-200 px-4 py-3">
        <h2 class="text-base" [id]="titleId">{{ title() }}</h2>
        <button type="button" class="rounded px-1 text-slate-500 hover:text-slate-900" (click)="closed.emit()" [attr.aria-label]="'common.close' | t">✕</button>
      </div>
      <div class="px-4 py-4"><ng-content /></div>
    </dialog>
  `,
})
export class DialogComponent {
  private static nextId = 0;
  protected readonly titleId = `hn-dialog-${DialogComponent.nextId++}`;
  readonly title = input.required<string>();
  readonly open = input(false);
  /** md = 32rem (forms), lg = 48rem, xl = 72rem (wizard, product detail). */
  readonly size = input<'md' | 'lg' | 'xl'>('md');
  readonly testId = input<string | null>(null);
  readonly closed = output<void>();
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');
  protected readonly widthClass = computed(() => {
    switch (this.size()) {
      case 'lg':
        return 'w-[min(48rem,calc(100vw-2rem))]';
      case 'xl':
        return 'w-[min(72rem,calc(100vw-2rem))]';
      default:
        return 'w-[min(32rem,calc(100vw-2rem))]';
    }
  });

  constructor() {
    effect(() => {
      const el = this.dialog().nativeElement;
      if (typeof el.showModal !== 'function') return;
      if (this.open() && !el.open) el.showModal();
      else if (!this.open() && el.open) el.close();
    });
  }
}

/** Formats an ApiProblem for templates that already hold a normalized problem. */
export function problemOf(error: unknown): ApiProblem | null {
  return error ? toApiProblem(error) : null;
}
