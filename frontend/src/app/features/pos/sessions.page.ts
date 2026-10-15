import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { PosApi } from '../../core/api/handynet-api';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';
import { PosSubnavComponent } from './pos-subnav';

/** Cash sessions of a register, newest first, with their Z-report figures. */
@Component({
  selector: 'hn-pos-sessions-page',
  imports: [RouterLink, PosSubnavComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-pos-subnav [registerId]="registerId()" />
    <hn-page-header [title]="'pos.sessions.title' | t" [description]="register.value()?.name ?? null">
      <button hnButton size="sm" type="button" [loading]="sessions.loading()" (click)="sessions.reload()">{{ 'common.refresh' | t }}</button>
    </hn-page-header>
    <hn-banner class="mb-3" [error]="sessions.error()" />
    <hn-table [loading]="sessions.loading()">
      <table class="hn-table" data-testid="pos-sessions">
        <thead>
          <tr>
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
              <td class="text-xs whitespace-nowrap">{{ s.openedAt | hnDate }}</td>
              <td class="text-xs whitespace-nowrap">{{ s.closedAt | hnDate }}</td>
              <td><hn-status-badge [status]="s.status" kind="posSessionStatus" [tone]="s.status === 'Open' ? 'success' : 'neutral'" /></td>
              <td class="num">{{ s.summary.receiptCount }}</td>
              <td class="num">{{ s.summary.totalGrossMinor | money }}</td>
              <td class="num" [class.text-red-700]="(s.differenceMinor ?? 0) < 0">{{ s.differenceMinor === null ? '–' : (s.differenceMinor | signedMoney) }}</td>
              <td class="text-end"><a class="hn-link" [routerLink]="['/app/kasse/sitzungen', s.id]">{{ (s.status === 'Open' ? 'pos.sessions.manage' : 'pos.sessions.zReport') | t }}</a></td>
            </tr>
          } @empty {
            <tr><td colspan="7" class="py-8 text-center text-sm text-slate-500">{{ 'pos.sessions.empty' | t }}</td></tr>
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
