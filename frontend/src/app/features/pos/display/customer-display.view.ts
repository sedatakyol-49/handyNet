import { ChangeDetectionStrategy, Component, ElementRef, afterRenderEffect, computed, input, viewChild } from '@angular/core';
import { MoneyPipe } from '../../../core/format/money';
import { TranslateEnumPipe, TranslatePipe } from '../../../core/i18n/translate.pipe';
import { QrCodeComponent } from '../qr-code';
import { DisplayState } from './display-state';

/**
 * What the customer sees (second screen or tablet): idle/welcome, the cart with a large total, the payment figures and
 * the thank-you screen with change and the e-receipt QR. Pure presentation of a DisplayState; read-only, no focusable
 * controls. Uses the POS palette (light/dark) and logical properties (RTL).
 */
@Component({
  selector: 'hn-customer-display-view',
  imports: [MoneyPipe, TranslatePipe, TranslateEnumPipe, QrCodeComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'hn-pos fixed inset-0 flex flex-col overflow-hidden bg-pos-surface text-pos-ink',
    '[class.hn-pos-dark]': 'dark()',
    '[attr.data-theme]': 'dark() ? "dark" : "light"',
    '[attr.data-phase]': 'state().phase',
    'data-testid': 'customer-display',
  },
  template: `
    @let s = state();
    <header class="flex min-h-14 shrink-0 items-center gap-3 border-b border-pos-line bg-pos-panel px-4 sm:min-h-16 sm:px-6">
      @if (logoUrl(); as logo) {
        <span class="flex h-10 items-center rounded-md bg-white px-2 ring-1 ring-pos-line"><img [src]="logo" alt="" class="h-8 w-auto max-w-40 object-contain" /></span>
      }
      <p class="min-w-0 flex-1 truncate text-lg font-bold sm:text-xl" data-testid="cd-shop">{{ shopName() }}</p>
      @if (notice(); as n) {
        <p class="shrink-0 rounded-full bg-pos-warn-soft px-3 py-1 text-xs font-semibold text-pos-ink" role="status" data-testid="cd-notice">{{ n }}</p>
      }
    </header>

    @if (s.phase === 'idle') {
      <main class="grid min-h-0 flex-1 place-items-center p-6 text-center" data-testid="cd-idle">
        <div class="max-w-3xl">
          @if (logoUrl(); as logo) {
            <span class="mx-auto mb-6 inline-flex rounded-2xl bg-white p-4 ring-1 ring-pos-line"><img [src]="logo" alt="" class="h-24 w-auto max-w-72 object-contain sm:h-32" /></span>
          }
          <h1 class="text-4xl font-extrabold tracking-tight sm:text-6xl">{{ 'pos.display.welcome' | t }}</h1>
          <p class="mt-4 text-xl whitespace-pre-line text-pos-muted sm:text-2xl" data-testid="cd-message">{{ s.message || shopName() }}</p>
        </div>
      </main>
    } @else if (s.phase === 'done') {
      <main class="grid min-h-0 flex-1 place-items-center overflow-y-auto p-4 text-center sm:p-6" data-testid="cd-done">
        <div class="w-full max-w-3xl">
          <span class="mx-auto mb-3 grid size-16 place-items-center rounded-full bg-pos-ok-soft text-pos-ok-text sm:size-20" aria-hidden="true">
            <svg class="size-10 sm:size-12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2"><path d="m5 12.5 4.5 4.5L19 7.5" /></svg>
          </span>
          <h1 class="text-4xl font-extrabold tracking-tight sm:text-5xl">{{ 'pos.display.thanks' | t }}</h1>
          @if (s.message) {
            <p class="mt-2 text-lg whitespace-pre-line text-pos-muted sm:text-xl" data-testid="cd-message">{{ s.message }}</p>
          }
          <div class="mt-6 grid gap-3" [class.sm:grid-cols-2]="!!s.payment?.changeMinor">
            <div class="rounded-2xl border border-pos-line bg-pos-panel p-5">
              <p class="text-base font-semibold text-pos-muted">{{ 'pos.display.total' | t }}</p>
              <p class="text-4xl font-bold tracking-tight tabular-nums sm:text-5xl"><bdi dir="ltr" data-testid="cd-total">{{ s.totalMinor | money }}</bdi></p>
              @if (s.payment?.method; as m) {
                <p class="mt-1 text-base text-pos-muted" data-testid="cd-method">{{ m === 'Mixed' ? ('pos.display.methodMixed' | t) : (m | tEnum: 'posPaymentMethod') }}</p>
              }
            </div>
            @if (s.payment?.changeMinor) {
              <div class="rounded-2xl bg-pos-accent-soft p-5 text-pos-accent-text">
                <p class="text-base font-semibold">{{ 'pos.display.change' | t }}</p>
                <p class="text-4xl font-bold tracking-tight tabular-nums sm:text-5xl"><bdi dir="ltr" data-testid="cd-change">{{ s.payment!.changeMinor | money }}</bdi></p>
              </div>
            }
          </div>
          @if (s.receiptUrl) {
            <div class="mx-auto mt-6 flex max-w-xl flex-wrap items-center justify-center gap-4 rounded-2xl border border-pos-line bg-pos-panel p-4 text-start" data-testid="cd-receipt-qr">
              <hn-qr-code [data]="s.receiptUrl" [label]="'pos.display.receiptQr' | t" [sizePx]="168" />
              <p class="max-w-64 text-lg font-semibold">{{ 'pos.display.receiptQrHint' | t }}</p>
            </div>
          }
        </div>
      </main>
    } @else {
      <main class="grid min-h-0 flex-1 grid-rows-[minmax(0,1fr)_auto] md:grid-cols-[minmax(0,1fr)_minmax(20rem,30rem)] md:grid-rows-1">
        <section class="flex min-h-0 flex-col" aria-labelledby="cd-cart-heading">
          <h1 id="cd-cart-heading" class="shrink-0 px-4 pt-4 text-sm font-bold tracking-wide text-pos-muted uppercase sm:px-6">
            {{ 'pos.display.cartHeading' | t }}
          </h1>
          <ul #list class="min-h-0 flex-1 overflow-y-auto px-4 py-2 sm:px-6" data-testid="cd-lines">
            @for (l of s.lines; track $index) {
              <li class="flex items-start justify-between gap-4 border-b border-pos-line py-3 last:border-0" data-testid="cd-line">
                <div class="min-w-0">
                  <p class="text-xl leading-snug font-semibold break-words sm:text-2xl">{{ l.description }}</p>
                  <p class="mt-0.5 text-base text-pos-muted tabular-nums">
                    <bdi dir="ltr">{{ l.quantity }} × {{ l.unitPriceMinor | money }}</bdi>
                    @if (l.discountMinor) {
                      · {{ 'pos.display.discount' | t }} <bdi dir="ltr" data-testid="cd-line-discount">{{ -l.discountMinor | money }}</bdi>
                    }
                  </p>
                </div>
                <p class="shrink-0 text-xl font-bold tabular-nums sm:text-2xl"><bdi dir="ltr" data-testid="cd-line-total">{{ l.lineGrossMinor | money }}</bdi></p>
              </li>
            }
          </ul>
        </section>

        <aside class="flex flex-col justify-end gap-3 border-t border-pos-line bg-pos-panel p-4 sm:p-6 md:justify-center md:border-s md:border-t-0" aria-live="polite">
          @if (s.discountTotalMinor) {
            <p class="flex items-baseline justify-between gap-3 text-lg text-pos-muted">
              <span>{{ 'pos.display.discountTotal' | t }}</span>
              <bdi dir="ltr" class="font-semibold tabular-nums" data-testid="cd-discount-total">{{ -s.discountTotalMinor | money }}</bdi>
            </p>
          }
          @if (s.loyalty; as lo) {
            <p class="flex items-baseline justify-between gap-3 text-lg text-pos-muted" data-testid="cd-loyalty">
              <span>{{ 'pos.display.loyalty' | t: { points: lo.points } }}</span>
              <bdi dir="ltr" class="font-semibold tabular-nums">{{ -lo.redeemValueMinor | money }}</bdi>
            </p>
          }
          <div>
            <p class="text-lg font-semibold text-pos-muted">{{ (s.phase === 'payment' ? 'pos.display.due' : 'pos.display.total') | t }}</p>
            <p class="text-5xl leading-none font-extrabold tracking-tight tabular-nums sm:text-6xl lg:text-7xl">
              <bdi dir="ltr" data-testid="cd-total">{{ (s.phase === 'payment' && s.payment ? s.payment.dueMinor : s.totalMinor) | money }}</bdi>
            </p>
            <p class="mt-1 text-sm text-pos-muted">{{ 'pos.display.inclVat' | t }}</p>
          </div>
          @if (s.phase === 'payment' && s.payment; as p) {
            <dl class="grid gap-2 border-t border-pos-line pt-3 text-xl" data-testid="cd-payment">
              <div class="flex items-baseline justify-between gap-3">
                <dt class="text-pos-muted">
                  {{ 'pos.display.tendered' | t }}
                  @if (p.method) {
                    <span class="text-base">({{ p.method === 'Mixed' ? ('pos.display.methodMixed' | t) : (p.method | tEnum: 'posPaymentMethod') }})</span>
                  }
                </dt>
                <dd class="font-bold tabular-nums"><bdi dir="ltr" data-testid="cd-tendered">{{ p.tenderedMinor | money }}</bdi></dd>
              </div>
              @if (p.changeMinor) {
                <div class="flex items-baseline justify-between gap-3 rounded-xl bg-pos-accent-soft px-3 py-2 text-pos-accent-text">
                  <dt class="font-semibold">{{ 'pos.display.change' | t }}</dt>
                  <dd class="text-3xl font-extrabold tabular-nums"><bdi dir="ltr" data-testid="cd-change">{{ p.changeMinor | money }}</bdi></dd>
                </div>
              } @else if (p.tenderedMinor && p.tenderedMinor < p.dueMinor) {
                <div class="flex items-baseline justify-between gap-3 rounded-xl bg-pos-warn-soft px-3 py-2">
                  <dt class="font-semibold">{{ 'pos.display.remaining' | t }}</dt>
                  <dd class="text-3xl font-extrabold tabular-nums"><bdi dir="ltr" data-testid="cd-remaining">{{ p.dueMinor - p.tenderedMinor | money }}</bdi></dd>
                </div>
              }
            </dl>
          }
        </aside>
      </main>
    }
  `,
})
export class CustomerDisplayViewComponent {
  readonly state = input.required<DisplayState>();
  readonly shopName = input<string | null>(null);
  readonly logoUrl = input<string | null>(null);
  readonly dark = input(false);
  /** Connection notice of the remote display ("Verbindung wird wiederhergestellt …"). */
  readonly notice = input<string | null>(null);

  private readonly list = viewChild<ElementRef<HTMLElement>>('list');
  private readonly lineCount = computed(() => this.state().lines.length);

  constructor() {
    // Keep the newest line in view.
    afterRenderEffect(() => {
      this.lineCount();
      const el = this.list()?.nativeElement;
      if (el) el.scrollTop = el.scrollHeight;
    });
  }
}
