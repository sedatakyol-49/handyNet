import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { of } from 'rxjs';
import { PosApi } from '../../core/api/handynet-api';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';
import { PosSubnavComponent } from './pos-subnav';

/** Receipts of a register: current session (default) or all sessions, newest first. */
@Component({
  selector: 'hn-pos-receipts-page',
  host: { class: 'pos-page block' },
  imports: [RouterLink, PosSubnavComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-pos-subnav [registerId]="registerId()" />
    <hn-page-header [title]="'pos.receipts.title' | t" [description]="register.value()?.name ?? null">
      <button hnButton type="button" class="min-h-11" [loading]="receipts.loading()" (click)="receipts.reload()">{{ 'common.refresh' | t }}</button>
    </hn-page-header>

    <fieldset class="mb-3 flex flex-wrap gap-4 text-sm">
      <legend class="sr-only">{{ 'pos.receipts.scope' | t }}</legend>
      <label class="flex min-h-11 items-center gap-2">
        <input type="radio" name="scope" class="hn-checkbox" [checked]="scope() === 'session'" [disabled]="!sessionId()" (change)="setScope('session')" />
        {{ 'pos.receipts.currentSession' | t }}
      </label>
      <label class="flex min-h-11 items-center gap-2">
        <input type="radio" name="scope" class="hn-checkbox" [checked]="scope() === 'all'" (change)="setScope('all')" data-testid="pos-receipts-all" />
        {{ 'pos.receipts.allSessions' | t }}
      </label>
    </fieldset>

    <hn-banner class="mb-3" [error]="receipts.error()" />
    <hn-table [loading]="receipts.loading()">
      <table class="hn-table" data-testid="pos-receipts">
        <thead>
          <tr>
            <th scope="col" class="num">{{ 'pos.receipt.number' | t }}</th>
            <th scope="col">{{ 'pos.receipts.kind' | t }}</th>
            <th scope="col">{{ 'pos.receipt.date' | t }}</th>
            <th scope="col" class="num">{{ 'pos.receipt.total' | t }}</th>
            <th scope="col"><span class="sr-only">{{ 'common.actions' | t }}</span></th>
          </tr>
        </thead>
        <tbody>
          @for (r of receipts.value()?.items ?? []; track r.id) {
            <tr [attr.data-receipt]="r.number">
              <td class="num font-semibold">{{ r.number }}</td>
              <td><hn-status-badge [status]="r.kind" kind="receiptKind" [tone]="r.kind === 'Sale' ? 'success' : r.kind === 'Storno' ? 'danger' : 'warning'" /></td>
              <td class="text-xs whitespace-nowrap">{{ r.createdAt | hnDate }}</td>
              <td class="num" [class.text-red-700]="r.totalGrossMinor < 0">{{ r.totalGrossMinor | money }}</td>
              <td class="text-end"><a class="hn-link inline-flex min-h-11 items-center" [routerLink]="['/kasse/belege', r.id]">{{ 'pos.receipts.open' | t }}</a></td>
            </tr>
          } @empty {
            <tr><td colspan="5" class="py-8 text-center text-sm text-slate-500">{{ 'pos.receipts.empty' | t }}</td></tr>
          }
        </tbody>
      </table>
      <hn-pagination footer [page]="page()" [pageSize]="pageSize" [totalCount]="receipts.value()?.totalCount ?? 0" (pageChange)="page.set($event)" />
    </hn-table>
  `,
})
export class PosReceiptsPage {
  private readonly api = inject(PosApi);
  readonly registerId = input.required<string>();

  protected readonly pageSize = 50;
  protected readonly page = signal(1);
  private readonly chosenScope = signal<'session' | 'all' | null>(null);

  protected readonly register = createQuery(
    () => this.registerId(),
    (id) => this.api.register(id),
  );
  protected readonly sessionId = computed(() => this.register.value()?.openSessionId ?? null);
  protected readonly scope = computed(() => this.chosenScope() ?? (this.sessionId() ? 'session' : 'all'));

  protected readonly receipts = createQuery(
    () => ({
      registerId: this.registerId(),
      ready: !!this.register.value() || !!this.register.error(),
      sessionId: this.scope() === 'session' ? this.sessionId() : null,
      page: this.page(),
    }),
    (q) =>
      q.ready
        ? this.api.receipts({ registerId: q.registerId, sessionId: q.sessionId, page: q.page, pageSize: this.pageSize })
        : of(null),
  );

  protected setScope(scope: 'session' | 'all'): void {
    this.chosenScope.set(scope);
    this.page.set(1);
  }
}
