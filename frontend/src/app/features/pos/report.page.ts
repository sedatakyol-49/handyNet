import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { of } from 'rxjs';
import { PosApi } from '../../core/api/handynet-api';
import { SessionStore } from '../../core/auth/session.store';
import { PosCashierStore } from './pos-cashier.store';
import { I18nService } from '../../core/i18n/i18n.service';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';
import { PosContext } from './pos-context';
import { canSeeSessionReport, posRights } from './pos-rights';
import { localDayStart } from './sales-filter';

/** Longest date range the API reports on. */
const MAX_RANGE_DAYS = 366;

/**
 * Sales report (`GET /api/pos/reports/sales`) by product, cashier and payment method — for one cash session
 * (`?sitzung=`, `pos.close_session` or `pos.manage`) or a date range, optionally per register (`pos.manage`).
 * Printable on the receipt roll like the Z-report. Gross figures only (§25a VAT per product is never shown).
 */
@Component({
  selector: 'hn-pos-report-page',
  host: { class: 'pos-page block' },
  imports: [RouterLink, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-page-header class="hn-no-print" [title]="'pos.report.title' | t" [description]="sessionId() ? ('pos.report.forSession' | t) : ('pos.report.forRange' | t)">
      @if (report.value()) {
        <button hnButton type="button" class="min-h-11" (click)="print()" data-testid="pos-report-print">{{ 'pos.report.print' | t }}</button>
      }
      @if (session.value(); as s) {
        <a hnButton class="min-h-11" [routerLink]="['/kasse/sitzungen', s.id]">{{ 'pos.report.toSession' | t }}</a>
      }
    </hn-page-header>

    @if (!allowed()) {
      <hn-banner tone="warning" [text]="'pos.rights.reportForbidden' | t" data-testid="pos-report-forbidden" />
    } @else {
      @if (!sessionId()) {
        <form class="pos-card hn-no-print mb-3 grid gap-3 p-3 sm:grid-cols-[repeat(3,minmax(0,12rem))_auto] sm:items-end" (submit)="$event.preventDefault(); apply()" novalidate data-testid="pos-report-range">
          <hn-field [label]="'pos.receipts.from' | t" for="pos-r-from">
            <input hnInput id="pos-r-from" type="date" class="h-11" [value]="from()" (input)="from.set($any($event.target).value)" data-testid="pos-r-from" />
          </hn-field>
          <hn-field [label]="'pos.receipts.to' | t" for="pos-r-to" [errors]="rangeError()">
            <input hnInput id="pos-r-to" type="date" class="h-11" [value]="to()" (input)="to.set($any($event.target).value)" data-testid="pos-r-to" />
          </hn-field>
          <hn-field [label]="'pos.report.register' | t" for="pos-r-register">
            <select hnSelect id="pos-r-register" class="h-11" (change)="registerId.set($any($event.target).value)" data-testid="pos-r-register">
              <option value="" [selected]="!registerId()">{{ 'pos.report.allRegisters' | t }}</option>
              @for (r of registers.value() ?? []; track r.id) {
                <option [value]="r.id" [selected]="r.id === registerId()">{{ r.name }}</option>
              }
            </select>
          </hn-field>
          <button hnButton variant="primary" type="submit" class="min-h-11" data-testid="pos-r-apply">{{ 'pos.report.show' | t }}</button>
        </form>
      }

      <hn-banner class="mb-3" [error]="report.error() || session.error()" />
      @if (report.value(); as r) {
        <article class="hn-print-area pos-card p-4 text-sm" aria-labelledby="pos-report-heading" data-testid="pos-report">
          <h2 id="pos-report-heading" class="text-base">{{ 'pos.report.title' | t }}</h2>
          <p class="mb-3 text-xs text-slate-600" data-testid="pos-report-scope">
            @if (session.value(); as s) {
              {{ s.registerName }} · {{ s.openedAt | hnDate }} – {{ s.closedAt ? (s.closedAt | hnDate) : ('pos.z.stillOpen' | t) }}
            } @else {
              {{ r.from | hnDate: 'date' }} – {{ rangeEndLabel() }} · {{ registerName() }}
            }
          </p>
          <dl class="mb-4 grid grid-cols-[1fr_auto] gap-x-4 gap-y-1">
            <dt>{{ 'pos.z.receiptCount' | t }}</dt>
            <dd class="text-end tabular-nums" data-testid="pos-report-count">{{ r.receiptCount }}</dd>
            <dt class="font-semibold">{{ 'pos.z.totalGross' | t }}</dt>
            <dd class="text-end font-semibold tabular-nums" data-testid="pos-report-total"><bdi dir="ltr">{{ r.totalGrossMinor | money }}</bdi></dd>
          </dl>

          <h3 class="mb-1 text-xs font-semibold uppercase tracking-wide text-slate-500">{{ 'pos.report.byProduct' | t }}</h3>
          <div class="mb-4 overflow-x-auto">
            <table class="w-full text-sm" data-testid="pos-report-products">
              <thead>
                <tr class="text-xs text-slate-500">
                  <th scope="col" class="text-start font-normal">{{ 'common.product' | t }}</th>
                  <th scope="col" class="text-end font-normal">{{ 'pos.report.sold' | t }}</th>
                  <th scope="col" class="text-end font-normal">{{ 'pos.report.returned' | t }}</th>
                  <th scope="col" class="text-end font-normal">{{ 'pos.report.discount' | t }}</th>
                  <th scope="col" class="text-end font-normal">{{ 'common.gross' | t }}</th>
                </tr>
              </thead>
              <tbody>
                @for (p of r.byProduct; track p.productId) {
                  <tr class="border-t border-slate-100 align-top" [attr.data-product]="p.description">
                    <td class="py-1 pe-2">{{ p.description }}</td>
                    <td class="py-1 text-end tabular-nums">{{ p.soldQuantity }}</td>
                    <td class="py-1 text-end tabular-nums">{{ p.returnedQuantity }}</td>
                    <td class="py-1 text-end tabular-nums"><bdi dir="ltr">{{ p.discountMinor | money }}</bdi></td>
                    <td class="py-1 text-end tabular-nums"><bdi dir="ltr">{{ p.grossMinor | money }}</bdi></td>
                  </tr>
                } @empty {
                  <tr><td colspan="5" class="py-2 text-slate-500">{{ 'pos.report.empty' | t }}</td></tr>
                }
              </tbody>
            </table>
          </div>

          <h3 class="mb-1 text-xs font-semibold uppercase tracking-wide text-slate-500">{{ 'pos.report.byCashier' | t }}</h3>
          <div class="mb-4 overflow-x-auto">
            <table class="w-full text-sm" data-testid="pos-report-cashiers">
              <thead>
                <tr class="text-xs text-slate-500">
                  <th scope="col" class="text-start font-normal">{{ 'pos.receipt.cashier' | t }}</th>
                  <th scope="col" class="text-end font-normal">{{ 'pos.report.sales' | t }}</th>
                  <th scope="col" class="text-end font-normal">{{ 'pos.report.refunds' | t }}</th>
                  <th scope="col" class="text-end font-normal">{{ 'common.gross' | t }}</th>
                </tr>
              </thead>
              <tbody>
                @for (c of r.byCashier; track c.userId) {
                  <tr class="border-t border-slate-100">
                    <td class="py-1">{{ c.name ?? '–' }}</td>
                    <td class="py-1 text-end tabular-nums">{{ c.saleCount }}</td>
                    <td class="py-1 text-end tabular-nums">{{ c.refundCount }}</td>
                    <td class="py-1 text-end tabular-nums"><bdi dir="ltr">{{ c.grossMinor | money }}</bdi></td>
                  </tr>
                } @empty {
                  <tr><td colspan="4" class="py-2 text-slate-500">{{ 'pos.report.empty' | t }}</td></tr>
                }
              </tbody>
            </table>
          </div>

          <h3 class="mb-1 text-xs font-semibold uppercase tracking-wide text-slate-500">{{ 'pos.z.byPayment' | t }}</h3>
          <table class="w-full text-sm" data-testid="pos-report-methods">
            <thead>
              <tr class="text-xs text-slate-500">
                <th scope="col" class="text-start font-normal">{{ 'pos.receipts.paymentMethod' | t }}</th>
                <th scope="col" class="text-end font-normal">{{ 'pos.z.receiptCount' | t }}</th>
                <th scope="col" class="text-end font-normal">{{ 'pos.receipt.amount' | t }}</th>
              </tr>
            </thead>
            <tbody>
              @for (m of r.byPaymentMethod; track m.method) {
                <tr class="border-t border-slate-100" [attr.data-method]="m.method">
                  <td class="py-1">{{ m.method | tEnum: 'posPaymentMethod' }}</td>
                  <td class="py-1 text-end tabular-nums">{{ m.receiptCount }}</td>
                  <td class="py-1 text-end tabular-nums" data-testid="pos-report-method-amount"><bdi dir="ltr">{{ m.amountMinor | money }}</bdi></td>
                </tr>
              }
            </tbody>
          </table>
          <p class="mt-3 text-xs text-slate-500">{{ 'pos.report.grossNote' | t }}</p>
        </article>
      } @else if (report.loading()) {
        <p class="py-6 text-center text-sm text-slate-500">{{ 'common.loading' | t }}</p>
      }
    }
  `,
})
export class PosReportPage {
  private readonly api = inject(PosApi);
  private readonly store = inject(SessionStore);
  protected readonly cashiers = inject(PosCashierStore);
  private readonly i18n = inject(I18nService);
  private readonly route = inject(ActivatedRoute);
  private readonly ctx = inject(PosContext);

  private readonly params = toSignal(this.route.queryParamMap, { requireSync: true });
  protected readonly sessionId = computed(() => this.params().get('sitzung'));
  protected readonly rights = computed(() => posRights(this.cashiers.membership()));
  protected readonly allowed = computed(() => (this.sessionId() ? canSeeSessionReport(this.rights()) : this.rights().manage));

  // Date-range form (pos.manage); defaults to today.
  protected readonly from = signal(today());
  protected readonly to = signal(today());
  protected readonly registerId = signal('');
  protected readonly rangeError = signal<string | null>(null);
  private readonly applied = signal<{ from: string; to: string; registerId: string } | null>(null);

  protected readonly registers = createQuery(
    () => !this.sessionId() && this.rights().manage,
    (load) => (load ? this.api.registers() : of([])),
  );
  protected readonly session = createQuery(
    () => (this.allowed() ? this.sessionId() : null),
    (id) => (id ? this.api.session(id) : of(null)),
  );
  protected readonly report = createQuery(
    () => ({ allowed: this.allowed(), sessionId: this.sessionId(), range: this.applied() }),
    (q) => {
      if (!q.allowed) return of(null);
      if (q.sessionId) return this.api.salesReport({ sessionId: q.sessionId });
      if (!q.range) return of(null);
      const from = localDayStart(q.range.from)!;
      const to = localDayStart(q.range.to, 1)!;
      return this.api.salesReport({ from: from.toISOString(), to: to.toISOString(), registerId: q.range.registerId || null });
    },
  );
  protected readonly registerName = computed(() => {
    const id = this.applied()?.registerId;
    return id ? (this.registers.value()?.find((r) => r.id === id)?.name ?? '–') : this.i18n.t('pos.report.allRegisters');
  });
  protected readonly rangeEndLabel = computed(() => {
    const to = this.applied()?.to;
    const day = to ? localDayStart(to) : null;
    return day ? new Intl.DateTimeFormat(this.i18n.locale(), { dateStyle: 'medium' }).format(day) : '–';
  });

  constructor() {
    effect(() => {
      const s = this.session.value();
      untracked(() => {
        if (s) {
          this.ctx.setRegister(s.registerId, s.registerName);
          this.ctx.session.set(s.status === 'Open' ? s : null);
        }
      });
    });
    effect(() => {
      if (!this.sessionId() && this.rights().manage) untracked(() => this.applied() ?? this.apply());
    });
  }

  protected apply(): void {
    const from = localDayStart(this.from());
    const to = localDayStart(this.to(), 1);
    if (!from || !to || from >= to || (to.getTime() - from.getTime()) / 86_400_000 > MAX_RANGE_DAYS) {
      this.rangeError.set(this.i18n.t('pos.report.rangeInvalid', { days: MAX_RANGE_DAYS }));
      return;
    }
    this.rangeError.set(null);
    this.applied.set({ from: this.from(), to: this.to(), registerId: this.registerId() });
  }

  protected print(): void {
    if (typeof window !== 'undefined') window.print();
  }

}

function today(): string {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}
