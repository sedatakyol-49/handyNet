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
    <div class="min-w-0 max-sm:w-full">
      <h1 class="text-xl wrap-break-word">{{ title() }}</h1>
      @if (description()) {
        <p class="mt-0.5 text-sm text-slate-500">{{ description() }}</p>
      }
    </div>
    <div class="flex flex-wrap items-center gap-2 max-sm:w-full" [class]="stickyActions() ? STICKY : ''"><ng-content /></div>
  `,
})
export class PageHeaderComponent {
  readonly title = input.required<string>();
  readonly description = input<string | null>(null);
  /** Long form: on a phone the actions (Cancel / Save) stick to the bottom edge of the screen. */
  readonly stickyActions = input(false);
  protected readonly STICKY = 'max-md:fixed max-md:inset-x-0 max-md:bottom-0 max-md:z-20 max-md:w-auto max-md:flex-nowrap max-md:justify-end max-md:border-t max-md:border-slate-200 max-md:bg-white max-md:px-3 max-md:pt-3 max-md:pb-[calc(0.75rem+env(safe-area-inset-bottom))]';
}

@Component({
  selector: 'hn-empty-state',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    <div class="px-4 py-10 text-center">
      <span class="mx-auto mb-2 grid size-10 place-items-center rounded-full bg-slate-100 text-slate-400" aria-hidden="true">
        <svg class="size-5" viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.5">
          <path d="M3 7.5 10 4l7 3.5v5L10 16l-7-3.5v-5Z" stroke-linejoin="round" />
          <path d="M3 7.5 10 11l7-3.5M10 11v5" stroke-linejoin="round" />
        </svg>
      </span>
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

/**
 * Placeholder rows while a list loads for the first time (no data yet). Put it in a full-width cell:
 * `<tr class="hn-row-static"><td colspan="8"><hn-skeleton-rows /></td></tr>`. Static unless motion is allowed.
 */
@Component({
  selector: 'hn-skeleton-rows',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block py-1', role: 'status', '[attr.aria-label]': 'label()' },
  template: `
    @for (r of rowList(); track $index) {
      <div class="flex items-center gap-4 border-b border-slate-100 py-2.5 last:border-b-0" aria-hidden="true">
        <span class="h-3 rounded bg-slate-200 motion-safe:animate-pulse" [style.width.%]="38 - ($index % 3) * 6"></span>
        <span class="h-3 flex-1 rounded bg-slate-100 motion-safe:animate-pulse"></span>
        <span class="h-3 w-16 rounded bg-slate-200 motion-safe:animate-pulse"></span>
      </div>
    }
  `,
})
export class SkeletonRowsComponent {
  private readonly i18n = inject(I18nService);
  protected readonly label = computed(() => this.i18n.t('common.loading'));
  readonly rows = input(5);
  protected readonly rowList = computed(() => Array.from({ length: this.rows() }));
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
          <button
            type="button"
            class="hn-icon-btn -my-1 -me-1 size-7 text-current/70 hover:bg-current/10 hover:text-current"
            (click)="dismissed.emit()"
            [attr.aria-label]="'common.close' | t"
            [title]="'common.close' | t"
          >
            <span aria-hidden="true">✕</span>
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
          <button type="button" class="hn-icon-btn -my-1 -me-1 size-7 text-current/70 hover:bg-current/10 hover:text-current" (click)="toasts.dismiss(toast.id)" [attr.aria-label]="'common.close' | t" [title]="'common.close' | t"><span aria-hidden="true">✕</span></button>
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
      [class]="widthClass() + ' ' + sheetClass()"
      [attr.aria-labelledby]="titleId"
      [attr.data-testid]="testId()"
      (cancel)="onCancel($event)"
      (close)="onNativeClose()"
    >
      <div class="sticky top-0 z-20 flex items-center justify-between gap-3 border-b border-slate-200 bg-white px-4 py-2.5">
        <h2 class="text-base" [id]="titleId">{{ title() }}</h2>
        <button type="button" class="hn-icon-btn -me-1" (click)="closed.emit()" [attr.aria-label]="'common.close' | t" [title]="'common.close' | t" data-testid="dialog-close"><span aria-hidden="true">✕</span></button>
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
  /** Wider dialog (lists with forms, e.g. the POS customer picker): 56rem unless a size is given. */
  readonly wide = input(false);
  readonly testId = input<string | null>(null);
  /**
   * Let the parent decide about closing: Esc does not close the dialog by itself but only emits `closed` (like the ✕
   * button); the dialog closes when the parent sets `open` to false (e.g. after a "discard changes?" confirmation).
   */
  readonly guardClose = input(false);
  readonly closed = output<void>();
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');
  /** Phone: small dialogs are bottom sheets, wide ones (lg/xl/wide) take the whole screen (see responsive.css). */
  protected readonly sheetClass = computed(() => (this.size() !== 'md' || this.wide() ? 'hn-sheet hn-sheet-full' : 'hn-sheet'));
  protected readonly widthClass = computed(() => {
    if (this.wide() && this.size() === 'md') return 'w-[min(56rem,calc(100vw-2rem))]';
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

  protected onCancel(event: Event): void {
    if (!this.guardClose()) return;
    event.preventDefault();
    this.closed.emit();
  }

  protected onNativeClose(): void {
    this.closed.emit();
    // The browser may close a guarded dialog anyway (repeated Esc without user activation): reopen it while the
    // parent still wants it open, so `open` and the native state never disagree.
    if (this.guardClose()) {
      setTimeout(() => {
        const el = this.dialog().nativeElement;
        if (this.open() && !el.open && typeof el.showModal === 'function') el.showModal();
      });
    }
  }
}

/** Formats an ApiProblem for templates that already hold a normalized problem. */
export function problemOf(error: unknown): ApiProblem | null {
  return error ? toApiProblem(error) : null;
}
