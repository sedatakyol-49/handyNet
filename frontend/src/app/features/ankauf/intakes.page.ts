import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AnkaufApi } from '../../core/api/handynet-api';
import { IntakeStatus } from '../../core/api/models';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';
import { modelLabel } from './ankauf.logic';

const STATUSES: IntakeStatus[] = ['Received', 'Inspected', 'Completed', 'Rejected'];

/** Ankäufe: the organization's device intakes (latest 100), filterable by status. */
@Component({
  selector: 'hn-intakes-page',
  imports: [RouterLink, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-page-header [title]="'ankauf.intakes.title' | t" [description]="'ankauf.intakes.subtitle' | t">
      <a hnButton routerLink="/app/ankauf">{{ 'ankauf.intakes.toQuotes' | t }}</a>
    </hn-page-header>

    <div class="mb-3 flex flex-wrap items-end gap-3">
      <div>
        <label for="ik-status" class="mb-1 block text-xs font-medium text-slate-700">{{ 'common.status' | t }}</label>
        <select hnSelect id="ik-status" [value]="status()" (change)="status.set($any($event.target).value)" data-testid="ik-status">
          <option value="">{{ 'common.all' | t }}</option>
          @for (s of statuses; track s) {
            <option [value]="s">{{ s | tEnum: 'intakeStatus' }}</option>
          }
        </select>
      </div>
      <button hnButton size="sm" type="button" class="ms-auto" [loading]="intakes.loading()" (click)="intakes.reload()">{{ 'common.refresh' | t }}</button>
    </div>

    <hn-banner class="mb-3" [error]="intakes.error()" />
    <hn-table [loading]="intakes.loading()">
      <table class="hn-table" data-testid="ik-table">
        <thead>
          <tr>
            <th scope="col">{{ 'ankauf.intake.receivedAt' | t }}</th>
            <th scope="col">{{ 'ankauf.model' | t }}</th>
            <th scope="col">IMEI</th>
            <th scope="col">{{ 'ankauf.intake.seller' | t }}</th>
            <th scope="col">{{ 'common.status' | t }}</th>
            <th scope="col" class="text-end">{{ 'ankauf.intake.quotedOffer' | t }}</th>
            <th scope="col" class="text-end">{{ 'ankauf.intake.inspectedOffer' | t }}</th>
            <th scope="col" class="text-end">{{ 'ankauf.intake.paid' | t }}</th>
            <th scope="col"><span class="sr-only">{{ 'common.actions' | t }}</span></th>
          </tr>
        </thead>
        <tbody>
          @for (i of rows(); track i.id) {
            <tr [attr.data-intake]="i.imei">
              <td class="whitespace-nowrap text-xs">{{ i.receivedAt | hnDate }}</td>
              <td>{{ label(i) }}</td>
              <td class="hn-mono text-xs">{{ i.imei }}</td>
              <td>{{ i.sellerName }}</td>
              <td><hn-status-badge [status]="i.status" kind="intakeStatus" /></td>
              <td class="text-end tabular-nums whitespace-nowrap">{{ i.quotedOfferMinor | money }}</td>
              <td class="text-end tabular-nums whitespace-nowrap">{{ i.inspectedOfferMinor | money }}</td>
              <td class="text-end tabular-nums whitespace-nowrap font-medium">{{ i.paidMinor | money }}</td>
              <td class="text-end whitespace-nowrap">
                <a hnButton size="sm" variant="ghost" [routerLink]="['/app/ankauf/ankaeufe', i.id]">{{ 'ankauf.intakes.open' | t }}</a>
              </td>
            </tr>
          } @empty {
            @if (!intakes.loading()) {
              <tr><td colspan="9"><hn-empty-state [title]="'ankauf.intakes.empty' | t" [text]="'ankauf.intakes.emptyText' | t" /></td></tr>
            }
          }
        </tbody>
      </table>
    </hn-table>
    @if ((intakes.value()?.length ?? 0) >= 100) {
      <p class="mt-2 text-xs text-slate-500">{{ 'ankauf.intakes.limit' | t }}</p>
    }
  `,
})
export class IntakesPage {
  private readonly api = inject(AnkaufApi);
  protected readonly statuses = STATUSES;
  protected readonly label = modelLabel;
  protected readonly status = signal<IntakeStatus | ''>('');
  protected readonly intakes = createQuery(
    () => true,
    () => this.api.intakes(),
  );
  protected readonly rows = computed(() => {
    const s = this.status();
    const all = this.intakes.value() ?? [];
    return s ? all.filter((i) => i.status === s) : all;
  });
}
