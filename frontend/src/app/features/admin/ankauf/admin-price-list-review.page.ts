import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { toApiProblem } from '../../../core/api/api-problem';
import { AdminAnkaufApi } from '../../../core/api/handynet-api';
import { ENTRY_DECISIONS, EntryDecision, PRICE_CHANGES, PriceChange, PriceList, PriceListEntry } from '../../../core/api/models';
import { I18nService } from '../../../core/i18n/i18n.service';
import { ToastService, describeProblem } from '../../../core/ui-state/toast.service';
import { createQuery } from '../../../shared/data/query';
import { UI } from '../../../shared/ui';
import { ankaufProblemText } from '../../ankauf/ankauf.logic';
import { AdminTabsComponent } from '../admin-tabs';
import { AnkaufAdminNavComponent } from './ankauf-admin-nav';
import {
  CHANGE_TONES,
  DECISION_TONES,
  changeTone,
  formatBp,
  isSelectable,
  priceChangeBp,
  publishBlockedReason,
  selectableIds,
  toggleAll,
} from './ankauf-admin.logic';

type Confirm = 'publish' | 'discard' | null;

/** Platform admin: review one Ankauf price list (decisions per entry, publish or discard). */
@Component({
  selector: 'hn-admin-price-list-review-page',
  imports: [RouterLink, AdminTabsComponent, AnkaufAdminNavComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-page-header [title]="'admin.title' | t" [description]="'ankaufAdmin.review.subtitle' | t" />
    <hn-admin-tabs />
    <hn-ankauf-admin-nav />
    <nav class="mb-2 text-sm"><a class="hn-link" routerLink="/app/administration/ankauf">← {{ 'ankaufAdmin.review.back' | t }}</a></nav>

    <hn-banner class="mb-3" [error]="list.error()" />

    @if (list.value(); as l) {
      <section class="mb-4" aria-labelledby="pl-heading">
        <div class="mb-2 flex flex-wrap items-center gap-2">
          <h2 id="pl-heading" class="text-base">{{ l.label }}</h2>
          <span data-testid="pl-status"><hn-status-badge [status]="l.status" kind="priceListStatus" [tone]="l.status === 'Review' ? 'warning' : l.status === 'Published' ? 'success' : 'neutral'" /></span>
          <span class="text-xs text-slate-500">{{ l.fileName }} · {{ 'ankaufAdmin.review.sheet' | t }} {{ l.sheetName }} · {{ 'ankaufAdmin.lists.importedAt' | t }} {{ l.importedAt | hnDate }}</span>
        </div>
        <p class="mb-3 text-sm">
          {{ 'ankaufAdmin.review.validity' | t: { from: (l.validFrom | hnDate), until: (l.validUntil | hnDate) } }}
          @if (l.publishedAt) { · {{ 'ankaufAdmin.review.publishedAt' | t: { date: (l.publishedAt | hnDate) } }} }
        </p>

        <dl class="grid grid-cols-2 gap-2 sm:grid-cols-4 lg:grid-cols-11" data-testid="pl-summary">
          @for (tile of tiles(); track tile.key) {
            <div class="hn-card px-3 py-2" [attr.data-tile]="tile.key">
              <dt class="text-[11px] font-medium text-slate-500">{{ 'ankaufAdmin.summary.' + tile.key | t }}</dt>
              <dd class="text-lg font-semibold tabular-nums" [class]="tile.tone">{{ tile.value }}</dd>
            </div>
          }
        </dl>

        <details class="hn-card mt-3 px-4 py-2 text-xs text-slate-700">
          <summary class="cursor-pointer text-sm font-medium">{{ 'ankaufAdmin.rules.title' | t }}</summary>
          <ul class="mt-2 list-disc space-y-1 ps-5">
            <li>{{ 'ankaufAdmin.rules.new' | t }}</li>
            <li>{{ 'ankaufAdmin.rules.increase' | t }}</li>
            <li>{{ 'ankaufAdmin.rules.largeDecrease' | t }}</li>
            <li>{{ 'ankaufAdmin.rules.auto' | t }}</li>
            <li>{{ 'ankaufAdmin.rules.noCarryOver' | t }}</li>
          </ul>
        </details>
      </section>

      @if (l.status === 'Review') {
        <div class="hn-card mb-4 flex flex-wrap items-center gap-3 px-4 py-3">
          <div class="min-w-0 flex-1 text-sm">
            @if (blockedReason(); as reason) {
              <p class="text-amber-800" id="publish-blocked" data-testid="pl-publish-blocked">{{ reason | t: { count: l.summary.pending } }}</p>
            } @else {
              <p class="text-slate-700">{{ 'ankaufAdmin.review.readyToPublish' | t }}</p>
            }
          </div>
          <button hnButton type="button" variant="ghost" [disabled]="!!busy()" (click)="ask('discard')" data-testid="pl-discard">{{ 'ankaufAdmin.review.discard' | t }}</button>
          <button hnButton type="button" variant="primary" [disabled]="!!busy() || !!blockedReason()" [attr.aria-describedby]="blockedReason() ? 'publish-blocked' : null"
            (click)="ask('publish')" data-testid="pl-publish">{{ 'ankaufAdmin.review.publish' | t }}</button>
        </div>
      } @else if (l.status === 'Published') {
        <hn-banner class="mb-4" tone="success" [text]="'ankaufAdmin.review.publishedText' | t" />
      } @else {
        <hn-banner class="mb-4" tone="info" [text]="'ankaufAdmin.review.discardedText' | t" />
      }

      <hn-banner class="mb-3" [text]="actionErrorText()" />

      <form class="mb-3 flex flex-wrap items-end gap-3" role="search" (submit)="$event.preventDefault(); applySearch(q.value)">
        <hn-field [label]="'ankaufAdmin.review.decision' | t" for="pl-f-decision" class="w-48">
          <select hnSelect id="pl-f-decision" (change)="setDecision($any($event.target).value)" data-testid="pl-filter-decision">
            <option value="">{{ 'common.all' | t }}</option>
            @for (d of decisions; track d) {
              <option [value]="d" [selected]="d === decision()">{{ d | tEnum: 'entryDecision' }}</option>
            }
          </select>
        </hn-field>
        <hn-field [label]="'ankaufAdmin.review.change' | t" for="pl-f-change" class="w-48">
          <select hnSelect id="pl-f-change" (change)="setChange($any($event.target).value)" data-testid="pl-filter-change">
            <option value="">{{ 'common.all' | t }}</option>
            @for (c of changes; track c) {
              <option [value]="c" [selected]="c === change()">{{ c | tEnum: 'priceChange' }}</option>
            }
          </select>
        </hn-field>
        <hn-field [label]="'common.search' | t" for="pl-f-search" class="w-full max-w-xs">
          <input #q hnInput id="pl-f-search" type="search" [placeholder]="'ankaufAdmin.review.searchPlaceholder' | t" (change)="applySearch(q.value)" data-testid="pl-search" />
        </hn-field>
      </form>

      @if (l.status === 'Review') {
        <div class="mb-2 flex flex-wrap items-center gap-2 text-sm" role="toolbar" [attr.aria-label]="'ankaufAdmin.review.bulk' | t">
          <span class="text-xs text-slate-600" data-testid="pl-selected-count" aria-live="polite">{{ 'ankaufAdmin.review.selected' | t: { count: selected().size } }}</span>
          <button hnButton size="sm" variant="primary" type="button" [disabled]="!selected().size || !!busy()" [loading]="busy() === 'approve'"
            (click)="decide(true)" data-testid="pl-approve">{{ 'ankaufAdmin.review.approve' | t }}</button>
          <button hnButton size="sm" variant="danger" type="button" [disabled]="!selected().size || !!busy()" [loading]="busy() === 'reject'"
            (click)="decide(false)" data-testid="pl-reject">{{ 'ankaufAdmin.review.reject' | t }}</button>
          @if (selected().size) {
            <button hnButton size="sm" variant="ghost" type="button" (click)="selected.set(emptySet())">{{ 'ankaufAdmin.review.clearSelection' | t }}</button>
          }
          <span class="text-xs text-slate-500">{{ 'ankaufAdmin.review.autoNotSelectable' | t }}</span>
        </div>
      }

      <hn-banner class="mb-3" [error]="entries.error()" />
      <hn-table [loading]="entries.loading()">
        <table class="hn-table" data-testid="pl-entries">
          <thead>
            <tr>
              @if (l.status === 'Review') {
                <th scope="col" class="w-8">
                  <input type="checkbox" class="hn-checkbox" [checked]="allOnPageSelected()" [indeterminate]="someOnPageSelected()"
                    [disabled]="!pageSelectable().length" (change)="selectPage()" [attr.aria-label]="'ankaufAdmin.review.selectPage' | t" data-testid="pl-select-page" />
                </th>
              }
              <th scope="col" class="text-end">{{ 'ankaufAdmin.review.row' | t }}</th>
              <th scope="col">{{ 'ankaufAdmin.review.brand' | t }}</th>
              <th scope="col">{{ 'ankaufAdmin.review.model' | t }}</th>
              <th scope="col">{{ 'ankaufAdmin.review.storage' | t }}</th>
              <th scope="col" class="text-end">{{ 'ankaufAdmin.review.previousPrice' | t }}</th>
              <th scope="col" class="text-end">{{ 'ankaufAdmin.review.newPrice' | t }}</th>
              <th scope="col" class="text-end">{{ 'ankaufAdmin.review.changePct' | t }}</th>
              <th scope="col">{{ 'ankaufAdmin.review.change' | t }}</th>
              <th scope="col">{{ 'ankaufAdmin.review.decision' | t }}</th>
            </tr>
          </thead>
          <tbody>
            @for (e of entries.value()?.items ?? []; track e.id) {
              <tr [attr.data-entry]="e.model" [class.bg-accent-50]="selected().has(e.id)">
                @if (l.status === 'Review') {
                  <td>
                    @if (selectable(e, l)) {
                      <input type="checkbox" class="hn-checkbox" [checked]="selected().has(e.id)" (change)="toggle(e.id)"
                        [attr.aria-label]="'ankaufAdmin.review.selectRow' | t: { model: e.brand + ' ' + e.model }" data-testid="pl-select" />
                    }
                  </td>
                }
                <td class="text-end tabular-nums text-xs text-slate-500">{{ e.rowNumber }}</td>
                <td>{{ e.brand }}</td>
                <td class="font-medium">{{ e.model }}</td>
                <td class="whitespace-nowrap">{{ e.storage ?? '–' }}</td>
                <td class="text-end tabular-nums whitespace-nowrap text-slate-600">{{ e.previousPriceMinor | money }}</td>
                <td class="text-end tabular-nums whitespace-nowrap font-medium">{{ e.priceMinor | money }}</td>
                <td class="text-end tabular-nums whitespace-nowrap" [class]="tone(e.change)">{{ pct(e) }}</td>
                <td><hn-status-badge [status]="e.change" kind="priceChange" [tone]="changeTones[e.change]" /></td>
                <td>
                  <hn-status-badge [status]="e.decision" kind="entryDecision" [tone]="decisionTones[e.decision]" />
                  @if (e.decidedAt) { <span class="block text-[11px] text-slate-500">{{ e.decidedAt | hnDate }}</span> }
                </td>
              </tr>
            } @empty {
              @if (!entries.loading()) {
                <tr><td [attr.colspan]="l.status === 'Review' ? 10 : 9"><hn-empty-state [title]="'ankaufAdmin.review.empty' | t" /></td></tr>
              }
            }
          </tbody>
        </table>
        <div footer class="border-t border-slate-200 px-2">
          <hn-pagination [page]="page()" [pageSize]="pageSize" [totalCount]="entries.value()?.totalCount ?? 0" (pageChange)="page.set($event)" />
        </div>
      </hn-table>

      @if (l.skippedRows.length) {
        <section class="hn-card mt-4" aria-labelledby="pl-skipped-heading" data-testid="pl-skipped">
          <button type="button" class="flex w-full items-center justify-between px-4 py-3 text-start" [attr.aria-expanded]="showSkipped()"
            aria-controls="pl-skipped-panel" (click)="showSkipped.set(!showSkipped())">
            <span id="pl-skipped-heading" class="text-sm font-semibold">{{ 'ankaufAdmin.skipped.title' | t: { count: l.skippedRows.length } }}</span>
            <span class="text-xs text-slate-500">{{ (showSkipped() ? 'common.hide' : 'common.show') | t }}</span>
          </button>
          @if (showSkipped()) {
            <div id="pl-skipped-panel" class="border-t border-slate-200">
              <p class="px-4 py-2 text-xs text-slate-600">{{ 'ankaufAdmin.skipped.hint' | t }}</p>
              <table class="hn-table">
                <thead>
                  <tr>
                    <th scope="col" class="text-end">{{ 'ankaufAdmin.review.row' | t }}</th>
                    <th scope="col">{{ 'ankaufAdmin.skipped.text' | t }}</th>
                    <th scope="col">{{ 'ankaufAdmin.skipped.reason' | t }}</th>
                  </tr>
                </thead>
                <tbody>
                  @for (s of l.skippedRows; track s.rowNumber) {
                    <tr>
                      <td class="text-end tabular-nums">{{ s.rowNumber }}</td>
                      <td>{{ s.text || '–' }}</td>
                      <td class="text-xs">{{ s.reason }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        </section>
      }
    } @else if (list.loading()) {
      <p class="hn-muted text-sm">{{ 'common.loading' | t }}</p>
    }

    <hn-dialog [open]="confirm() === 'publish'" [title]="'ankaufAdmin.review.publishTitle' | t" (closed)="confirm.set(null)">
      <div class="space-y-3 text-sm">
        <p>{{ 'ankaufAdmin.review.publishText' | t: { accepted: acceptedCount(), from: (list.value()?.validFrom | hnDate), until: (list.value()?.validUntil | hnDate) } }}</p>
        <hn-banner tone="warning" [text]="'ankaufAdmin.review.publishWarning' | t: { rejected: list.value()?.summary?.rejected ?? 0 }" />
        <hn-banner [text]="actionErrorText()" />
        <div class="flex justify-end gap-2">
          <button hnButton type="button" (click)="confirm.set(null)">{{ 'common.cancel' | t }}</button>
          <button hnButton variant="primary" type="button" [loading]="busy() === 'publish'" [disabled]="!!busy()" (click)="publish()" data-testid="pl-publish-confirm">
            {{ 'ankaufAdmin.review.publish' | t }}
          </button>
        </div>
      </div>
    </hn-dialog>

    <hn-dialog [open]="confirm() === 'discard'" [title]="'ankaufAdmin.review.discardTitle' | t" (closed)="confirm.set(null)">
      <div class="space-y-3 text-sm">
        <p>{{ 'ankaufAdmin.review.discardText' | t }}</p>
        <hn-banner [text]="actionErrorText()" />
        <div class="flex justify-end gap-2">
          <button hnButton type="button" (click)="confirm.set(null)">{{ 'common.cancel' | t }}</button>
          <button hnButton variant="danger" type="button" [loading]="busy() === 'discard'" [disabled]="!!busy()" (click)="discard()" data-testid="pl-discard-confirm">
            {{ 'ankaufAdmin.review.discard' | t }}
          </button>
        </div>
      </div>
    </hn-dialog>
  `,
})
export class AdminPriceListReviewPage {
  private readonly api = inject(AdminAnkaufApi);
  private readonly i18n = inject(I18nService);
  private readonly toast = inject(ToastService);

  /** Route parameter (component input binding). */
  readonly id = input.required<string>();

  protected readonly decisions = ENTRY_DECISIONS;
  protected readonly changes = PRICE_CHANGES;
  protected readonly changeTones = CHANGE_TONES;
  protected readonly decisionTones = DECISION_TONES;

  protected readonly decision = signal<EntryDecision | ''>('');
  protected readonly change = signal<PriceChange | ''>('');
  protected readonly search = signal('');
  protected readonly page = signal(1);
  protected readonly pageSize = 100;
  private readonly entriesVersion = signal(0);

  protected readonly list = createQuery(
    () => this.id(),
    (id) => this.api.list(id),
  );
  protected readonly entries = createQuery(
    () => ({
      id: this.id(),
      decision: this.decision() || null,
      change: this.change() || null,
      search: this.search(),
      page: this.page(),
      pageSize: this.pageSize,
      v: this.entriesVersion(),
    }),
    ({ id, v: _, ...q }) => this.api.entries(id, q),
  );

  protected readonly selected = signal<Set<number>>(new Set());
  protected readonly busy = signal<'approve' | 'reject' | 'publish' | 'discard' | null>(null);
  protected readonly confirm = signal<Confirm>(null);
  protected readonly showSkipped = signal(false);
  private readonly actionError = signal<unknown>(null);

  protected readonly actionErrorText = computed(() => {
    const e = this.actionError();
    if (!e) return null;
    return ankaufProblemText(toApiProblem(e), (k) => this.i18n.t(k), (k) => this.i18n.has(k), (p) => describeProblem(this.i18n, p));
  });

  protected readonly blockedReason = computed(() => {
    const l = this.list.value();
    return l ? publishBlockedReason(l.status, l.summary.pending) : null;
  });

  protected readonly acceptedCount = computed(() => {
    const s = this.list.value()?.summary;
    return s ? s.approved + s.autoAccepted : 0;
  });

  protected readonly tiles = computed(() => {
    const s = this.list.value()?.summary;
    if (!s) return [];
    return [
      { key: 'total', value: s.total, tone: '' },
      { key: 'new', value: s.new, tone: s.new ? 'text-blue-700' : '' },
      { key: 'increases', value: s.increases, tone: s.increases ? 'text-emerald-700' : '' },
      { key: 'decreases', value: s.decreases, tone: '' },
      { key: 'largeDecreases', value: s.largeDecreases, tone: s.largeDecreases ? 'text-red-700' : '' },
      { key: 'unchanged', value: s.unchanged, tone: '' },
      { key: 'pending', value: s.pending, tone: s.pending ? 'text-amber-700' : '' },
      { key: 'approved', value: s.approved, tone: '' },
      { key: 'rejected', value: s.rejected, tone: s.rejected ? 'text-red-700' : '' },
      { key: 'autoAccepted', value: s.autoAccepted, tone: '' },
      { key: 'skipped', value: s.skipped, tone: s.skipped ? 'text-amber-700' : '' },
    ];
  });

  protected readonly pageSelectable = computed(() => {
    const l = this.list.value();
    return l ? selectableIds(this.entries.value()?.items ?? [], l.status) : [];
  });
  protected readonly allOnPageSelected = computed(() => {
    const ids = this.pageSelectable();
    const sel = this.selected();
    return ids.length > 0 && ids.every((id) => sel.has(id));
  });
  protected readonly someOnPageSelected = computed(() => !this.allOnPageSelected() && this.pageSelectable().some((id) => this.selected().has(id)));

  protected emptySet(): Set<number> {
    return new Set();
  }

  protected selectable(e: PriceListEntry, l: PriceList): boolean {
    return isSelectable(e, l.status);
  }

  protected tone(change: PriceChange): string {
    return changeTone(change);
  }

  protected pct(e: PriceListEntry): string {
    const bp = priceChangeBp(e.previousPriceMinor, e.priceMinor);
    return bp === null ? '–' : formatBp(bp, this.i18n.locale(), true);
  }

  protected toggle(id: number): void {
    const next = new Set(this.selected());
    if (next.has(id)) next.delete(id);
    else next.add(id);
    this.selected.set(next);
  }

  protected selectPage(): void {
    this.selected.set(toggleAll(this.selected(), this.pageSelectable()));
  }

  protected setDecision(value: string): void {
    this.decision.set(value as EntryDecision | '');
    this.page.set(1);
  }

  protected setChange(value: string): void {
    this.change.set(value as PriceChange | '');
    this.page.set(1);
  }

  protected applySearch(value: string): void {
    this.search.set(value.trim());
    this.page.set(1);
  }

  protected ask(kind: Confirm): void {
    this.actionError.set(null);
    this.confirm.set(kind);
  }

  protected async decide(approve: boolean): Promise<void> {
    const ids = [...this.selected()];
    if (!ids.length) return;
    // The API accepts at most 1000 ids per call; pages are 100 rows, so this only splits very large cross-page selections.
    await this.run(approve ? 'approve' : 'reject', async () => {
      let updated: PriceList | null = null;
      for (let i = 0; i < ids.length; i += 1000) updated = await firstValueFrom(this.api.decide(this.id(), ids.slice(i, i + 1000), approve));
      this.list.set(updated);
      this.selected.set(new Set());
      this.entriesVersion.update((v) => v + 1);
      this.toast.success(this.i18n.t(approve ? 'ankaufAdmin.review.approvedToast' : 'ankaufAdmin.review.rejectedToast', { count: ids.length }));
    });
  }

  protected async publish(): Promise<void> {
    await this.run('publish', async () => {
      const l = await firstValueFrom(this.api.publish(this.id()));
      this.list.set(l);
      this.confirm.set(null);
      this.selected.set(new Set());
      this.entriesVersion.update((v) => v + 1);
      this.toast.success(this.i18n.t('ankaufAdmin.review.publishedToast', { label: l.label }));
    });
  }

  protected async discard(): Promise<void> {
    await this.run('discard', async () => {
      const l = await firstValueFrom(this.api.discard(this.id()));
      this.list.set(l);
      this.confirm.set(null);
      this.selected.set(new Set());
      this.entriesVersion.update((v) => v + 1);
      this.toast.success(this.i18n.t('ankaufAdmin.review.discardedToast', { label: l.label }));
    });
  }

  private async run(kind: 'approve' | 'reject' | 'publish' | 'discard', action: () => Promise<void>): Promise<void> {
    this.busy.set(kind);
    this.actionError.set(null);
    try {
      await action();
    } catch (e) {
      this.actionError.set(e);
      // State may have changed elsewhere (e.g. list published in another tab): re-read.
      this.list.reload();
    } finally {
      this.busy.set(null);
    }
  }
}
