import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ImportsApi } from '../../core/api/handynet-api';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';

@Component({
  selector: 'hn-imports-history-page',
  imports: [RouterLink, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-page-header [title]="'imports.title' | t" [description]="'imports.subtitle' | t">
      <a hnButton variant="primary" routerLink="/app/importe/neu" data-testid="new-import">{{ 'imports.newImport' | t }}</a>
    </hn-page-header>

    <hn-banner class="mb-3" [error]="history.error()" />

    <hn-table [loading]="history.loading()">
      <table class="hn-table">
        <thead>
          <tr>
            <th scope="col">{{ 'imports.file' | t }}</th>
            <th scope="col">{{ 'imports.createdAt' | t }}</th>
            <th scope="col">{{ 'common.status' | t }}</th>
            <th scope="col" class="num">{{ 'imports.summary.totalRows' | t }}</th>
            <th scope="col" class="num">{{ 'imports.summary.validRows' | t }}</th>
            <th scope="col" class="num">{{ 'imports.summary.errorRows' | t }}</th>
            <th scope="col" class="num">{{ 'imports.summary.conflictRows' | t }}</th>
            <th scope="col" class="num">{{ 'imports.summary.newOffers' | t }}</th>
            <th scope="col" class="num">{{ 'imports.summary.updatedOffers' | t }}</th>
            <th scope="col">{{ 'imports.completedAt' | t }}</th>
          </tr>
        </thead>
        <tbody>
          @for (b of history.value()?.items ?? []; track b.id) {
            <tr>
              <td>
                <a class="hn-link" [routerLink]="['/app/importe', b.id]">{{ b.fileName }}</a>
                @if (b.duplicateOfCommittedFile) {
                  <span class="ms-1 text-xs text-amber-700">({{ 'imports.duplicateShort' | t }})</span>
                }
              </td>
              <td class="whitespace-nowrap">{{ b.createdAt | hnDate }}</td>
              <td><hn-status-badge [status]="b.status" kind="importStatus" /></td>
              <td class="num">{{ b.summary.totalRows }}</td>
              <td class="num">{{ b.summary.validRows }}</td>
              <td class="num" [class.text-red-700]="b.summary.errorRows > 0">{{ b.summary.errorRows }}</td>
              <td class="num" [class.text-amber-700]="b.summary.conflictRows > 0">{{ b.summary.conflictRows }}</td>
              <td class="num">{{ b.summary.newOffers }}</td>
              <td class="num">{{ b.summary.updatedOffers }}</td>
              <td class="whitespace-nowrap">{{ b.completedAt | hnDate }}</td>
            </tr>
          } @empty {
            @if (!history.loading()) {
              <tr>
                <td colspan="10">
                  <hn-empty-state [title]="'imports.empty' | t" [text]="'imports.emptyText' | t">
                    <a hnButton variant="primary" routerLink="/app/importe/neu">{{ 'imports.newImport' | t }}</a>
                  </hn-empty-state>
                </td>
              </tr>
            }
          }
        </tbody>
      </table>
      <div footer class="border-t border-slate-200 px-2">
        <hn-pagination [page]="page()" [pageSize]="pageSize" [totalCount]="history.value()?.totalCount ?? 0" (pageChange)="page.set($event)" />
      </div>
    </hn-table>
  `,
})
export class ImportsHistoryPage {
  private readonly api = inject(ImportsApi);
  protected readonly page = signal(1);
  protected readonly pageSize = 25;
  protected readonly history = createQuery(
    () => this.page(),
    (p) => this.api.list(p, this.pageSize),
  );
}
