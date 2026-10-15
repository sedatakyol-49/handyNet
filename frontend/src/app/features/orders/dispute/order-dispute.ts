import { isPlatformBrowser } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, PLATFORM_ID, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { fieldErrors, toApiProblem } from '../../../core/api/api-problem';
import { DisputesApi } from '../../../core/api/handynet-api';
import { DISPUTE_REASONS, Dispute, DisputeReason, Order } from '../../../core/api/models';
import { SessionStore } from '../../../core/auth/session.store';
import { I18nService } from '../../../core/i18n/i18n.service';
import { ToastService } from '../../../core/ui-state/toast.service';
import { UI } from '../../../shared/ui';
import { DisputeThreadComponent } from './dispute-thread';
import {
  DISPUTE_TEXT_MAX,
  DisputeActions,
  canReportProblem,
  disputeActions,
  isDisputeActive,
  ownParty,
  validateDisputeText,
  validateReport,
} from './dispute.logic';

/** Actions that end or hand over the dispute; all go through the confirmation dialog. */
type DialogAction = 'refund' | 'withdraw' | 'escalate';

const DIALOG_TEXTS: Record<DialogAction, { title: string; text: string; label: string; textRequired: boolean }> = {
  refund: { title: 'disputes.refund', text: 'disputes.confirmText.refund', label: 'disputes.refundText', textRequired: true },
  withdraw: { title: 'disputes.withdraw', text: 'disputes.confirmText.withdraw', label: 'disputes.withdrawText', textRequired: true },
  escalate: { title: 'disputes.escalate', text: 'disputes.confirmText.escalate', label: 'disputes.escalateText', textRequired: false },
};

export interface DisputeChange {
  /** The order status changed on the server (Disputed / Completed / Refunded). */
  reloadOrder: boolean;
  /**
   * The online payment is refunded (or the seller paid out) by a background job a few seconds later – re-read the
   * payments once more after a short delay.
   */
  refetchPaymentsSoon?: boolean;
}

/**
 * "Reklamation" section of the order detail page.
 * - Buyer: report a problem while the order is Delivered and the inspection window runs (stops automatic completion
 *   and the payout), withdraw the complaint.
 * - Seller: grant a full refund.
 * - Both: message thread, hand over to HandyNet (escalate) while Open.
 */
