import { ChangeDetectionStrategy, Component, DOCUMENT, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { ReportsApi } from '../../core/api/handynet-api';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';

/** yyyy-MM-dd of a local date shifted by `days`. */
export function shiftDay(iso: string, days: number): string {
  const [y, m, d] = iso.split('-').map(Number);
  const t = new Date(Date.UTC(y, m - 1, d + days));
  return t.toISOString().slice(0, 10);
}

export function localIsoDate(now: Date = new Date()): string {
  return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;
}

/**
 * Tagesbericht: what the organization sold and bought on one day — till (sales, refunds, VAT, payment methods, cash
 * movements, sessions), Ankauf purchases, B2B purchases (Einkauf) and B2B sales. Printable on A4. Back office
 * (`/app/berichte/tagesbericht`) and POS app (`/kasse/berichte/tag`).
 */
@Component({
  selector: 'hn-daily-report-page',
  imports: [...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[class]': 'pos ? "pos-page block" : "block"' },
  template: `
    <hn-page-header [title]="'reports.daily.title' | t" [description]="'reports.daily.subtitle' | t">
      <button hnButton type="button" class="hn-no-print" (click)="print()" [disabled]="!report.value()" data-testid="dr-print">{{ 'reports.daily.print' | t }}</button>
    </hn-page-header>

    <div class="hn-no-print mb-4 flex flex-wrap items-center gap-2">
      <button hnButton size="sm" type="button" (click)="date.set(shift(-1))" [attr.aria-label]="'reports.daily.previous' | t">‹</button>
      <label for="dr-date" class="sr-only">{{ 'reports.daily.date' | t }}</label>
      <input hnInput id="dr-date" type="date" class="w-44" [max]="todayIso" [value]="date()" (change)="date.set($any($event.target).value || todayIso)" data-testid="dr-date" />
      <button hnButton size="sm" type="button" (click)="date.set(shift(1))" [disabled]="date() >= todayIso" [attr.aria-label]="'reports.daily.next' | t">›</button>
      <button hnButton size="sm" variant="ghost" type="button" (click)="date.set(todayIso)" [disabled]="date() === todayIso">{{ 'reports.daily.today' | t }}</button>
      <button hnButton size="sm" type="button" class="ms-auto" [loading]="report.loading()" (click)="report.reload()">{{ 'common.refresh' | t }}</button>
    </div>

    <hn-banner class="mb-3" [error]="report.error()" />
    @if (report.value(); as r) {
      <article class="hn-print-a4 space-y-4" data-testid="daily-report">
        <header class="hidden print:block">
          <h2 class="text-xl font-bold">{{ 'reports.daily.title' | t }} · {{ r.organizationName }}</h2>
          <p class="text-sm">{{ r.date | hnDate: 'date' }} · {{ 'reports.daily.generated' | t: { time: (r.generatedAt | hnDate) } }}</p>
        </header>

        <div class="grid gap-3 sm:grid-cols-2" data-testid="dr-totals">
          <div class="rounded-xl border border-emerald-200 bg-gradient-to-br from-emerald-50 to-white p-4">
            <p class="text-xs font-semibold tracking-wide text-emerald-800 uppercase">{{ 'reports.daily.sold' | t }}</p>
            <p class="text-3xl font-bold tabular-nums" data-testid="dr-sold">{{ r.totals.soldMinor | money }}</p>
            <p class="text-xs text-slate-600">{{ 'reports.daily.soldHint' | t }}</p>
          </div>
          <div class="rounded-xl border border-accent-100 bg-gradient-to-br from-accent-50 to-white p-4">
            <p class="text-xs font-semibold tracking-wide text-accent-800 uppercase">{{ 'reports.daily.bought' | t }}</p>
            <p class="text-3xl font-bold tabular-nums" data-testid="dr-bought">{{ r.totals.boughtMinor | money }}</p>
            <p class="text-xs text-slate-600">{{ 'reports.daily.boughtHint' | t }}</p>
          </div>
        </div>

        @if (r.pos; as p) {
          <section class="hn-card p-4" aria-labelledby="dr-pos" data-testid="dr-pos">
            <h3 id="dr-pos" class="mb-3 text-sm">{{ 'reports.daily.pos' | t }}</h3>
            <div class="grid gap-4 lg:grid-cols-3">
              <dl class="grid grid-cols-[1fr_auto] gap-x-4 gap-y-1 text-sm">
                <dt>{{ 'reports.daily.sales' | t: { count: p.saleCount } }}</dt><dd class="text-end tabular-nums">{{ p.salesGrossMinor | money }}</dd>
                <dt>{{ 'reports.daily.refunds' | t: { count: p.returnCount + p.stornoCount } }}</dt><dd class="text-end tabular-nums">{{ p.refundsGrossMinor | signedMoney }}</dd>
                <dt class="border-t border-slate-200 pt-1 font-semibold">{{ 'pos.z.totalGross' | t }}</dt><dd class="border-t border-slate-200 pt-1 text-end font-semibold tabular-nums" data-testid="dr-pos-gross">{{ p.totalGrossMinor | money }}</dd>
                <dt>{{ 'pos.z.totalNet' | t }}</dt><dd class="text-end tabular-nums">{{ p.totalNetMinor | money }}</dd>
                <dt>{{ 'pos.z.totalVat' | t }}</dt><dd class="text-end tabular-nums">{{ p.totalVatMinor | money }}</dd>
                <dt>{{ 'reports.daily.invoices' | t }}</dt><dd class="text-end tabular-nums">{{ p.invoiceCount }}</dd>
              </dl>
              <div>
                <h4 class="mb-1 text-xs font-semibold tracking-wide text-slate-500 uppercase">{{ 'pos.z.byTax' | t }}</h4>
                <table class="w-full text-sm">
                  <tbody>
                    @for (t of p.byTaxTreatment; track t.treatment + t.vatRateBasisPoints) {
                      <tr><td>{{ t.treatment | tEnum: 'taxTreatment' }} ({{ t.vatRateBasisPoints / 100 }} %)</td><td class="text-end tabular-nums">{{ t.grossMinor | money }}</td><td class="text-end text-xs text-slate-500 tabular-nums">{{ t.vatMinor | money }}</td></tr>
                    } @empty {
                      <tr><td class="text-slate-500">–</td></tr>
                    }
                  </tbody>
                </table>
                <h4 class="mt-3 mb-1 text-xs font-semibold tracking-wide text-slate-500 uppercase">{{ 'pos.z.byPayment' | t }}</h4>
                <dl class="grid grid-cols-[1fr_auto] gap-x-4 text-sm">
                  @for (m of p.byPaymentMethod; track m.method) {
                    <dt>{{ m.method | tEnum: 'posPaymentMethod' }}</dt><dd class="text-end tabular-nums">{{ m.amountMinor | money }}</dd>
                  }
                </dl>
              </div>
              <dl class="grid grid-cols-[1fr_auto] gap-x-4 gap-y-1 text-sm">
                <dt>{{ 'pos.z.deposits' | t }}</dt><dd class="text-end tabular-nums">{{ p.depositsMinor | money }}</dd>
                <dt>{{ 'pos.z.withdrawals' | t }}</dt><dd class="text-end tabular-nums">{{ p.withdrawalsMinor | money }}</dd>
                <dt class="ps-3 text-slate-600">{{ 'reports.daily.ankaufPayouts' | t: { count: p.ankaufPayoutCount } }}</dt><dd class="text-end tabular-nums text-slate-600" data-testid="dr-pos-ankauf">{{ p.ankaufPayoutsMinor | money }}</dd>
              </dl>
            </div>
            @if (p.sessions.length) {
              <div class="mt-4 overflow-x-auto">
                <table class="hn-table hn-stack" data-testid="dr-sessions">
                  <thead><tr><th scope="col">{{ 'reports.daily.register' | t }}</th><th scope="col">{{ 'common.status' | t }}</th><th scope="col">{{ 'pos.sessions.opened' | t }}</th><th scope="col" class="num">{{ 'pos.z.expected' | t }}</th><th scope="col" class="num">{{ 'pos.z.difference' | t }}</th></tr></thead>
                  <tbody>
                    @for (s of p.sessions; track s.sessionId) {
                      <tr><td [attr.data-label]="'reports.daily.register' | t">{{ s.registerName }}</td><td [attr.data-label]="'common.status' | t">{{ s.status | tEnum: 'posSessionStatus' }}</td><td [attr.data-label]="'pos.sessions.opened' | t" class="text-xs">{{ s.openedAt | hnDate }}</td><td [attr.data-label]="'pos.z.expected' | t" class="num">{{ s.expectedCashMinor | money }}</td><td [attr.data-label]="'pos.z.difference' | t" class="num" [class.text-red-700]="(s.differenceMinor ?? 0) < 0">{{ s.differenceMinor === null ? '–' : (s.differenceMinor | signedMoney) }}</td></tr>
                    }
                  </tbody>
                </table>
              </div>
            }
          </section>
        }

        @if (r.ankauf; as a) {
          <section class="hn-card p-4" aria-labelledby="dr-ankauf" data-testid="dr-ankauf">
            <div class="mb-3 flex flex-wrap items-baseline justify-between gap-2">
              <h3 id="dr-ankauf" class="text-sm">{{ 'reports.daily.ankauf' | t }}</h3>
              <p class="text-sm text-slate-600">{{ 'reports.daily.ankaufSummary' | t: { completed: a.completedCount, received: a.receivedCount, rejected: a.rejectedCount } }}</p>
            </div>
            <div class="grid gap-3 sm:grid-cols-3">
              <div class="rounded-lg bg-slate-50 px-3 py-2"><p class="text-xs text-slate-500">{{ 'reports.daily.ankaufTotal' | t }}</p><p class="text-xl font-bold tabular-nums" data-testid="dr-ankauf-total">{{ a.paidTotalMinor | money }}</p></div>
              @for (m of a.byPaymentMethod; track m.method) {
                <div class="rounded-lg bg-slate-50 px-3 py-2" [attr.data-method]="m.method"><p class="text-xs text-slate-500">{{ m.method | tEnum: 'intakePaymentMethod' }} ({{ m.count }})</p><p class="text-xl font-semibold tabular-nums">{{ m.amountMinor | money }}</p></div>
              }
            </div>
            @if (a.devices.length) {
              <div class="mt-3 overflow-x-auto">
                <table class="hn-table hn-stack" data-testid="dr-devices">
                  <thead><tr><th scope="col">{{ 'reports.daily.time' | t }}</th><th scope="col">{{ 'ankauf.model' | t }}</th><th scope="col">IMEI</th><th scope="col">{{ 'ankauf.condition' | t }}</th><th scope="col">{{ 'ankauf.intake.paymentMethod' | t }}</th><th scope="col" class="num">{{ 'ankauf.intake.paid' | t }}</th></tr></thead>
                  <tbody>
                    @for (d of a.devices; track d.intakeId) {
                      <tr><td [attr.data-label]="'reports.daily.time' | t" class="text-xs">{{ d.completedAt | hnDate }}</td><td [attr.data-label]="'ankauf.model' | t">{{ d.brand }} {{ d.model }} {{ d.storage }}</td><td class="hn-mono text-xs">{{ d.imei }}</td><td [attr.data-label]="'ankauf.condition' | t">{{ d.condition | tEnum: 'deviceCondition' }}</td>
                        <td [attr.data-label]="'ankauf.intake.paymentMethod' | t">{{ d.paymentMethod | tEnum: 'intakePaymentMethod' }}@if (d.paidFromTill) { <span class="ms-1 rounded bg-slate-100 px-1.5 text-[11px]">{{ 'reports.daily.fromTill' | t }}</span> }</td><td [attr.data-label]="'ankauf.intake.paid' | t" class="num">{{ d.paidMinor | money }}</td></tr>
                    }
                  </tbody>
                </table>
              </div>
            }
          </section>
        }

        @if (r.purchases; as b) {
          <section class="hn-card p-4" aria-labelledby="dr-purchases" data-testid="dr-purchases">
            <h3 id="dr-purchases" class="mb-3 text-sm">{{ 'reports.daily.purchases' | t }}</h3>
            <dl class="grid gap-3 text-sm sm:grid-cols-3">
              <div class="rounded-lg bg-slate-50 px-3 py-2"><dt class="text-xs text-slate-500">{{ 'reports.daily.placed' | t: { count: b.placed.length } }}</dt><dd class="text-lg font-semibold tabular-nums">{{ b.placedGrossMinor | money }}</dd></div>
              <div class="rounded-lg bg-slate-50 px-3 py-2"><dt class="text-xs text-slate-500">{{ 'reports.daily.goodsReceived' | t: { count: b.received.length } }}</dt><dd class="text-lg font-semibold tabular-nums">{{ b.receivedGrossMinor | money }}</dd></div>
              <div class="rounded-lg bg-slate-50 px-3 py-2"><dt class="text-xs text-slate-500">{{ 'reports.daily.paid' | t: { count: b.paidCount } }}</dt><dd class="text-lg font-semibold tabular-nums">{{ b.paidMinor | money }}</dd></div>
            </dl>
            @if (b.placed.length + b.received.length) {
              <ul class="mt-3 divide-y divide-slate-100 text-sm">
                @for (e of b.placed.concat(b.received); track e.orderId + e.event) {
                  <li class="flex justify-between gap-3 py-1.5"><span><span class="hn-mono">{{ e.number }}</span> · {{ 'reports.daily.event.' + e.event | t }}</span><span class="tabular-nums">{{ e.grossMinor | money }}</span></li>
                }
              </ul>
            }
          </section>
        }

        @if (r.b2BSales; as s) {
          <section class="hn-card p-4" aria-labelledby="dr-sales" data-testid="dr-b2b-sales">
            <h3 id="dr-sales" class="mb-3 text-sm">{{ 'reports.daily.b2bSales' | t }}</h3>
            <dl class="grid gap-3 text-sm sm:grid-cols-4">
              <div class="rounded-lg bg-slate-50 px-3 py-2"><dt class="text-xs text-slate-500">{{ 'reports.daily.ordersIn' | t: { count: s.received.length } }}</dt><dd class="text-lg font-semibold tabular-nums">{{ s.receivedGrossMinor | money }}</dd></div>
              <div class="rounded-lg bg-slate-50 px-3 py-2"><dt class="text-xs text-slate-500">{{ 'reports.daily.shipped' | t: { count: s.shipped.length } }}</dt><dd class="text-lg font-semibold tabular-nums">{{ s.shippedGrossMinor | money }}</dd></div>
              <div class="rounded-lg bg-slate-50 px-3 py-2"><dt class="text-xs text-slate-500">{{ 'reports.daily.completed' | t: { count: s.completed.length } }}</dt><dd class="text-lg font-semibold tabular-nums">{{ s.completedGrossMinor | money }}</dd></div>
              <div class="rounded-lg bg-slate-50 px-3 py-2"><dt class="text-xs text-slate-500">{{ 'reports.daily.paymentsIn' | t: { count: s.paidCount } }}</dt><dd class="text-lg font-semibold tabular-nums">{{ s.paidMinor | money }}</dd></div>
            </dl>
          </section>
        }

        @if (!r.pos && !r.ankauf && !r.purchases && !r.b2BSales) {
          <div class="hn-card"><hn-empty-state [title]="'reports.daily.emptyTitle' | t" [text]="'reports.daily.empty' | t" /></div>
        }
        <p class="text-xs text-slate-500">{{ 'reports.daily.note' | t: { zone: r.timeZone } }}</p>
      </article>
    } @else if (report.loading()) {
      <div class="space-y-3" aria-busy="true">
        <div class="h-28 animate-pulse rounded-xl bg-slate-100"></div>
        <div class="h-48 animate-pulse rounded-xl bg-slate-100"></div>
      </div>
    }
  `,
})
export class DailyReportPage {
  private readonly api = inject(ReportsApi);
  private readonly document = inject(DOCUMENT);
  protected readonly pos = inject(Router).url.startsWith('/kasse');
  protected readonly todayIso = localIsoDate();
  protected readonly date = signal(this.todayIso);
  protected readonly report = createQuery(() => this.date(), (d) => this.api.daily(d));

  protected shift(days: number): string {
    const next = shiftDay(this.date(), days);
    return next > this.todayIso ? this.todayIso : next;
  }

  protected print(): void {
    this.document.defaultView?.print();
  }
}
