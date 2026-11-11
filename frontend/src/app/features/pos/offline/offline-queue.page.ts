import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { SessionStore } from '../../../core/auth/session.store';
import { I18nService } from '../../../core/i18n/i18n.service';
import { ToastService } from '../../../core/ui-state/toast.service';
import { UI } from '../../../shared/ui';
import { PosCashierStore } from '../pos-cashier.store';
import { PosSubnavComponent } from '../pos-subnav';
import { QueueEntry, discardReasonError } from './offline-queue.logic';
import { OfflineQueueService } from './offline-queue.service';
import { ProvisionalReceiptComponent } from './provisional-receipt';
import { ReachabilityService } from './reachability.service';

/** Saves a JSON document as a file from the browser (works offline). */
export function downloadJson(filename: string, data: unknown): void {
  const blob = new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10_000);
}

/**
 * Kasse → Offline: the sales this device recorded while the API was unreachable. Waiting entries sync by themselves
 * on reconnect; entries the API refused stay here with the reason until a manager retries or discards them. Discarding
 * is local and final, so the entry is saved as a file first and a reason is required.
 */
@Component({
  selector: 'hn-pos-offline-queue-page',
  host: { class: 'pos-page block' },
  imports: [FormsModule, RouterLink, PosSubnavComponent, ProvisionalReceiptComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-pos-subnav class="hn-no-print" [registerId]="registerId()" />
    <div class="hn-no-print mb-4 flex flex-wrap items-end justify-between gap-3">
      <div class="min-w-0">
        <h1 class="text-2xl font-extrabold tracking-tight">{{ 'pos.offline.queue.title' | t }}</h1>
        <p class="mt-1 max-w-3xl text-sm text-pos-muted">{{ 'pos.offline.queue.subtitle' | t }}</p>
      </div>
      <button type="button" class="pos-soft pos-soft-accent" [disabled]="queue.syncing() || !reach.online() || !waiting()" [attr.aria-busy]="queue.syncing() || null" (click)="syncNow()" data-testid="pos-offline-sync">
        @if (queue.syncing()) {
          <span class="size-3.5 animate-spin rounded-full border-2 border-current border-r-transparent" aria-hidden="true"></span>
        }
        {{ 'pos.offline.queue.syncNow' | t }}
      </button>
    </div>

    <div class="hn-no-print">
      @if (!queue.persistent()) {
        <hn-banner class="mb-3" tone="warning" [text]="'pos.offline.noStorage' | t" data-testid="pos-offline-no-storage" />
      }
      @if (!reach.online()) {
        <hn-banner class="mb-3" tone="warning" [text]="'pos.offline.queue.offlineNow' | t" data-testid="pos-offline-queue-offline" />
      }
      <hn-banner class="mb-3" tone="info" [text]="'pos.offline.legal' | t" />

      @if (!entries().length) {
        <div class="pos-card mb-4">
          <hn-empty-state [title]="'pos.offline.queue.emptyTitle' | t" [text]="'pos.offline.queue.emptyText' | t" data-testid="pos-offline-empty" />
        </div>
      } @else {
        <ul class="mb-4 grid gap-3" data-testid="pos-offline-entries">
          @for (e of entries(); track e.id) {
            <li class="pos-card p-4" [attr.data-reference]="e.reference" [attr.data-status]="e.status" data-testid="pos-offline-entry">
              <div class="flex flex-wrap items-start justify-between gap-3">
                <div class="min-w-0">
                  <p class="font-mono text-base font-bold" dir="ltr">{{ e.reference }}</p>
                  <p class="text-sm text-pos-muted">
                    {{ e.recordedAt | hnDate: 'datetime' }}
                    @if (e.cashierName) {
                      · {{ e.cashierName }}
                    }
                    · {{ 'pos.till.items' | t: { count: itemCount(e) } }}
                  </p>
                </div>
                <div class="flex items-center gap-2">
                  <span class="pos-pill" [class]="e.status === 'attention' ? 'bg-pos-danger-soft text-pos-ink' : 'bg-pos-warn-soft text-pos-ink'" data-testid="pos-offline-status">
                    {{ 'pos.offline.status.' + e.status | t }}
                  </span>
                  <bdi dir="ltr" class="text-lg font-bold tabular-nums" data-testid="pos-offline-total">{{ e.totalMinor | money }}</bdi>
                </div>
              </div>
              @if (e.error; as err) {
                <p class="mt-2 rounded-[10px] px-3 py-2 text-sm" [class]="e.status === 'attention' ? 'bg-pos-danger-soft text-pos-ink' : 'bg-pos-surface text-pos-muted'" role="status" data-testid="pos-offline-error">
                  {{ (e.status === 'attention' ? 'pos.offline.queue.refused' : 'pos.offline.queue.lastTry') | t }}
                  {{ err.message }}
                  @if (err.code) {
                    <span class="font-mono text-xs" dir="ltr">({{ err.code }})</span>
                  }
                </p>
              }
              <div class="mt-3 flex flex-wrap gap-2">
                <button type="button" class="pos-soft" (click)="preview.set(e)" data-testid="pos-offline-print">{{ 'pos.offline.queue.provisional' | t }}</button>
                <button type="button" class="pos-soft" (click)="export(e)" data-testid="pos-offline-export">{{ 'pos.offline.queue.export' | t }}</button>
                @if (e.status === 'attention' && canManage()) {
                  <button type="button" class="pos-soft pos-soft-accent" [disabled]="queue.syncing() || !reach.online()" (click)="retry(e)" data-testid="pos-offline-retry">{{ 'pos.offline.queue.retry' | t }}</button>
                }
                @if (canManage() && e.status !== 'syncing') {
                  <button type="button" class="pos-soft text-pos-danger" (click)="openDiscard(e)" data-testid="pos-offline-discard">{{ 'pos.offline.queue.discard' | t }}</button>
                }
              </div>
            </li>
          }
        </ul>
        @if (!canManage()) {
          <p class="mb-4 text-sm text-pos-muted">{{ 'pos.offline.queue.manageHint' | t }}</p>
        }
      }

      @if (synced().length) {
        <section class="pos-card mb-4 p-4" aria-labelledby="pos-offline-synced-heading" data-testid="pos-offline-synced">
          <h2 id="pos-offline-synced-heading" class="pos-section-label">{{ 'pos.offline.queue.syncedTitle' | t }}</h2>
          <ul class="mt-2 divide-y divide-pos-line text-sm">
            @for (s of synced(); track s.receiptId) {
              <li class="flex flex-wrap items-center justify-between gap-2 py-2" data-testid="pos-offline-synced-row">
                <span><span class="font-mono" dir="ltr">{{ s.reference }}</span> → {{ 'pos.offline.queue.syncedAs' | t: { number: s.receiptNumber } }}</span>
                <span class="flex items-center gap-3">
                  <bdi dir="ltr" class="font-semibold tabular-nums">{{ s.totalMinor | money }}</bdi>
                  <a class="pos-soft min-h-11" [routerLink]="['/kasse/belege', s.receiptId]">{{ 'pos.offline.queue.openReceipt' | t }}</a>
                </span>
              </li>
            }
          </ul>
        </section>
      }
    </div>

    @if (preview(); as p) {
      <section class="pos-card p-4" aria-labelledby="pos-offline-preview-heading" data-testid="pos-offline-preview">
        <div class="hn-no-print mb-3 flex flex-wrap items-center justify-between gap-2">
          <h2 id="pos-offline-preview-heading" class="text-base font-bold">{{ 'pos.offline.queue.provisional' | t }}</h2>
          <div class="flex gap-2">
            <button type="button" class="pos-soft pos-soft-accent" (click)="print()" data-testid="pos-offline-preview-print">{{ 'pos.receipt.print' | t }}</button>
            <button type="button" class="pos-soft" (click)="preview.set(null)">{{ 'common.close' | t }}</button>
          </div>
        </div>
        <hn-provisional-receipt [entry]="p" />
      </section>
    }

    <hn-dialog [title]="'pos.offline.queue.discardTitle' | t" [open]="!!discarding()" (closed)="discarding.set(null)">
      @if (discarding(); as e) {
        <p class="text-sm">{{ 'pos.offline.queue.discardText' | t: { reference: e.reference, total: (e.totalMinor | money) } }}</p>
        <hn-field class="mt-3 block" [label]="'pos.offline.queue.discardReason' | t" for="pos-offline-reason" [required]="true" [errors]="reasonError()">
          <textarea hnInput id="pos-offline-reason" name="reason" rows="3" maxlength="500" [(ngModel)]="reason" data-testid="pos-offline-reason"></textarea>
        </hn-field>
        <div class="mt-4 flex justify-end gap-2">
          <button hnButton class="min-h-11" type="button" (click)="discarding.set(null)">{{ 'common.cancel' | t }}</button>
          <button hnButton class="min-h-11" variant="danger" type="button" (click)="confirmDiscard(e)" data-testid="pos-offline-discard-confirm">{{ 'pos.offline.queue.discardConfirm' | t }}</button>
        </div>
      }
    </hn-dialog>
  `,
})
export class PosOfflineQueuePage {
  readonly registerId = input.required<string>();

  protected readonly queue = inject(OfflineQueueService);
  protected readonly reach = inject(ReachabilityService);
  private readonly session = inject(SessionStore);
  private readonly cashiers = inject(PosCashierStore);
  private readonly i18n = inject(I18nService);
  private readonly toast = inject(ToastService);

  protected readonly canManage = computed(() => this.session.can('pos.manage'));
  protected readonly entries = computed(() => this.queue.entries().filter((e) => e.registerId === this.registerId()));
  protected readonly waiting = computed(() => this.entries().some((e) => e.status === 'queued'));
  protected readonly synced = computed(() => this.queue.synced().filter((s) => s.registerId === this.registerId()));
  protected readonly preview = signal<QueueEntry | null>(null);
  protected readonly discarding = signal<QueueEntry | null>(null);
  protected readonly reasonError = signal<string | null>(null);
  protected reason = '';

  protected itemCount(e: QueueEntry): number {
    return e.lines.reduce((n, l) => n + l.quantity, 0);
  }

  protected async syncNow(): Promise<void> {
    await this.reach.check();
    await this.queue.sync(this.registerId());
  }

  protected retry(e: QueueEntry): void {
    void this.queue.retry(e.id);
  }

  protected export(e: QueueEntry): void {
    downloadJson(`offline-${e.reference}.json`, { kind: 'handynet-offline-sale', exportedAt: new Date().toISOString(), entry: e });
  }

  protected print(): void {
    window.print();
  }

  protected openDiscard(e: QueueEntry): void {
    this.reason = '';
    this.reasonError.set(null);
    this.discarding.set(e);
  }

  /** Saves the entry with reason, user and time as a file, then removes it from this device. */
  protected async confirmDiscard(e: QueueEntry): Promise<void> {
    const problem = discardReasonError(this.reason);
    this.reasonError.set(problem ? this.i18n.t('pos.offline.queue.discardReasonError.' + problem) : null);
    if (problem) return;
    const record = this.queue.discardRecordFor(e.id, this.reason, this.cashiers.actingName() ?? this.session.me()?.displayName ?? null);
    if (!record) return;
    downloadJson(`offline-${e.reference}-verworfen.json`, record);
    if (await this.queue.discard(e.id)) {
      if (this.preview()?.id === e.id) this.preview.set(null);
      this.discarding.set(null);
      this.toast.success(this.i18n.t('pos.offline.queue.discarded', { reference: e.reference }));
    }
  }
}
