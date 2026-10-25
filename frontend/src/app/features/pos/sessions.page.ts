import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { PosApi } from '../../core/api/handynet-api';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';
import { PosSubnavComponent } from './pos-subnav';

/** Cash sessions of a register, newest first, with their Z-report figures. */
@Component({
  selector: 'hn-pos-sessions-page',
  host: { class: 'pos-page block' },
  imports: [RouterLink, PosSubnavComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-pos-subnav [registerId]="registerId()" />
    <hn-page-header [title]="'pos.sessions.title' | t" [description]="register.value()?.name ?? null">
      <button hnButton type="button" class="min-h-11" [loading]="sessions.loading()" (click)="sessions.reload()">{{ 'common.refresh' | t }}</button>
    </hn-page-header>
    <hn-banner class="mb-3" [error]="sessions.error()" />
    <!-- phone: cards; md+: table -->
    <ul class="grid gap-3 sm:grid-cols-2 md:hidden" data-testid="pos-sessions-cards">
      @for (s of sessions.value()?.items ?? []; track s.id) {
        <li class="hn-card flex flex-col gap-1 p-4">
          <div class="flex items-start justify-between gap-2">
            <span class="font-semibold tabular-nums">{{ 'pos.sessions.zNumber' | t }} {{ s.zNumber ?? '–' }}</span>
            <hn-status-badge [status]="s.status" kind="posSessionStatus" [tone]="s.status === 'Open' ? 'success' : 'neutral'" />
          </div>
          <p class="text-xs text-slate-500">{{ s.openedAt | hnDate }} – {{ s.closedAt | hnDate }}</p>
          <p class="text-sm">{{ s.summary.receiptCount }} · <span class="font-semibold tabular-nums">{{ s.summary.totalGrossMinor | money }}</span></p>
          @if (s.differenceMinor !== null) {
            <p class="text-sm tabular-nums" [class.text-red-700]="(s.differenceMinor ?? 0) < 0">{{ 'pos.z.difference' | t }}: {{ s.differenceMinor | signedMoney }}</p>
          }
          <a class="hn-link inline-flex min-h-11 items-center" [routerLink]="['/kasse/sitzungen', s.id]">{{ (s.status === 'Open' ? 'pos.sessions.manage' : 'pos.sessions.zReport') | t }}</a>
        </li>
      } @empty {
        @if (!sessions.loading()) {
          <li class="py-8 text-center text-sm text-slate-500 sm:col-span-2">{{ 'pos.sessions.empty' | t }}</li>
        }
      }
    </ul>
    <div class="md:hidden">
      <hn-pagination [page]="page()" [pageSize]="pageSize" [totalCount]="sessions.value()?.totalCount ?? 0" (pageChange)="page.set($event)" />
    </div>
    <hn-table class="hidden md:block" [loading]="sessions.loading()">
      <table class="hn-table" data-testid="pos-sessions">
        <thead>
          <tr>
            <th scope="col">{{ 'pos.sessions.zNumber' | t }}</th>
            <th scope="col">{{ 'pos.sessions.opened' | t }}</th>
            <th scope="col">{{ 'pos.sessions.closed' | t }}</th>
            <th scope="col">{{ 'common.status' | t }}</th>
            <th scope="col" class="num">{{ 'pos.z.receiptCount' | t }}</th>
            <th scope="col" class="num">{{ 'pos.z.totalGross' | t }}</th>
            <th scope="col" class="num">{{ 'pos.z.difference' | t }}</th>
            <th scope="col"><span class="sr-only">{{ 'common.actions' | t }}</span></th>
          </tr>
        </thead>
        <tbody>
          @for (s of sessions.value()?.items ?? []; track s.id) {
            <tr>
              <td class="tabular-nums" data-testid="pos-session-z">{{ s.zNumber ?? '–' }}</td>
              <td class="text-xs whitespace-nowrap">{{ s.openedAt | hnDate }}</td>
              <td class="text-xs whitespace-nowrap">{{ s.closedAt | hnDate }}</td>
              <td><hn-status-badge [status]="s.status" kind="posSessionStatus" [tone]="s.status === 'Open' ? 'success' : 'neutral'" /></td>
              <td class="num">{{ s.summary.receiptCount }}</td>
              <td class="num">{{ s.summary.totalGrossMinor | money }}</td>
              <td class="num" [class.text-red-700]="(s.differenceMinor ?? 0) < 0">{{ s.differenceMinor === null ? '–' : (s.differenceMinor | signedMoney) }}</td>
              <td class="text-end"><a class="hn-link inline-flex min-h-11 items-center" [routerLink]="['/kasse/sitzungen', s.id]">{{ (s.status === 'Open' ? 'pos.sessions.manage' : 'pos.sessions.zReport') | t }}</a></td>
            </tr>
          } @empty {
            <tr><td colspan="8" class="py-8 text-center text-sm text-slate-500">{{ 'pos.sessions.empty' | t }}</td></tr>
          }
        </tbody>
      </table>
      <hn-pagination footer [page]="page()" [pageSize]="pageSize" [totalCount]="sessions.value()?.totalCount ?? 0" (pageChange)="page.set($event)" />
    </hn-table>
  `,
})
export class PosSessionsPage {
  private readonly api = inject(PosApi);
  readonly registerId = input.required<string>();
  protected readonly pageSize = 25;
  protected readonly page = signal(1);
  protected readonly register = createQuery(
    () => this.registerId(),
    (id) => this.api.register(id),
  );
  protected readonly sessions = createQuery(
    () => ({ registerId: this.registerId(), page: this.page() }),
    (q) => this.api.sessions({ registerId: q.registerId, page: q.page, pageSize: this.pageSize }),
  );
}
