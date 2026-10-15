import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { fieldErrors, toApiProblem } from '../../core/api/api-problem';
import { AdminDisputesApi } from '../../core/api/handynet-api';
import { I18nService } from '../../core/i18n/i18n.service';
import { ToastService } from '../../core/ui-state/toast.service';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';
import { DisputeThreadComponent } from '../orders/dispute/dispute-thread';
import { DISPUTE_TEXT_MAX, shortId, validateDecision } from '../orders/dispute/dispute.logic';
import { AdminTabsComponent } from './admin-tabs';

/**
 * Platform admin: one escalated dispute with its thread and the decision form
 * (full refund yes/no + resolution text, confirmed in a dialog).
 */
@Component({
  selector: 'hn-admin-dispute-detail-page',
  imports: [FormsModule, RouterLink, AdminTabsComponent, DisputeThreadComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <nav class="mb-2 text-sm"><a class="hn-link" routerLink="/app/administration/reklamationen">← {{ 'admin.tabs.disputes' | t }}</a></nav>
    <hn-page-header [title]="'admin.disputes.detailTitle' | t: { id: short(id()) }" [description]="'admin.disputes.detailSubtitle' | t" />
    <hn-admin-tabs />

    <hn-banner class="mb-3" [error]="dispute.error()" />

    @if (dispute.value(); as d) {
      <div class="grid gap-4 lg:grid-cols-3">
        <section class="hn-card p-4 lg:col-span-2" aria-labelledby="admin-thread-heading">
          <h2 id="admin-thread-heading" class="mb-3 text-sm">{{ 'disputes.thread' | t }}</h2>
          <hn-dispute-thread [messages]="d.messages" own="Platform" />
        </section>

        <section class="hn-card p-4" aria-labelledby="admin-dispute-details" data-testid="admin-dispute-details">
          <h2 id="admin-dispute-details" class="mb-3 flex flex-wrap items-center gap-2 text-sm">
            <span>{{ 'orders.details' | t }}</span>
            <hn-status-badge [status]="d.status" kind="disputeStatus" data-testid="admin-dispute-status" />
          </h2>
          <dl class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-2 text-sm">
            <dt class="hn-muted">{{ 'admin.disputes.order' | t }}</dt>
            <dd class="hn-mono text-xs break-all">{{ d.orderId }}</dd>
            <dt class="hn-muted">{{ 'orders.buyer' | t }}</dt>
            <dd class="hn-mono text-xs break-all">{{ d.buyerOrganizationId }}</dd>
            <dt class="hn-muted">{{ 'orders.seller' | t }}</dt>
            <dd class="hn-mono text-xs break-all">{{ d.sellerOrganizationId }}</dd>
            <dt class="hn-muted">{{ 'disputes.reason' | t }}</dt>
            <dd>{{ d.reason | tEnum: 'disputeReason' }}</dd>
            <dt class="hn-muted">{{ 'disputes.openedAt' | t }}</dt>
            <dd>{{ d.openedAt | hnDate }}</dd>
            <dt class="hn-muted">{{ 'disputes.escalatedAt' | t }}</dt>
            <dd>{{ d.escalatedAt | hnDate }}</dd>
            @if (d.resolvedAt) {
              <dt class="hn-muted">{{ 'disputes.resolvedAt' | t }}</dt>
              <dd>{{ d.resolvedAt | hnDate }}</dd>
              <dt class="hn-muted">{{ 'disputes.resolvedBy' | t }}</dt>
              <dd>{{ d.resolvedBy | tEnum: 'disputeParty' }}</dd>
              <dt class="hn-muted">{{ 'disputes.resolution' | t }}</dt>
              <dd class="whitespace-pre-line" data-testid="admin-dispute-resolution">{{ d.resolution ?? '–' }}</dd>
            }
          </dl>
        </section>
      </div>

      @if (d.status === 'Escalated') {
        <section class="hn-card mt-4 p-4" aria-labelledby="decision-heading">
          <h2 id="decision-heading" class="mb-2 text-sm">{{ 'admin.disputes.decision' | t }}</h2>
          <form class="max-w-2xl space-y-3" (ngSubmit)="review()" novalidate data-testid="admin-decision-form">
            <fieldset [attr.aria-describedby]="submitted() && errors().refund ? 'decision-refund-error' : null">
              <legend class="mb-1 block text-xs font-medium text-slate-700">
                {{ 'admin.disputes.outcome' | t }} <span class="text-red-700" aria-hidden="true">*</span>
              </legend>
              <div class="space-y-1">
                <label class="flex items-start gap-2 text-sm">
                  <input type="radio" class="mt-0.5" name="refund" [value]="true" [(ngModel)]="refund" (ngModelChange)="revalidate()" data-testid="decision-refund" />
                  <span>
                    <span class="font-medium">{{ 'admin.disputes.refundYes' | t }}</span>
                    <span class="block text-xs text-slate-500">{{ 'admin.disputes.refundYesHint' | t }}</span>
                  </span>
                </label>
                <label class="flex items-start gap-2 text-sm">
                  <input type="radio" class="mt-0.5" name="refund" [value]="false" [(ngModel)]="refund" (ngModelChange)="revalidate()" data-testid="decision-no-refund" />
                  <span>
                    <span class="font-medium">{{ 'admin.disputes.refundNo' | t }}</span>
                    <span class="block text-xs text-slate-500">{{ 'admin.disputes.refundNoHint' | t }}</span>
                  </span>
                </label>
              </div>
              @if (submitted() && errors().refund) {
                <p class="mt-1 text-xs text-red-700" id="decision-refund-error" role="alert">{{ errors().refund! | t }}</p>
              }
            </fieldset>
            <hn-field [label]="'disputes.resolution' | t" for="decision-resolution" [required]="true" [hint]="'admin.disputes.resolutionHint' | t"
              [errors]="resolutionError()">
              <textarea hnInput id="decision-resolution" name="resolution" rows="4" [maxlength]="maxLength" required [(ngModel)]="resolution"
                (ngModelChange)="revalidate()" [attr.aria-invalid]="!!resolutionError()"
                [attr.aria-describedby]="resolutionError() ? 'decision-resolution-error' : 'decision-resolution-hint'"></textarea>
            </hn-field>
            @if (!confirming()) {
              <hn-banner [error]="actionError()" />
            }
            <div class="flex justify-end">
              <button hnButton variant="primary" type="submit" [disabled]="busy()" data-testid="decision-submit">{{ 'admin.disputes.decide' | t }}</button>
            </div>
          </form>
        </section>
      } @else {
        <p class="mt-4 rounded-md border border-slate-200 bg-slate-50 px-3 py-2 text-sm" data-testid="admin-dispute-closed">
          {{ 'admin.disputes.notEscalated' | t }}
        </p>
      }
    } @else if (dispute.loading()) {
      <p class="hn-muted text-sm">{{ 'common.loading' | t }}</p>
    }

    <hn-dialog [open]="confirming()" [title]="'admin.disputes.confirmTitle' | t" (closed)="confirming.set(false)">
      <div class="space-y-4">
        <hn-banner [error]="confirming() ? actionError() : null" />
        <p class="text-sm" data-testid="decision-confirm-text">
          {{ (refund ? 'admin.disputes.confirmRefund' : 'admin.disputes.confirmNoRefund') | t }}
        </p>
        <div class="flex justify-end gap-2">
          <button hnButton type="button" (click)="confirming.set(false)">{{ 'common.cancel' | t }}</button>
          <button hnButton [variant]="refund ? 'danger' : 'primary'" type="button" [loading]="busy()" [disabled]="busy()" (click)="decide()"
            data-testid="decision-confirm">
            {{ 'admin.disputes.decide' | t }}
          </button>
        </div>
      </div>
    </hn-dialog>
  `,
})
export class AdminDisputeDetailPage {
  private readonly api = inject(AdminDisputesApi);
  private readonly toast = inject(ToastService);
  private readonly i18n = inject(I18nService);

  /** Route parameter: dispute id. */
  readonly id = input.required<string>();

  protected readonly dispute = createQuery(
    () => this.id(),
    (id) => this.api.get(id),
  );
  protected readonly maxLength = DISPUTE_TEXT_MAX;
  protected readonly short = shortId;

  protected refund: boolean | null = null;
  protected resolution = '';
  protected readonly submitted = signal(false);
  protected readonly errors = signal<{ refund?: string; resolution?: string }>({});
  protected readonly confirming = signal(false);
  protected readonly busy = signal(false);
  protected readonly actionError = signal<unknown>(null);

  protected readonly resolutionError = computed(() => {
    const key = this.submitted() ? this.errors().resolution : undefined;
    if (key) return this.i18n.t(key, { max: this.maxLength });
    const err = this.actionError();
    const messages = err ? fieldErrors(toApiProblem(err), 'resolution') : [];
    return messages.length ? messages.join(' ') : null;
  });

  protected revalidate(): void {
    if (this.submitted()) this.errors.set(validateDecision({ refund: this.refund, resolution: this.resolution }));
  }

  protected review(): void {
    this.submitted.set(true);
    const errors = validateDecision({ refund: this.refund, resolution: this.resolution });
    this.errors.set(errors);
    if (Object.keys(errors).length) return;
    this.actionError.set(null);
    this.confirming.set(true);
  }

  protected async decide(): Promise<void> {
    const d = this.dispute.value();
    if (!d || this.busy() || this.refund === null) return;
    this.busy.set(true);
    this.actionError.set(null);
    try {
      const updated = await firstValueFrom(this.api.decide(d.id, this.refund, this.resolution.trim()));
      this.dispute.set(updated);
      this.confirming.set(false);
      this.toast.success(this.i18n.t(this.refund ? 'admin.disputes.decidedRefund' : 'admin.disputes.decidedNoRefund'));
    } catch (e) {
      this.actionError.set(e);
      if (toApiProblem(e).status === 409) this.dispute.reload();
    } finally {
      this.busy.set(false);
    }
  }
}
