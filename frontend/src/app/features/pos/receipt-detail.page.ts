import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { PosApi } from '../../core/api/handynet-api';
import { POS_PAYMENT_METHODS, PosPaymentMethod, Receipt } from '../../core/api/models';
import { SessionStore } from '../../core/auth/session.store';
import { I18nService } from '../../core/i18n/i18n.service';
import { ToastService } from '../../core/ui-state/toast.service';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';
import { posErrorText } from './pos-error';
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

/** One receipt as issued (printable) with Storno (`pos.manage`) and Return (select lines/quantities). */
@Component({
  selector: 'hn-pos-receipt-detail-page',
  imports: [FormsModule, RouterLink, PosSubnavComponent, ReceiptViewComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (receipt(); as r) {
      <hn-pos-subnav [registerId]="r.registerId" />
      <hn-page-header class="hn-no-print" [title]="'pos.detail.title' | t: { number: r.number }" [description]="r.registerName">
        <hn-status-badge [status]="r.kind" kind="receiptKind" [tone]="r.kind === 'Sale' ? 'success' : r.kind === 'Storno' ? 'danger' : 'warning'" />
        <button hnButton type="button" (click)="print()" data-testid="pos-detail-print">{{ 'pos.receipt.print' | t }}</button>
      </hn-page-header>
    } @else {
      <hn-page-header [title]="'pos.detail.loading' | t" />
    }
    <hn-banner class="mb-3" [error]="query.error()" />

    @if (receipt(); as r) {
      <div class="grid gap-4 xl:grid-cols-[24rem_minmax(0,1fr)]">
        <hn-receipt-view [doc]="r.document" [cashier]="r.createdByName" />

        <div class="hn-no-print min-w-0 space-y-4">
          <section class="hn-card p-4 text-sm" aria-labelledby="pos-detail-info">
            <h2 id="pos-detail-info" class="mb-2 text-sm">{{ 'pos.detail.info' | t }}</h2>
            <dl class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1">
              @if (r.referencedReceiptId) {
                <dt>{{ 'pos.detail.references' | t }}</dt>
                <dd><a class="hn-link" [routerLink]="['/app/kasse/belege', r.referencedReceiptId]">{{ 'pos.detail.receiptNo' | t: { number: r.referencedReceiptNumber } }}</a></dd>
              }
              @if (r.cancelledByReceiptId) {
                <dt>{{ 'pos.detail.cancelledBy' | t }}</dt>
                <dd><a class="hn-link" [routerLink]="['/app/kasse/belege', r.cancelledByReceiptId]" data-testid="pos-cancelled-by">{{ 'pos.detail.openStorno' | t }}</a></dd>
              }
              @if (r.reason) {
                <dt>{{ 'pos.detail.reason' | t }}</dt>
                <dd>{{ r.reason }}</dd>
              }
              <dt>{{ 'pos.detail.session' | t }}</dt>
              <dd><a class="hn-link" [routerLink]="['/app/kasse/sitzungen', r.sessionId]">{{ 'pos.detail.openSession' | t }}</a></dd>
            </dl>
            @if (r.kind === 'Sale') {
              <div class="mt-3 flex flex-wrap gap-2">
                @if (returnable()) {
                  <button hnButton type="button" class="min-h-11" (click)="startReturn()" [disabled]="returning()" data-testid="pos-start-return">{{ 'pos.detail.return' | t }}</button>
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
            <section class="hn-card" aria-labelledby="pos-detail-lines">
              <h2 id="pos-detail-lines" class="border-b border-slate-200 px-4 py-3 text-sm">{{ returning() ? ('pos.detail.returnTitle' | t) : ('pos.detail.lines' | t) }}</h2>
              <div class="overflow-x-auto">
                <table class="hn-table" data-testid="pos-detail-lines">
                  <thead>
                    <tr>
                      <th scope="col" class="num">#</th>
                      <th scope="col">{{ 'common.product' | t }}</th>
                      <th scope="col" class="num">{{ 'pos.detail.sold' | t }}</th>
                      <th scope="col" class="num">{{ 'pos.detail.returned' | t }}</th>
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
                        <td class="num">{{ l.lineNumber }}</td>
                        <td>
                          {{ l.description }}
                          @if (l.imei) {
                            <span class="hn-mono block text-slate-500">IMEI {{ l.imei }}</span>
                          }
                        </td>
                        <td class="num">{{ l.quantity }}</td>
                        <td class="num">{{ l.returnedQuantity }}</td>
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
              <button hnButton type="button" (click)="closeDialog()">{{ 'common.cancel' | t }}</button>
              <button hnButton variant="danger" type="submit" [loading]="busy()" [disabled]="busy()" data-testid="pos-storno-confirm">{{ 'pos.detail.stornoConfirm' | t }}</button>
            </div>
          </form>
        } @else if (dialog() === 'return') {
          <p class="text-sm">{{ 'pos.detail.returnConfirmText' | t: { amount: (check().refundMinor | money), method: (refundMethod | tEnum: 'posPaymentMethod') } }}</p>
          @if (dialogError()) {
            <p class="mt-3 rounded-md border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-900" role="alert" data-testid="pos-return-dialog-error">{{ dialogError() }}</p>
          }
          <div class="mt-4 flex justify-end gap-2">
            <button hnButton type="button" (click)="closeDialog()">{{ 'common.cancel' | t }}</button>
            <button hnButton variant="primary" type="button" [loading]="busy()" [disabled]="busy()" (click)="confirmReturn(r)" data-testid="pos-return-confirm">{{ 'common.confirm' | t }}</button>
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

  readonly id = input.required<string>();

  protected readonly methods = POS_PAYMENT_METHODS;
  protected readonly maxReason = POS_LIMITS.receiptReasonLength;
  protected readonly query = createQuery(
    () => this.id(),
    (id) => this.api.receipt(id),
  );
  protected readonly receipt = computed(() => this.query.value());
  protected readonly canManage = computed(() => this.session.can('pos.manage'));
  protected readonly stornoAllowed = computed(() => {
    const r = this.receipt();
    return !!r && this.canManage() && canStorno(r);
  });
  protected readonly returnable = computed(() => {
    const r = this.receipt();
    return !!r && this.session.can('pos.sell') && canReturn(r);
  });

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

  protected stornoReason = '';
  protected readonly stornoReasonError = signal<string | null>(null);
  protected readonly dialog = signal<'storno' | 'return' | null>(null);
  protected readonly dialogError = signal<string | null>(null);
  protected readonly busy = signal(false);

  protected readonly returnableOf = returnableQuantity;
  protected readonly refundOf = refundPreviewMinor;

  protected print(): void {
    if (typeof window !== 'undefined') window.print();
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
    const body = { lines: c.lines, refundMethod: this.refundMethod, reason: this.returnReason.trim() };
    const attempt = attemptFor(this.pendingReturn, JSON.stringify(body), newIdempotencyKey);
    this.pendingReturn = attempt;
    await this.run(async () => {
      try {
        const created = await firstValueFrom(this.api.returnItems(r.id, body, attempt.key));
        this.pendingReturn = null;
        this.toast.success(this.i18n.t('pos.detail.returnDone', { number: created.number }));
        this.returning.set(false);
        await this.router.navigate(['/app/kasse/belege', created.id]);
      } catch (e) {
        if (!keepAttemptAfter(problemStatus(e))) this.pendingReturn = null;
        throw e;
      }
    });
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
      await this.router.navigate(['/app/kasse/belege', created.id]);
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
      this.dialogError.set(posErrorText(this.i18n, e));
      this.query.reload();
    } finally {
      this.busy.set(false);
    }
  }
}
