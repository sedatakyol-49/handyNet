import { ChangeDetectionStrategy, Component, Injector, afterNextRender, computed, inject, input, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { PosApi } from '../../core/api/handynet-api';
import { POS_PAYMENT_METHODS, PosPaymentMethod, Receipt, ReturnRequest } from '../../core/api/models';
import { SessionStore } from '../../core/auth/session.store';
import { I18nService } from '../../core/i18n/i18n.service';
import { ToastService } from '../../core/ui-state/toast.service';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';
import { posErrorText } from './pos-error';
import { PosCashierStore } from './pos-cashier.store';
import { PosContext } from './pos-context';
import { posRights } from './pos-rights';
import {
  PendingAttempt,
  POS_LIMITS,
  ReturnSelection,
  attemptFor,
  canReturn,
  canStorno,
  checkReturn,
  keepAttemptAfter,
  newIdempotencyKey,
  problemStatus,
  refundPreviewMinor,
  returnableQuantity,
} from './pos.logic';
import { PosSubnavComponent } from './pos-subnav';
import { ReceiptViewComponent } from './receipt-view';
import { PosEReceiptComponent } from './pos-ereceipt';
import { PosInvoiceComponent } from './pos-invoice';
import { PrinterService } from './printing/printer.service';
import { OrderCard } from './terminals/card-flow';
import { TerminalService } from './terminals/terminal.service';
import { formatMoney } from '../../core/format/money';

/** One receipt as issued (printable) with Storno (`pos.manage`) and Return (select lines/quantities). */
@Component({
  selector: 'hn-pos-receipt-detail-page',
  host: { class: 'pos-page block' },
  imports: [FormsModule, RouterLink, PosSubnavComponent, ReceiptViewComponent, PosEReceiptComponent, PosInvoiceComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (receipt(); as r) {
      <hn-pos-subnav [registerId]="r.registerId" />
      <hn-page-header class="hn-no-print" [title]="'pos.detail.title' | t: { number: r.number }" [description]="r.registerName">
        <hn-status-badge [status]="r.kind" kind="receiptKind" [tone]="r.kind === 'Sale' ? 'success' : r.kind === 'Storno' ? 'danger' : 'warning'" />
        <button hnButton class="min-h-11" type="button" [loading]="reprinting()" [disabled]="reprinting()" (click)="reprint(r)" data-testid="pos-detail-reprint">
          {{ 'pos.detail.reprint' | t }}
        </button>
      </hn-page-header>
    } @else {
      <hn-page-header [title]="'pos.detail.loading' | t" />
    }
    <hn-banner class="mb-3" [error]="query.error()" />
    <hn-banner class="mb-3" [error]="reprintError()" />

    @if (receipt(); as r) {
      <div class="grid gap-4 xl:grid-cols-[24rem_minmax(0,1fr)]">
        <hn-receipt-view [doc]="r.document" [cashier]="r.createdByName" [copy]="copy()" />

        <div class="hn-no-print min-w-0 space-y-4">
          <hn-pos-ereceipt [receipt]="r" />
          @if (r.kind === 'Sale') {
            <hn-pos-invoice [receipt]="r" />
          }
          <section class="pos-card p-4 text-sm" aria-labelledby="pos-detail-info">
            <h2 id="pos-detail-info" class="mb-2 text-sm">{{ 'pos.detail.info' | t }}</h2>
            <dl class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1">
              @if (r.referencedReceiptId) {
                <dt>{{ 'pos.detail.references' | t }}</dt>
                <dd><a class="hn-link" [routerLink]="['/kasse/belege', r.referencedReceiptId]">{{ 'pos.detail.receiptNo' | t: { number: r.referencedReceiptNumber } }}</a></dd>
              }
              @if (r.cancelledByReceiptId) {
                <dt>{{ 'pos.detail.cancelledBy' | t }}</dt>
                <dd><a class="hn-link" [routerLink]="['/kasse/belege', r.cancelledByReceiptId]" data-testid="pos-cancelled-by">{{ 'pos.detail.openStorno' | t }}</a></dd>
              }
              @if (r.reason) {
                <dt>{{ 'pos.detail.reason' | t }}</dt>
                <dd>{{ r.reason }}</dd>
              }
              <dt>{{ 'pos.detail.session' | t }}</dt>
              <dd><a class="hn-link inline-flex min-h-11 items-center" [routerLink]="['/kasse/sitzungen', r.sessionId]">{{ 'pos.detail.openSession' | t }}</a></dd>
              @if (r.customer; as c) {
                <dt>{{ 'pos.receipt.customer' | t }}</dt>
                <dd>
                  <a class="hn-link" [routerLink]="['/kasse', r.registerId, 'belege']" [queryParams]="{ scope: 'all', kunde: c.id, kundeName: c.name }" data-testid="pos-detail-customer">{{ c.name }}</a>
                </dd>
              }
              <dt>{{ 'pos.detail.reprints' | t }}</dt>
              <dd data-testid="pos-detail-reprint-count">
                {{ r.reprintCount ?? 0 }}
                @if (r.lastReprintedAt) {
                  <span class="text-xs text-slate-500">({{ 'pos.detail.lastReprint' | t: { date: (r.lastReprintedAt | hnDate) } }})</span>
                }
              </dd>
            </dl>
            @if (r.kind === 'Sale') {
              <div class="mt-3 flex flex-wrap gap-2">
                @if (returnable()) {
                  <button hnButton type="button" class="min-h-11" (click)="startReturn()" [disabled]="returning()" data-testid="pos-start-return">{{ 'pos.detail.return' | t }}</button>
                } @else if (refundNeedsApproval()) {
                  <button hnButton type="button" class="min-h-11" (click)="ctx?.requestApproval(['pos.refund'])" data-testid="pos-approval-refund">
                    {{ 'pos.approval.refund' | t }}
                  </button>
                }
                @if (stornoAllowed()) {
                  <button hnButton variant="danger" type="button" class="min-h-11" (click)="openStorno()" data-testid="pos-start-storno">{{ 'pos.detail.storno' | t }}</button>
                } @else if (canManage() && !r.cancelledByReceiptId) {
                  <p class="text-xs text-slate-500">{{ 'pos.detail.stornoHasReturns' | t }}</p>
                }
              </div>
            }
          </section>

          @if (r.kind === 'Sale') {
            <section class="pos-card" aria-labelledby="pos-detail-lines">
              <h2 id="pos-detail-lines" class="border-b border-slate-200 px-4 py-3 text-sm">{{ returning() ? ('pos.detail.returnTitle' | t) : ('pos.detail.lines' | t) }}</h2>
              <div class="overflow-x-auto">
                <table class="hn-table" data-testid="pos-detail-lines">
                  <thead>
                    <tr>
                      <th scope="col" class="num max-sm:hidden">#</th>
                      <th scope="col">{{ 'common.product' | t }}</th>
                      <th scope="col" class="num">{{ 'pos.detail.sold' | t }}</th>
                      <th scope="col" class="num max-sm:hidden">{{ 'pos.detail.returned' | t }}</th>
                      <th scope="col" class="num">{{ 'pos.till.lineTotal' | t }}</th>
                      @if (returning()) {
                        <th scope="col" class="num">{{ 'pos.detail.returnQty' | t }}</th>
                        <th scope="col" class="num">{{ 'pos.detail.refund' | t }}</th>
                      }
                    </tr>
                  </thead>
                  <tbody>
                    @for (l of r.lines; track l.lineNumber) {
                      @let max = returnableOf(l);
                      <tr>
                        <td class="num max-sm:hidden">{{ l.lineNumber }}</td>
                        <td>
                          {{ l.description }}
                          @if (l.imei) {
                            <span class="hn-mono block text-slate-500">IMEI {{ l.imei }}</span>
                          }
                        </td>
                        <td class="num">{{ l.quantity }}</td>
                        <td class="num max-sm:hidden">{{ l.returnedQuantity }}</td>
                        <td class="num">{{ l.lineGrossMinor | money }}</td>
                        @if (returning()) {
                          <td class="num">
                            @if (max > 0) {
                              <label class="sr-only" [for]="'pos-ret-' + l.lineNumber">{{ 'pos.detail.returnQtyFor' | t: { name: l.description, max: max } }}</label>
                              <input [id]="'pos-ret-' + l.lineNumber" type="number" min="0" [max]="max" step="1" class="hn-control h-11 w-20 text-end"
                                [value]="selection()[l.lineNumber] ?? 0" (input)="setReturnQty(l.lineNumber, $any($event.target).value, max)" data-testid="pos-return-qty" />
                              <span class="block text-xs text-slate-500">{{ 'pos.detail.max' | t: { max: max } }}</span>
                            } @else {
                              <span class="text-xs text-slate-500">{{ 'pos.detail.nothingLeft' | t }}</span>
                            }
                          </td>
                          <td class="num">{{ refundOf(l, selection()[l.lineNumber] ?? 0) | money }}</td>
                        }
                      </tr>
                    }
                  </tbody>
                </table>
              </div>

              @if (returning()) {
                <form class="space-y-3 border-t border-slate-200 p-4" (ngSubmit)="askReturn()" novalidate>
                  <fieldset>
                    <legend class="mb-1 text-xs font-medium text-slate-700">{{ 'pos.detail.refundMethod' | t }}</legend>
                    <div class="flex gap-4">
                      @for (m of methods; track m) {
                        <label class="flex min-h-11 items-center gap-2 text-sm">
                          <input type="radio" name="refundMethod" class="hn-checkbox" [value]="m" [(ngModel)]="refundMethod" [attr.data-testid]="'pos-refund-' + m" />
                          {{ m | tEnum: 'posPaymentMethod' }}
                        </label>
                      }
                    </div>
                  </fieldset>
                  <hn-field [label]="'pos.detail.reason' | t" for="pos-return-reason" [required]="true">
                    <input hnInput id="pos-return-reason" name="returnReason" [maxlength]="maxReason" [(ngModel)]="returnReason" data-testid="pos-return-reason" />
                  </hn-field>
                  <p class="flex justify-between text-base font-semibold">
                    <span>{{ 'pos.detail.refundTotal' | t }}</span><span class="tabular-nums" data-testid="pos-refund-total">{{ check().refundMinor | money }}</span>
                  </p>
                  <p class="text-xs text-slate-500">{{ 'pos.detail.refundHint' | t }}</p>
                  @if (returnError()) {
                    <p class="rounded-md border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-900" role="alert" data-testid="pos-return-error">{{ returnError() }}</p>
                  }
                  <div class="flex justify-end gap-2">
                    <button hnButton type="button" class="min-h-11" (click)="returning.set(false)">{{ 'common.cancel' | t }}</button>
                    <button hnButton variant="primary" type="submit" class="min-h-11" data-testid="pos-return-submit">{{ 'pos.detail.returnSubmit' | t }}</button>
                  </div>
                </form>
              }
            </section>
          }
        </div>
      </div>

      <hn-dialog [title]="(dialog() === 'storno' ? 'pos.detail.stornoTitle' : 'pos.detail.returnConfirmTitle') | t" [open]="!!dialog()" (closed)="closeDialog()">
        @if (dialog() === 'storno') {
          <form (ngSubmit)="confirmStorno(r)" novalidate>
            <p class="mb-3 text-sm">{{ 'pos.detail.stornoText' | t: { number: r.number, amount: (r.totalGrossMinor | money) } }}</p>
            <hn-field [label]="'pos.detail.reason' | t" for="pos-storno-reason" [required]="true" [errors]="stornoReasonError()">
              <input hnInput id="pos-storno-reason" name="stornoReason" [maxlength]="maxReason" [(ngModel)]="stornoReason" data-testid="pos-storno-reason" />
            </hn-field>
            @if (dialogError()) {
              <p class="mt-3 rounded-md border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-900" role="alert">{{ dialogError() }}</p>
            }
            <div class="mt-4 flex justify-end gap-2">
              <button hnButton class="min-h-11" type="button" (click)="closeDialog()">{{ 'common.cancel' | t }}</button>
              <button hnButton class="min-h-11" variant="danger" type="submit" [loading]="busy()" [disabled]="busy()" data-testid="pos-storno-confirm">{{ 'pos.detail.stornoConfirm' | t }}</button>
            </div>
          </form>
        } @else if (dialog() === 'return') {
          <p class="text-sm">{{ 'pos.detail.returnConfirmText' | t: { amount: (check().refundMinor | money), method: (refundMethod | tEnum: 'posPaymentMethod') } }}</p>
          @if (dialogError()) {
            <p class="mt-3 rounded-md border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-900" role="alert" data-testid="pos-return-dialog-error">{{ dialogError() }}</p>
          }
          <div class="mt-4 flex justify-end gap-2">
            <button hnButton class="min-h-11" type="button" (click)="closeDialog()">{{ 'common.cancel' | t }}</button>
            <button hnButton class="min-h-11" variant="primary" type="button" [loading]="busy()" [disabled]="busy()" (click)="confirmReturn(r)" data-testid="pos-return-confirm">{{ 'common.confirm' | t }}</button>
          </div>
        }
      </hn-dialog>
    }
  `,
})
export class PosReceiptDetailPage {
  private readonly api = inject(PosApi);
  private readonly session = inject(SessionStore);
  private readonly i18n = inject(I18nService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly printer = inject(PrinterService);
  private readonly terminals = inject(TerminalService);

  readonly id = input.required<string>();

  protected readonly methods = POS_PAYMENT_METHODS;
  protected readonly maxReason = POS_LIMITS.receiptReasonLength;
  protected readonly query = createQuery(
    () => this.id(),
    (id) => this.api.receipt(id),
  );
  protected readonly receipt = computed(() => this.query.value());
  protected readonly cashiers = inject(PosCashierStore);
  /** Inside the POS app (a manager can approve a refund on the spot); null in the back office. */
  protected readonly ctx = inject(PosContext, { optional: true });
  private readonly rights = computed(() => posRights(this.cashiers.membership()));
  protected readonly canManage = computed(() => this.rights().manage);
  protected readonly stornoAllowed = computed(() => {
    const r = this.receipt();
    return !!r && this.canManage() && canStorno(r);
  });
  protected readonly returnable = computed(() => {
    const r = this.receipt();
    return !!r && this.rights().refund && canReturn(r);
  });
  /** Returnable, but the cashier lacks the refund right: a manager may approve it. */
  protected readonly refundNeedsApproval = computed(() => {
    const r = this.receipt();
    return !!r && !!this.ctx && !this.rights().refund && canReturn(r);
  });

  /** Copy number shown on the receipt after a recorded reprint ("Kopie n"). */
  protected readonly copy = signal<number | null>(null);
  protected readonly reprinting = signal(false);
  protected readonly reprintError = signal<unknown>(null);

  protected readonly returning = signal(false);
  protected readonly selection = signal<ReturnSelection>({});
  protected refundMethod: PosPaymentMethod = 'Cash';
  protected returnReason = '';
  private readonly reasonSignal = signal('');
  protected readonly check = computed(() => {
    const r = this.receipt();
    return r ? checkReturn(r, this.selection(), this.reasonSignal()) : { lines: [], refundMinor: 0, error: 'nothingSelected' as const };
  });
  protected readonly returnError = signal<string | null>(null);
  private pendingReturn: PendingAttempt | null = null;
  /** Refund the terminal approved for this return and that no receipt uses yet (kept for a retry of the same return). */
  private refundCard: OrderCard | null = null;
  /** The terminal's answer to the refund was lost; the cashier confirms the next attempt after checking the terminal. */
  private refundManual = false;

  protected stornoReason = '';
  protected readonly stornoReasonError = signal<string | null>(null);
  protected readonly dialog = signal<'storno' | 'return' | null>(null);
  protected readonly dialogError = signal<string | null>(null);
  protected readonly busy = signal(false);

  protected readonly returnableOf = returnableQuantity;
  protected readonly refundOf = refundPreviewMinor;

  /** Records the reprint (copy number from the API), marks the receipt "Kopie n" and prints it. */
  protected async reprint(r: Receipt): Promise<void> {
    this.reprinting.set(true);
    this.reprintError.set(null);
    try {
      const copy = await firstValueFrom(this.api.reprint(r.id));
      this.query.set(copy.receipt);
      this.copy.set(copy.sequence);
      this.toast.success(this.i18n.t('pos.detail.reprinted', { sequence: copy.sequence }));
      // With the device's printer profile (ADR-0018); browser print without one or after a printer error.
      afterNextRender(() => void this.printer.printReceipt(copy.receipt, { copy: copy.sequence }), { injector: this.injector });
    } catch (e) {
      this.reprintError.set(e);
    } finally {
      this.reprinting.set(false);
    }
  }

  // ---- return
  protected startReturn(): void {
    this.selection.set({});
    this.returnReason = '';
    this.refundMethod = 'Cash';
    this.returnError.set(null);
    this.pendingReturn = null;
    this.returning.set(true);
  }

  protected setReturnQty(lineNumber: number, raw: string, max: number): void {
    const n = Math.trunc(Number(raw));
    const q = Number.isFinite(n) ? Math.min(Math.max(n, 0), max) : 0;
    this.selection.update((s) => ({ ...s, [lineNumber]: q }));
    this.returnError.set(null);
  }

  protected askReturn(): void {
    this.reasonSignal.set(this.returnReason);
    const c = this.check();
    if (c.error) {
      this.returnError.set(this.i18n.t('pos.detail.returnError.' + c.error));
      return;
    }
    this.returnError.set(null);
    this.dialogError.set(null);
    this.dialog.set('return');
  }

  protected async confirmReturn(r: Receipt): Promise<void> {
    const c = this.check();
    if (c.error) return;
    const body: ReturnRequest = { lines: c.lines, refundMethod: this.refundMethod, reason: this.returnReason.trim() };
    await this.run(async () => {
      // Card refund inside the till: run it on the terminal first (the back office has no terminal → confirmed by hand).
      if (this.refundMethod === 'Card' && this.ctx) {
        const cardId = await this.refundOnTerminal(r, c.refundMinor);
        if (cardId) body.cardTransactionId = cardId;
      }
      const attempt = attemptFor(this.pendingReturn, JSON.stringify(body), newIdempotencyKey);
      this.pendingReturn = attempt;
      try {
        const created = await firstValueFrom(this.api.returnItems(r.id, body, attempt.key));
        this.pendingReturn = null;
        this.refundCard = null;
        this.refundManual = false;
        this.toast.success(this.i18n.t('pos.detail.returnDone', { number: created.number }));
        this.returning.set(false);
        await this.router.navigate(['/kasse/belege', created.id]);
      } catch (e) {
        if (!keepAttemptAfter(problemStatus(e))) this.pendingReturn = null;
        // The refund already went to the card: say so. It stays stored and is reused when this return is confirmed again.
        if (this.refundCard) throw new Error(this.i18n.t('pos.terminals.flow.refundKept', { reason: posErrorText(this.i18n, e) ?? '', amount: formatMoney(this.refundCard.amountMinor) }));
        throw e;
      }
    });
  }

  /**
   * Runs the card refund on the device's terminal. Returns the card transaction to send with the return, or null when
   * the refund is confirmed by hand (no terminal). Throws with a text for the dialog when the return must not continue.
   */
  private async refundOnTerminal(r: Receipt, refundMinor: number): Promise<string | null> {
    let card = this.refundCard;
    if (card && card.amountMinor !== refundMinor) {
      throw new Error(this.i18n.t('pos.terminals.flow.refundAmountChanged', { amount: formatMoney(card.amountMinor) }));
    }
    if (!card) {
      if (this.refundManual) return null;
      const run = await this.terminals.charge(r.registerId, 'Refund', refundMinor);
      if (run.kind === 'manual') return null;
      if (run.kind === 'uncertain') {
        this.refundManual = true;
        throw new Error(`${run.text} ${this.i18n.t('pos.terminals.flow.refundUncertain')}`);
      }
      if (run.kind !== 'approved') throw new Error(run.text);
      card = this.refundCard = run.card;
    }
    if (!card.reported && card.pendingResult) {
      if (!(await this.terminals.report(card.transactionId, card.pendingResult))) {
        throw new Error(this.i18n.t('pos.terminals.flow.unreported', { amount: formatMoney(card.amountMinor) }));
      }
      card = this.refundCard = { ...card, reported: true, pendingResult: null };
    }
    return card.transactionId;
  }

  // ---- storno
  protected openStorno(): void {
    this.stornoReason = '';
    this.stornoReasonError.set(null);
    this.dialogError.set(null);
    this.dialog.set('storno');
  }

  protected async confirmStorno(r: Receipt): Promise<void> {
    const reason = this.stornoReason.trim();
    this.stornoReasonError.set(reason ? null : this.i18n.t('pos.detail.reasonRequired'));
    if (!reason) return;
    await this.run(async () => {
      const created = await firstValueFrom(this.api.storno(r.id, { reason }));
      this.toast.success(this.i18n.t('pos.detail.stornoDone', { number: created.number }));
      await this.router.navigate(['/kasse/belege', created.id]);
    });
  }

  protected closeDialog(): void {
    if (!this.busy()) this.dialog.set(null);
  }

  private async run(fn: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.dialogError.set(null);
    try {
      await fn();
      this.dialog.set(null);
    } catch (e) {
      // Plain errors carry a ready text (card terminal); API errors are translated.
      this.dialogError.set(e instanceof Error && !(e instanceof HttpErrorResponse) ? e.message : posErrorText(this.i18n, e));
      this.query.reload();
    } finally {
      this.busy.set(false);
    }
  }
}
