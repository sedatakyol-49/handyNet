import { ChangeDetectionStrategy, Component, DestroyRef, Injectable, PLATFORM_ID, computed, effect, inject, signal, untracked } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { firstValueFrom } from 'rxjs';
import { FiscalApi, RegisterFiscalStatus } from '../../core/api/fiscal-api';
import { TranslatePipe } from '../../core/i18n/translate.pipe';
import { HnDatePipe } from '../../core/format/date.pipe';
import { PosContext } from './pos-context';
import { isTestEnvironment, tseTone } from './fiscal.logic';

/**
 * TSE state of the current register (provided by the POS app next to PosContext): loaded when the register changes and
 * refreshed every 20 s, so an outage (and its end) shows up at the till without a reload. `refresh()` after a sale.
 */
@Injectable()
export class PosFiscalStatus {
  private readonly api = inject(FiscalApi);
  private readonly ctx = inject(PosContext);
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));

  readonly status = signal<RegisterFiscalStatus | null>(null);
  readonly tone = computed(() => tseTone(this.status()?.state));
  readonly outage = computed(() => this.status()?.state === 'Outage');
  /** Simulated TSE or fiskaly TEST/Sandbox: receipts are not valid for tax purposes. */
  readonly testEnvironment = computed(() => isTestEnvironment(this.status()));

  constructor() {
    effect(() => {
      const id = this.ctx.registerId();
      untracked(() => {
        this.status.set(null);
        if (id) void this.refresh();
      });
    });
    if (!this.browser) return;
    const timer = setInterval(() => void this.refresh(), 20_000);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }

  async refresh(): Promise<void> {
    const id = this.ctx.registerId();
    if (!id || !this.browser) return;
    try {
      const status = await firstValueFrom(this.api.status(id));
      if (this.ctx.registerId() === id) this.status.set(status);
    } catch {
      // Status is informative only; the sale itself reports fiscal problems.
    }
  }
}

/** Compact TSE indicator for the top bar: green (TSE ok), amber (test TSE), red (outage / not usable), grey (no TSE). */
@Component({
  selector: 'hn-pos-tse-pill',
  imports: [TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (fiscal.status(); as s) {
      <span class="flex items-center gap-1.5 rounded-full px-2 py-1 text-xs font-semibold" [class]="toneClass()" role="status"
        [attr.title]="s.tseSerialNumber ? ('pos.tse.serial' | t) + ': ' + s.tseSerialNumber : null" data-testid="pos-tse-status" [attr.data-state]="s.state">
        <span class="inline-block size-2 rounded-full" [class]="dotClass()" aria-hidden="true"></span>
        <span class="max-sm:sr-only">{{ 'pos.tse.state.' + s.state | t }}</span>
      </span>
    }
  `,
})
export class PosTsePillComponent {
  protected readonly fiscal = inject(PosFiscalStatus);
  protected readonly toneClass = computed(() => {
    switch (this.fiscal.tone()) {
      case 'ok':
        return 'bg-emerald-50 text-emerald-800';
      case 'test':
        return 'bg-amber-50 text-amber-900';
      case 'danger':
        return 'bg-red-50 text-red-800';
      default:
        return 'bg-slate-100 text-slate-700';
    }
  });
  protected readonly dotClass = computed(() => {
    switch (this.fiscal.tone()) {
      case 'ok':
        return 'bg-emerald-600';
      case 'test':
        return 'bg-amber-500';
      case 'danger':
        return 'bg-red-600';
      default:
        return 'bg-slate-400';
    }
  });
}

/**
 * Full-width banners: while the register signs in a test environment (receipts not valid for tax purposes), and while the
 * register's TSE is down (sales continue with the outage marker).
 */
@Component({
  selector: 'hn-pos-tse-banner',
  imports: [TranslatePipe, HnDatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (fiscal.testEnvironment()) {
      <div class="hn-no-print border-b border-amber-300 bg-amber-50 px-3 py-1.5 text-center text-sm font-semibold text-amber-900" role="status"
        data-testid="pos-tse-test-banner">
        {{ 'pos.tse.testEnvironment' | t }}
      </div>
    }
    @if (fiscal.outage()) {
      <div class="hn-no-print border-b border-red-300 bg-red-50 px-3 py-2 text-sm text-red-900" role="alert" data-testid="pos-tse-outage-banner">
        <strong>{{ 'pos.tse.outageTitle' | t }}</strong>
        {{ 'pos.tse.outageText' | t }}
        @if (fiscal.status()?.outageSince; as since) {
          <span class="whitespace-nowrap">({{ 'pos.tse.outageSince' | t }} {{ since | hnDate }})</span>
        }
      </div>
    }
  `,
})
export class PosTseBannerComponent {
  protected readonly fiscal = inject(PosFiscalStatus);
}
