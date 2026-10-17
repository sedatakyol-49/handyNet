import { ChangeDetectionStrategy, Component, computed, inject, input, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { PosApi } from '../../core/api/handynet-api';
import { CashMovementKind, CashMovementReason, CashSession } from '../../core/api/models';
import { SessionStore } from '../../core/auth/session.store';
import { I18nService } from '../../core/i18n/i18n.service';
import { ToastService } from '../../core/ui-state/toast.service';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';
import { CashCounts, countTotal, toDenominations } from './cash-count';
import { posErrorText } from './pos-error';
import { PosCashCountComponent } from './pos-cash-count';
import { canSeeSessionReport, posRights } from './pos-rights';
import { POS_LIMITS, cashDifference, parseAmount } from './pos.logic';
import { PosSubnavComponent } from './pos-subnav';
import { ZReportComponent } from './z-report';

/** Translation key of a predefined cash movement reason (falls back to the API's German label). */
export function reasonKey(r: Pick<CashMovementReason, 'code' | 'kind'>): string {
  return r.code === 'Other' ? `pos.reasons.Other${r.kind}` : `pos.reasons.${r.code}`;
}

/**
 * Cash session: cash movements (`pos.cash_movement`, predefined reasons) and closing (`pos.close_session`, counted as
 * an amount or by denomination) while open, the Z-report afterwards; link to the session's sales report.
 */
@Component({
  selector: 'hn-pos-session-page',
  host: { class: 'pos-page block' },
  imports: [FormsModule, RouterLink, PosCashCountComponent, PosSubnavComponent, ZReportComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (session(); as s) {
      <hn-pos-subnav [registerId]="s.registerId" />
      <hn-page-header class="hn-no-print" [title]="'pos.session.title' | t: { register: s.registerName }" [description]="'pos.session.openedAt' | t: { date: (s.openedAt | hnDate) }">
        <hn-status-badge [status]="s.status" kind="posSessionStatus" [tone]="s.status === 'Open' ? 'success' : 'neutral'" />
        @if (s.status === 'Open') {
          <a hnButton class="min-h-11" variant="primary" [routerLink]="['/kasse', s.registerId]">{{ 'pos.session.toTill' | t }}</a>
        } @else {
          <button hnButton class="min-h-11" type="button" (click)="print()" data-testid="z-print">{{ 'pos.z.print' | t }}</button>
        }
        @if (canReport()) {
          <a hnButton class="min-h-11" [routerLink]="['/kasse/berichte']" [queryParams]="{ sitzung: s.id }" data-testid="pos-session-report">{{ 'pos.report.open' | t }}</a>
        }
        <a hnButton class="min-h-11" [routerLink]="['/kasse', s.registerId, 'belege']">{{ 'pos.subnav.receipts' | t }}</a>
      </hn-page-header>
    } @else {
      <hn-page-header [title]="'pos.session.loading' | t" />
    }
    <hn-banner class="mb-3" [error]="query.error()" />

    @if (session(); as s) {
      <div class="grid gap-4 xl:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
        <hn-z-report [session]="s" [printable]="s.status === 'Closed'" />

        <div class="hn-no-print min-w-0 space-y-4">
          @if (s.status === 'Open') {
            <!-- ======================================================== cash movement -->
            @if (rights().cashMovement) {
              <section class="pos-card p-4" aria-labelledby="pos-mv-heading">
                <h2 id="pos-mv-heading" class="mb-3 text-sm">{{ 'pos.session.movementTitle' | t }}</h2>
                <form class="space-y-3" (ngSubmit)="addMovement(s)" novalidate>
                  <div class="grid grid-cols-2 gap-1.5" role="group" [attr.aria-label]="'pos.session.movementKind' | t">
                    @for (k of kinds; track k) {
                      <button type="button" class="pos-mode" [attr.aria-pressed]="movementKind() === k" (click)="setKind(k)" [attr.data-testid]="'pos-mv-' + k">
                        {{ k | tEnum: 'cashMovementKind' }}
                      </button>
                    }
                  </div>
                  <div class="grid gap-3 sm:grid-cols-[10rem_minmax(0,1fr)]">
                    <hn-field [label]="'pos.session.amount' | t" for="pos-mv-amount" [required]="true">
                      <input hnInput id="pos-mv-amount" name="amount" inputmode="decimal" autocomplete="off" class="h-11 text-end tabular-nums" placeholder="0,00"
                        [(ngModel)]="movementAmount" data-testid="pos-mv-amount" />
                    </hn-field>
                    <hn-field [label]="'pos.session.reasonCode' | t" for="pos-mv-code" [required]="true">
                      <select hnSelect id="pos-mv-code" name="code" class="h-11" [value]="movementCode()" (change)="movementCode.set($any($event.target).value)" data-testid="pos-mv-code">
                        @for (r of reasonsForKind(); track r.code) {
                          <option [value]="r.code" [selected]="r.code === movementCode()">{{ reasonLabel(r) }}</option>
                        }
                        @if (!reasonsForKind().length) {
                          <option value="">{{ 'pos.session.reasonFree' | t }}</option>
                        }
                      </select>
                    </hn-field>
                  </div>
                  <hn-field [label]="'pos.session.reasonText' | t" for="pos-mv-reason" [required]="textRequired()" [hint]="textRequired() ? null : ('pos.session.reasonOptional' | t)">
                    <input hnInput id="pos-mv-reason" name="reason" class="h-11" [maxlength]="maxMovementReason" [(ngModel)]="movementReason" data-testid="pos-mv-reason" />
                  </hn-field>
                  @if (movementError()) {
                    <p class="rounded-md border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-900" role="alert" data-testid="pos-mv-error">{{ movementError() }}</p>
                  }
                  <div class="flex justify-end">
                    <button hnButton variant="primary" type="submit" class="min-h-11" [loading]="busy() === 'movement'" [disabled]="!!busy()" data-testid="pos-mv-submit">
                      {{ 'pos.session.movementSubmit' | t }}
                    </button>
                  </div>
                </form>
              </section>
            }

            <!-- ======================================================== closing -->
            @if (rights().closeSession) {
              <section class="pos-card p-4" aria-labelledby="pos-close-heading" data-testid="pos-close">
                <h2 id="pos-close-heading" class="mb-1 text-sm">{{ 'pos.session.closeTitle' | t }}</h2>
                <p class="mb-3 text-xs text-slate-600">{{ 'pos.session.closeText' | t }}</p>
                <div class="mb-3 grid grid-cols-2 gap-1.5" role="group" [attr.aria-label]="'pos.cashCount.modeLabel' | t">
                  <button type="button" class="pos-mode" [attr.aria-pressed]="closeMode() === 'amount'" (click)="closeMode.set('amount')" data-testid="pos-close-mode-amount">{{ 'pos.cashCount.modeAmount' | t }}</button>
                  <button type="button" class="pos-mode" [attr.aria-pressed]="closeMode() === 'count'" (click)="closeMode.set('count')" data-testid="pos-close-mode-count">{{ 'pos.cashCount.modeCount' | t }}</button>
                </div>
                <form class="space-y-3" (ngSubmit)="askClose(s)" novalidate>
                  @if (closeMode() === 'amount') {
                    <hn-field [label]="'pos.session.counted' | t" for="pos-counted" [required]="true" [errors]="countedError()">
                      <input hnInput id="pos-counted" name="counted" inputmode="decimal" autocomplete="off" class="h-12 text-end text-lg tabular-nums" placeholder="0,00"
                        [value]="counted()" (input)="counted.set($any($event.target).value); countedError.set(null)"
                        [attr.aria-invalid]="!!countedError()" [attr.aria-describedby]="countedError() ? 'pos-counted-error' : null" data-testid="pos-counted" />
                    </hn-field>
                  } @else {
                    <hn-pos-cash-count [(counts)]="closeCounts" idPrefix="pos-close-count-" testId="pos-closing-count" />
                    @if (countedError()) {
                      <p class="text-sm text-red-700" role="alert">{{ countedError() }}</p>
                    }
                  }
                  <dl class="grid grid-cols-[1fr_auto] gap-x-4 gap-y-1 text-sm">
                    <dt>{{ 'pos.z.expected' | t }}</dt>
                    <dd class="text-end tabular-nums">{{ s.expectedCashMinor | money }}</dd>
                    <dt class="font-semibold">{{ 'pos.z.difference' | t }}</dt>
                    <dd class="text-end font-semibold tabular-nums" [class.text-red-700]="(difference() ?? 0) < 0" data-testid="pos-close-difference">
                      {{ difference() === null ? '–' : (difference() | signedMoney) }}
                    </dd>
                  </dl>
                  <hn-field [label]="'pos.session.note' | t" for="pos-close-note">
                    <textarea hnInput id="pos-close-note" name="note" rows="2" [maxlength]="maxNote" [(ngModel)]="note"></textarea>
                  </hn-field>
                  <div class="flex justify-end">
                    <button hnButton variant="danger" type="submit" class="min-h-11" [loading]="busy() === 'refresh'" [disabled]="!!busy()" data-testid="pos-close-submit">
                      {{ 'pos.session.closeSubmit' | t }}
                    </button>
                  </div>
                </form>
              </section>
            } @else {
              <hn-banner tone="info" [text]="'pos.rights.closeForbidden' | t" data-testid="pos-close-forbidden" />
            }
          } @else {
            <hn-banner tone="success" [text]="'pos.session.closedText' | t: { date: (s.closedAt | hnDate) }" data-testid="pos-session-closed" />
          }

          <!-- ======================================================== movements -->
          <section class="pos-card" aria-labelledby="pos-mvlist-heading">
            <h2 id="pos-mvlist-heading" class="border-b border-slate-200 px-4 py-3 text-sm">{{ 'pos.session.movements' | t }}</h2>
            @if (s.movements.length) {
              <div class="overflow-x-auto">
                <table class="hn-table" data-testid="pos-movements">
                  <thead>
                    <tr>
                      <th scope="col">{{ 'pos.receipt.date' | t }}</th>
                      <th scope="col">{{ 'pos.session.movementKind' | t }}</th>
                      <th scope="col">{{ 'pos.session.reason' | t }}</th>
                      <th scope="col">{{ 'pos.receipt.cashier' | t }}</th>
                      <th scope="col" class="num">{{ 'pos.session.amount' | t }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (m of s.movements; track m.id) {
                      <tr>
                        <td class="text-xs whitespace-nowrap">{{ m.createdAt | hnDate }}</td>
                        <td>{{ m.kind | tEnum: 'cashMovementKind' }}</td>
                        <td>{{ m.reason }}</td>
                        <td class="text-xs">{{ m.createdByName ?? '–' }}</td>
                        <td class="num">{{ (m.kind === 'Withdrawal' ? -m.amountMinor : m.amountMinor) | signedMoney }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            } @else {
              <p class="px-4 py-6 text-center text-sm text-slate-500">{{ 'pos.session.noMovements' | t }}</p>
            }
          </section>
        </div>
      </div>

      <hn-dialog [title]="'pos.session.confirmTitle' | t" [open]="confirmOpen()" (closed)="closeDialog()">
        <p class="mb-3 text-sm">{{ 'pos.session.confirmText' | t }}</p>
        <dl class="grid grid-cols-[1fr_auto] gap-x-4 gap-y-1 text-sm" data-testid="pos-close-confirm-figures">
          <dt>{{ 'pos.z.expected' | t }}</dt>
          <dd class="text-end tabular-nums">{{ s.expectedCashMinor | money }}</dd>
          <dt>{{ 'pos.z.counted' | t }}</dt>
          <dd class="text-end tabular-nums" data-testid="pos-confirm-counted">{{ countedMinor() | money }}</dd>
          <dt class="font-semibold">{{ 'pos.z.difference' | t }}</dt>
          <dd class="text-end font-semibold tabular-nums" [class.text-red-700]="(difference() ?? 0) < 0" data-testid="pos-confirm-difference">{{ difference() | signedMoney }}</dd>
        </dl>
        @if (closeError()) {
          <p class="mt-3 rounded-md border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-900" role="alert">{{ closeError() }}</p>
        }
        <div class="mt-4 flex justify-end gap-2">
          <button hnButton class="min-h-11" type="button" (click)="closeDialog()">{{ 'common.cancel' | t }}</button>
          <button hnButton class="min-h-11" variant="danger" type="button" [loading]="busy() === 'close'" [disabled]="!!busy()" (click)="close(s)" data-testid="pos-close-confirm">
            {{ 'pos.session.closeConfirm' | t }}
          </button>
        </div>
      </hn-dialog>
    }
  `,
})
export class PosSessionPage {
  private readonly api = inject(PosApi);
  private readonly store = inject(SessionStore);
  private readonly i18n = inject(I18nService);
  private readonly toast = inject(ToastService);
  private readonly subnav = viewChild(PosSubnavComponent);

  readonly id = input.required<string>();

  protected readonly kinds: CashMovementKind[] = ['Deposit', 'Withdrawal'];
  protected readonly maxMovementReason = POS_LIMITS.movementReasonLength;
  protected readonly maxNote = POS_LIMITS.closingNoteLength;
  protected readonly rights = computed(() => posRights(this.store.membership()));
  protected readonly canReport = computed(() => canSeeSessionReport(this.rights()));

  protected readonly query = createQuery(
    () => this.id(),
    (id) => this.api.session(id),
  );
  protected readonly session = computed(() => this.query.value());
  protected readonly reasons = createQuery(
    () => true,
    () => this.api.cashMovementReasons(),
  );

  // ---- movement
  protected readonly movementKind = signal<CashMovementKind>('Deposit');
  protected readonly movementCode = signal('');
  protected movementAmount = '';
  protected movementReason = '';
  protected readonly movementError = signal<string | null>(null);
  protected readonly reasonsForKind = computed(() => (this.reasons.value() ?? []).filter((r) => r.kind === this.movementKind()));
  private readonly selectedReason = computed(() => {
    const list = this.reasonsForKind();
    return list.find((r) => r.code === this.movementCode()) ?? list[0] ?? null;
  });
  /** "Other" and movements without predefined reasons need a text. */
  protected readonly textRequired = computed(() => !this.selectedReason() || this.selectedReason()!.requiresText);

  // ---- closing
  protected readonly closeMode = signal<'amount' | 'count'>('amount');
  protected readonly counted = signal('');
  protected readonly closeCounts = signal<CashCounts>({});
  protected readonly countedError = signal<string | null>(null);
  protected note = '';
  protected readonly countedMinor = computed(() => (this.closeMode() === 'count' ? countTotal(this.closeCounts()) : parseAmount(this.counted(), null)));
  protected readonly difference = computed(() => {
    const s = this.session();
    if (!s) return null;
    if (this.closeMode() === 'count') return countTotal(this.closeCounts()) - s.expectedCashMinor;
    return cashDifference(s.expectedCashMinor, this.counted());
  });
  protected readonly confirmOpen = signal(false);
  protected readonly closeError = signal<string | null>(null);
  protected readonly busy = signal<'movement' | 'refresh' | 'close' | null>(null);

  protected print(): void {
    if (typeof window !== 'undefined') window.print();
  }

  protected reasonLabel(r: CashMovementReason): string {
    const key = reasonKey(r);
    return this.i18n.has(key) ? this.i18n.t(key) : r.label;
  }

  protected setKind(k: CashMovementKind): void {
    this.movementKind.set(k);
    this.movementCode.set(this.reasonsForKind()[0]?.code ?? '');
    this.movementError.set(null);
  }

  protected async addMovement(s: CashSession): Promise<void> {
    const amount = parseAmount(this.movementAmount, null);
    const reason = this.movementReason.trim();
    const code = this.selectedReason()?.code ?? null;
    if (amount === null || amount <= 0) return this.movementError.set(this.i18n.t('pos.session.amountInvalid'));
    if (this.textRequired() && !reason) return this.movementError.set(this.i18n.t('pos.session.reasonRequired'));
    this.movementError.set(null);
    this.busy.set('movement');
    try {
      await firstValueFrom(this.api.addMovement(s.id, { kind: this.movementKind(), amountMinor: amount, reason: reason || null, reasonCode: code }));
      this.toast.success(this.i18n.t('pos.session.movementSaved'));
      this.movementAmount = '';
      this.movementReason = '';
      this.query.reload();
    } catch (e) {
      this.movementError.set(posErrorText(this.i18n, e));
    } finally {
      this.busy.set(null);
    }
  }

  /** Re-reads the session first so the confirmation shows the current expected cash (sales may have happened). */
  protected async askClose(s: CashSession): Promise<void> {
    if (this.countedMinor() === null) {
      this.countedError.set(this.i18n.t('pos.session.countedInvalid'));
      return;
    }
    this.busy.set('refresh');
    try {
      this.query.set(await firstValueFrom(this.api.session(s.id)));
      this.closeError.set(null);
      this.confirmOpen.set(true);
    } catch (e) {
      this.countedError.set(posErrorText(this.i18n, e));
    } finally {
      this.busy.set(null);
    }
  }

  protected async close(s: CashSession): Promise<void> {
    const counted = this.countedMinor();
    if (counted === null) return;
    this.busy.set('close');
    this.closeError.set(null);
    const note = this.note.trim() || null;
    const body = this.closeMode() === 'count' ? { denominations: toDenominations(this.closeCounts()), note } : { countedCashMinor: counted, note };
    try {
      this.query.set(await firstValueFrom(this.api.closeSession(s.id, body)));
      this.confirmOpen.set(false);
      this.toast.success(this.i18n.t('pos.session.closed'));
      this.subnav()?.reloadSession();
    } catch (e) {
      this.closeError.set(posErrorText(this.i18n, e));
    } finally {
      this.busy.set(null);
    }
  }

  protected closeDialog(): void {
    if (!this.busy()) this.confirmOpen.set(false);
  }
}