@Component({
  selector: 'hn-order-dispute',
  imports: [FormsModule, DisputeThreadComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[class.hidden]': '!visible()' },
  templateUrl: './order-dispute.html',
})
export class OrderDisputeComponent {
  private readonly api = inject(DisputesApi);
  private readonly session = inject(SessionStore);
  private readonly toast = inject(ToastService);
  private readonly i18n = inject(I18nService);

  readonly order = input.required<Order>();
  /** Disputes of the order, newest first. */
  readonly disputes = input.required<Dispute[]>();
  readonly disputesLoading = input(false);
  readonly disputesError = input<unknown>(null);
  readonly isBuyer = input(false);
  readonly isSeller = input(false);

  /** Emitted after every successful action (and after conflicts); the parent reloads disputes, order and payments. */
  readonly changed = output<DisputeChange>();

  protected readonly reasons = DISPUTE_REASONS;
  protected readonly maxLength = DISPUTE_TEXT_MAX;
  protected readonly now = signal(Date.now());
  protected readonly busy = signal<string | null>(null);
  protected readonly error = signal<unknown>(null);

  // Report form
  protected readonly formOpen = signal(false);
  protected readonly submitted = signal(false);
  protected report: { reason: DisputeReason | ''; description: string } = { reason: '', description: '' };
  protected readonly descriptionLength = signal(0);
  protected readonly reportErrors = signal<{ reason?: string; description?: string }>({});

  // Thread input (per dispute id)
  protected messageText = '';
  protected readonly messageError = signal<string | null>(null);

  // Confirmation dialog
  protected readonly dialog = signal<{ action: DialogAction; dispute: Dispute } | null>(null);
  protected dialogText = '';
  protected readonly dialogSubmitted = signal(false);
  protected readonly dialogTexts = computed(() => DIALOG_TEXTS[this.dialog()?.action ?? 'escalate']);

  private readonly canRead = computed(() => this.session.can('orders.read'));
  private readonly canPlace = computed(() => this.isBuyer() && this.session.allows('B2BPurchasing', 'orders.place'));
  private readonly canFulfil = computed(() => this.isSeller() && this.session.allows('B2BSales', 'orders.fulfil'));

  protected readonly canReport = computed(() =>
    canReportProblem(this.order(), this.disputes(), this.isBuyer(), this.canPlace(), this.now()),
  );
  protected readonly visible = computed(
    () => this.disputes().length > 0 || this.canReport() || this.order().status === 'Disputed' || !!this.disputesError(),
  );

  constructor() {
    // Keeps the "report until" check current (minute resolution; no animation).
    if (isPlatformBrowser(inject(PLATFORM_ID))) {
      const handle = setInterval(() => this.now.set(Date.now()), 30_000);
      inject(DestroyRef).onDestroy(() => clearInterval(handle));
    }
  }

  protected actions(d: Dispute): DisputeActions {
    return disputeActions(d, { canRead: this.canRead(), canPlace: this.canPlace(), canFulfil: this.canFulfil() });
  }

  protected own(d: Dispute) {
    return ownParty(d.role);
  }

  protected active(d: Dispute): boolean {
    return isDisputeActive(d.status);
  }

  // ------------------------------------------------------------ report

  protected openForm(): void {
    this.report = { reason: '', description: '' };
    this.descriptionLength.set(0);
    this.reportErrors.set({});
    this.submitted.set(false);
    this.error.set(null);
    this.formOpen.set(true);
  }

  protected descriptionChanged(value: string): void {
    this.descriptionLength.set(value.length);
    this.revalidate();
  }

  protected revalidate(): void {
    if (this.submitted()) this.reportErrors.set(validateReport(this.report));
  }

  protected reportFieldError(field: 'reason' | 'description'): string | null {
    const client = this.submitted() ? this.reportErrors()[field] : undefined;
    if (client) return this.i18n.t(client, { max: this.maxLength });
    const err = this.error();
    const messages = err ? fieldErrors(toApiProblem(err), field) : [];
    return messages.length ? messages.join(' ') : null;
  }

  protected async submitReport(): Promise<void> {
    this.submitted.set(true);
    const errors = validateReport(this.report);
    this.reportErrors.set(errors);
    if (Object.keys(errors).length) return;
    await this.run('report', async () => {
      await firstValueFrom(
        this.api.open(this.order().id, { reason: this.report.reason as DisputeReason, description: this.report.description.trim() }),
      );
      this.formOpen.set(false);
      this.toast.success(this.i18n.t('disputes.reported', { number: this.order().number }));
      this.changed.emit({ reloadOrder: true });
    });
  }

  // ------------------------------------------------------------ messages

  protected async sendMessage(d: Dispute): Promise<void> {
    const key = validateDisputeText(this.messageText);
    this.messageError.set(key ? this.i18n.t(key, { max: this.maxLength }) : null);
    if (key) return;
    await this.run('message-' + d.id, async () => {
      await firstValueFrom(this.api.message(d.id, this.messageText.trim()));
      this.messageText = '';
      this.changed.emit({ reloadOrder: false });
    });
  }

  // ------------------------------------------------------------ dialog actions

  protected ask(action: DialogAction, dispute: Dispute): void {
    this.dialogText = '';
    this.dialogSubmitted.set(false);
    this.error.set(null);
    this.dialog.set({ action, dispute });
  }

  protected dialogTextError(): string | null {
    const pending = this.dialog();
    if (!pending) return null;
    const required = DIALOG_TEXTS[pending.action].textRequired;
    const key = required ? validateDisputeText(this.dialogText) : this.dialogText.length > this.maxLength ? 'disputes.validation.tooLong' : null;
    if (key && this.dialogSubmitted()) return this.i18n.t(key, { max: this.maxLength });
    const err = this.error();
    const messages = err ? fieldErrors(toApiProblem(err), 'text') : [];
    return messages.length ? messages.join(' ') : null;
  }

  protected async confirmDialog(): Promise<void> {
    const pending = this.dialog();
    if (!pending) return;
    this.dialogSubmitted.set(true);
    if (this.dialogTextError()) return;
    const { action, dispute } = pending;
    const text = this.dialogText.trim();
    await this.run(action, async () => {
      switch (action) {
        case 'refund':
          await firstValueFrom(this.api.refund(dispute.id, text));
          this.toast.success(this.i18n.t('disputes.refunded', { number: this.order().number }));
          this.changed.emit({ reloadOrder: true, refetchPaymentsSoon: true });
          break;
        case 'withdraw':
          await firstValueFrom(this.api.withdraw(dispute.id, text));
          this.toast.success(this.i18n.t('disputes.withdrawn', { number: this.order().number }));
          this.changed.emit({ reloadOrder: true, refetchPaymentsSoon: true });
          break;
        case 'escalate':
          await firstValueFrom(this.api.escalate(dispute.id, text));
          this.toast.success(this.i18n.t('disputes.escalated'));
          this.changed.emit({ reloadOrder: false });
          break;
      }
      this.dialog.set(null);
    });
  }

  private async run(name: string, work: () => Promise<void>): Promise<void> {
    if (this.busy()) return;
    this.busy.set(name);
    this.error.set(null);
    try {
      await work();
    } catch (e) {
      this.error.set(e);
      // State may have moved on (closed by the other party, escalated, auto-completed): refresh everything.
      if (toApiProblem(e).status === 409 || toApiProblem(e).status === 404) this.changed.emit({ reloadOrder: true });
    } finally {
      this.busy.set(null);
    }
  }
}
