import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { AnkaufApi } from '../../core/api/handynet-api';
import { Intake, IntakeStatus } from '../../core/api/models';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';
import { PosSubnavComponent } from '../pos/pos-subnav';
import { ankaufLinks } from './ankauf-links';
import { modelLabel } from './ankauf.logic';

const STATUSES: IntakeStatus[] = ['Received', 'Inspected', 'Completed', 'Rejected'];

/** Counts and sums for the summary tiles (open = received or inspected). */
export function intakeStats(intakes: readonly Intake[]): { open: number; completed: number; paidMinor: number; rejected: number } {
  return {
    open: intakes.filter((i) => i.status === 'Received' || i.status === 'Inspected').length,
    completed: intakes.filter((i) => i.status === 'Completed').length,
    paidMinor: intakes.reduce((sum, i) => sum + (i.status === 'Completed' ? (i.paidMinor ?? 0) : 0), 0),
    rejected: intakes.filter((i) => i.status === 'Rejected').length,
  };
}

/** Ankäufe: the organization's device intakes (latest 100) as cards, filterable by status. */
@Component({
  selector: 'hn-intakes-page',
  imports: [RouterLink, PosSubnavComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[class]': 'links.registerId ? "pos-page block" : "block"' },
  template: `
    @if (links.registerId; as rid) {
      <hn-pos-subnav [registerId]="rid" />
    }
    <hn-page-header [title]="'ankauf.intakes.title' | t" [description]="'ankauf.intakes.subtitle' | t">
      <a hnButton variant="primary" [routerLink]="links.quotes" data-testid="ik-new">+ {{ 'ankauf.intakes.new' | t }}</a>
    </hn-page-header>

    @let st = stats();
    <div class="mb-4 grid grid-cols-2 gap-3 lg:grid-cols-4" data-testid="ik-stats">
      <div class="hn-card px-4 py-3"><p class="text-xs text-slate-500">{{ 'ankauf.intakes.statOpen' | t }}</p><p class="text-2xl font-bold tabular-nums">{{ st.open }}</p></div>
      <div class="hn-card px-4 py-3"><p class="text-xs text-slate-500">{{ 'ankauf.intakes.statCompleted' | t }}</p><p class="text-2xl font-bold tabular-nums">{{ st.completed }}</p></div>
      <div class="hn-card px-4 py-3"><p class="text-xs text-slate-500">{{ 'ankauf.intakes.statPaid' | t }}</p><p class="text-2xl font-bold tabular-nums">{{ st.paidMinor | money }}</p></div>
      <div class="hn-card px-4 py-3"><p class="text-xs text-slate-500">{{ 'ankauf.intakes.statRejected' | t }}</p><p class="text-2xl font-bold tabular-nums">{{ st.rejected }}</p></div>
    </div>

    <div class="mb-3 flex flex-wrap items-center gap-2">
      <div class="flex flex-wrap gap-1" role="group" [attr.aria-label]="'common.status' | t">
        <button type="button" class="min-h-11 rounded-full border px-3 text-sm transition-colors" [attr.aria-pressed]="status() === ''"
          [class]="status() === '' ? 'border-accent-600 bg-accent-50 font-semibold text-accent-800' : 'border-slate-200 bg-white hover:border-accent-600'"
          (click)="status.set('')">{{ 'common.all' | t }}</button>
        @for (s of statuses; track s) {
          <button type="button" class="min-h-11 rounded-full border px-3 text-sm transition-colors" [attr.aria-pressed]="status() === s"
            [class]="status() === s ? 'border-accent-600 bg-accent-50 font-semibold text-accent-800' : 'border-slate-200 bg-white hover:border-accent-600'"
            (click)="status.set(s)" [attr.data-status]="s">{{ s | tEnum: 'intakeStatus' }}</button>
        }
      </div>
      <button hnButton size="sm" type="button" class="ms-auto" [loading]="intakes.loading()" (click)="intakes.reload()">{{ 'common.refresh' | t }}</button>
    </div>

    <hn-banner class="mb-3" [error]="intakes.error()" />
    @if (intakes.loading() && !intakes.value()) {
      <div class="grid gap-3 md:grid-cols-2 xl:grid-cols-3" aria-busy="true">
        @for (n of [1, 2, 3]; track n) {
          <div class="h-36 animate-pulse rounded-xl bg-slate-100"></div>
        }
      </div>
    }
    <ul class="grid gap-3 md:grid-cols-2 xl:grid-cols-3" data-testid="ik-table" [attr.aria-label]="'ankauf.intakes.title' | t">
      @for (i of rows(); track i.id) {
        <li [attr.data-intake]="i.imei">
          <a [routerLink]="links.intake(i.id)" class="group flex h-full flex-col rounded-xl border border-slate-200 bg-white p-4 transition hover:-translate-y-0.5 hover:border-accent-600 hover:shadow-md focus-visible:outline-2 focus-visible:outline-accent-600">
            <div class="flex items-start justify-between gap-2">
              <div class="min-w-0">
                <p class="truncate font-semibold">{{ label(i) }}</p>
                <p class="hn-mono truncate text-xs text-slate-500">{{ i.imei }}</p>
              </div>
              <hn-status-badge [status]="i.status" kind="intakeStatus" />
            </div>
            <p class="mt-2 truncate text-sm text-slate-700">{{ i.sellerName }}</p>
            <div class="mt-auto flex items-end justify-between gap-2 pt-3">
              <span class="text-xs text-slate-500">{{ i.receivedAt | hnDate }}</span>
              <span class="text-end">
                @if (i.paidMinor !== null) {
                  <span class="block text-[11px] text-slate-500">{{ 'ankauf.intake.paid' | t }}</span>
                  <span class="text-lg font-bold tabular-nums">{{ i.paidMinor | money }}</span>
                } @else {
                  <span class="block text-[11px] text-slate-500">{{ (i.inspectedOfferMinor !== null ? 'ankauf.intake.inspectedOffer' : 'ankauf.intake.quotedOffer') | t }}</span>
                  <span class="text-lg font-semibold tabular-nums">{{ (i.inspectedOfferMinor ?? i.quotedOfferMinor) | money }}</span>
                }
              </span>
            </div>
            <span class="mt-2 text-xs font-medium text-accent-700 opacity-0 transition group-hover:opacity-100 group-focus-visible:opacity-100">{{ 'ankauf.intakes.open' | t }} →</span>
          </a>
        </li>
      } @empty {
        @if (!intakes.loading()) {
          <li class="md:col-span-2 xl:col-span-3">
            <div class="hn-card"><hn-empty-state [title]="'ankauf.intakes.empty' | t" [text]="'ankauf.intakes.emptyText' | t" /></div>
          </li>
        }
      }
    </ul>
    @if ((intakes.value()?.length ?? 0) >= 100) {
      <p class="mt-2 text-xs text-slate-500">{{ 'ankauf.intakes.limit' | t }}</p>
    }
  `,
})
export class IntakesPage {
  private readonly api = inject(AnkaufApi);
  protected readonly links = ankaufLinks(inject(Router).url);
  protected readonly statuses = STATUSES;
  protected readonly label = modelLabel;
  protected readonly status = signal<IntakeStatus | ''>('');
  protected readonly intakes = createQuery(
    () => true,
    () => this.api.intakes(),
  );
  protected readonly stats = computed(() => intakeStats(this.intakes.value() ?? []));
  protected readonly rows = computed(() => {
    const s = this.status();
    const all = this.intakes.value() ?? [];
    return s ? all.filter((i) => i.status === s) : all;
  });
}
