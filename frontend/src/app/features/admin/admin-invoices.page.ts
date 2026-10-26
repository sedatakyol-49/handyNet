import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { fieldErrors, toApiProblem } from '../../core/api/api-problem';
import { AdminInvoicesApi } from '../../core/api/handynet-api';
import { IssueFailure, IssueRunResult } from '../../core/api/models';
import { I18nService } from '../../core/i18n/i18n.service';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';
import {
  defaultIssueMonth,
  formatBillingMonth,
  formatDateOnly,
  runOutcome,
  runOutcomeKey,
  runOutcomeTone,
  sumDocuments,
  validateIssueMonth,
} from '../finance/invoices/invoice.logic';
import { AdminTabsComponent } from './admin-tabs';

/**
 * Platform admin: issue commission invoices and corrections for a completed month (same idempotent path as the
 * monthly job, `POST /api/admin/invoices/issue`) and review all documents of a month (`GET /api/admin/invoices`).
 */
@Component({
  selector: 'hn-admin-invoices-page',
  imports: [FormsModule, AdminTabsComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-page-header [title]="'admin.title' | t" [description]="'invoices.admin.subtitle' | t" />
    <hn-admin-tabs />

    <div class="mb-4 grid gap-4 lg:grid-cols-3">
      <section class="hn-card p-4 lg:col-span-1" aria-labelledby="issue-heading">
        <h2 id="issue-heading" class="mb-2 text-sm">{{ 'invoices.admin.issueHeading' | t }}</h2>
        <form class="space-y-3" (ngSubmit)="review()" novalidate data-testid="invoice-issue-form">
          <hn-field [label]="'invoices.admin.month' | t" for="issue-month" [required]="true" [hint]="'invoices.admin.monthHint' | t" [errors]="monthError()">
            <input hnInput id="issue-month" name="month" type="month" required [ngModel]="month()" (ngModelChange)="setMonth($event)"
              [attr.max]="maxMonth" [attr.aria-invalid]="!!monthError()"
              [attr.aria-describedby]="monthError() ? 'issue-month-error' : 'issue-month-hint'" data-testid="invoice-issue-month" />
          </hn-field>
          @if (!confirming()) {
            <hn-banner [error]="issueErrorBanner()" />
          }
          <div class="flex justify-end">
            <button hnButton variant="primary" type="submit" [disabled]="busy()" data-testid="invoice-issue-submit">{{ 'invoices.admin.issue' | t }}</button>
          </div>
        </form>
      </section>

      <section class="hn-card p-4 text-sm lg:col-span-2" aria-labelledby="issue-help-heading" data-testid="invoice-issue-help">
        <h2 id="issue-help-heading" class="mb-2 text-sm">{{ 'invoices.admin.helpHeading' | t }}</h2>
        <ul class="list-disc space-y-1 ps-5 text-slate-700">
          <li>{{ 'invoices.admin.autoHint' | t }}</li>
          <li data-testid="invoice-idempotent-hint">{{ 'invoices.admin.idempotentHint' | t }}</li>
          <li>{{ 'invoices.admin.correctionHint' | t }}</li>
          <li>{{ 'invoices.admin.pdfHint' | t }}</li>
        </ul>
      </section>
    </div>

    @if (result(); as r) {
      <section class="mb-4" aria-labelledby="result-heading" data-testid="invoice-issue-result">
        <h2 id="result-heading" class="mb-2 text-sm">{{ 'invoices.admin.result' | t: { month: monthLabel(r.month) } }}</h2>
        <hn-banner class="mb-3" [tone]="tone(r)" [text]="outcomeKey(r) | t: { count: r.failures.length }" data-testid="invoice-issue-outcome" />
        <dl class="mb-3 grid grid-cols-2 gap-3 sm:grid-cols-4">
          @for (s of stats(r); track s.key) {
            <div class="hn-card p-3" [attr.data-testid]="'invoice-stat-' + s.key">
              <dt class="text-xs text-slate-600">{{ s.labelKey | t }}</dt>
              <dd class="mt-1 text-xl font-semibold tabular-nums">{{ s.value }}</dd>
            </div>
          }
        </dl>

        @if (r.failures.length) {
          <h3 class="mb-1 text-sm">{{ 'invoices.admin.failures' | t }}</h3>
          <hn-table class="mb-3">
            <table class="hn-table" data-testid="invoice-issue-failures">
              <thead>
                <tr>
                  <th scope="col">{{ 'invoices.admin.organization' | t }}</th>
                  <th scope="col">{{ 'invoices.admin.code' | t }}</th>
                  <th scope="col">{{ 'invoices.admin.message' | t }}</th>
                </tr>
              </thead>
              <tbody>
                @for (f of r.failures; track f.organizationId + f.code) {
                  <tr>
                    <td class="hn-mono text-xs"><bdi>{{ f.organizationId }}</bdi></td>
                    <td class="hn-mono text-xs"><bdi>{{ f.code }}</bdi></td>
                    <td>{{ failureText(f) }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </hn-table>
        }
      </section>
    }

    <section aria-labelledby="month-docs-heading">
      <div class="mb-2 flex flex-wrap items-center justify-between gap-2">
        <h2 id="month-docs-heading" class="text-sm">{{ 'invoices.admin.monthDocuments' | t: { month: monthLabel(listedMonth()) } }}</h2>
        <button hnButton size="sm" type="button" [loading]="documents.loading()" [disabled]="!listedMonth()" (click)="documents.reload()">
          {{ 'common.refresh' | t }}
        </button>
      </div>
      <p class="mb-2 text-xs text-slate-600">{{ 'invoices.admin.monthDocumentsHint' | t }}</p>
      <hn-banner class="mb-3" [error]="documents.error()" />
      <!-- phone: cards plus a totals summary; md+: table -->
      <ul class="grid gap-3 sm:grid-cols-2 md:hidden" data-testid="admin-invoices-cards">
        @for (d of documents.value() ?? []; track d.id) {
          <li class="hn-card flex flex-col gap-1 p-4" [attr.data-invoice-number]="d.number" [attr.data-kind]="d.kind">
            <div class="flex items-start justify-between gap-2">
              <span class="hn-mono text-xs font-semibold"><bdi>{{ d.number }}</bdi></span>
              <hn-status-badge [status]="d.kind" kind="invoiceKind" [tone]="d.kind === 'CreditNote' ? 'warning' : 'neutral'" />
            </div>
            @if (d.referencedInvoiceNumber) {
              <p class="text-xs text-slate-500">{{ 'invoices.corrects' | t: { number: d.referencedInvoiceNumber } }}</p>
            }
            <p class="text-sm" [title]="d.organizationId">{{ d.recipientName }}</p>
            <p class="text-xs text-slate-500">{{ date(d.issueDate) }} · {{ d.lineCount }} {{ 'invoices.lineCount' | t }}</p>
            <dl class="mt-1 grid grid-cols-3 gap-2 text-xs">
              <div><dt class="text-slate-500">{{ 'invoices.net' | t }}</dt><dd class="tabular-nums"><bdi dir="ltr">{{ d.totalNetMinor | money: d.currency }}</bdi></dd></div>
              <div><dt class="text-slate-500">{{ 'invoices.vat' | t }}</dt><dd class="tabular-nums"><bdi dir="ltr">{{ d.totalVatMinor | money: d.currency }}</bdi></dd></div>
              <div><dt class="text-slate-500">{{ 'invoices.gross' | t }}</dt><dd class="font-semibold tabular-nums"><bdi dir="ltr">{{ d.totalGrossMinor | money: d.currency }}</bdi></dd></div>
            </dl>
          </li>
        } @empty {
          @if (!documents.loading()) {
            <li class="sm:col-span-2"><div class="hn-card"><hn-empty-state [title]="'invoices.admin.noDocuments' | t" /></div></li>
          }
        }
      </ul>
      @if (documents.value()?.length) {
        <section class="hn-card mt-3 p-4 md:hidden" aria-labelledby="admin-invoices-total-heading" data-testid="admin-invoices-card-total">
          <h3 id="admin-invoices-total-heading" class="mb-1 text-sm">{{ 'invoices.admin.total' | t }}</h3>
          <dl class="grid grid-cols-3 gap-2 text-xs">
            <div><dt class="text-slate-500">{{ 'invoices.net' | t }}</dt><dd class="font-medium tabular-nums"><bdi dir="ltr">{{ totals().netMinor | money }}</bdi></dd></div>
            <div><dt class="text-slate-500">{{ 'invoices.vat' | t }}</dt><dd class="font-medium tabular-nums"><bdi dir="ltr">{{ totals().vatMinor | money }}</bdi></dd></div>
            <div><dt class="text-slate-500">{{ 'invoices.gross' | t }}</dt><dd class="font-semibold tabular-nums"><bdi dir="ltr">{{ totals().grossMinor | money }}</bdi></dd></div>
          </dl>
        </section>
      }
      <hn-table class="hidden md:block" [loading]="documents.loading()">
        <table class="hn-table" data-testid="admin-invoices-table">
          <thead>
            <tr>
              <th scope="col">{{ 'invoices.number' | t }}</th>
              <th scope="col">{{ 'invoices.kind' | t }}</th>
              <th scope="col">{{ 'invoices.admin.recipient' | t }}</th>
              <th scope="col">{{ 'invoices.issueDate' | t }}</th>
              <th scope="col" class="num">{{ 'invoices.lineCount' | t }}</th>
              <th scope="col" class="num">{{ 'invoices.net' | t }}</th>
              <th scope="col" class="num">{{ 'invoices.vat' | t }}</th>
              <th scope="col" class="num">{{ 'invoices.gross' | t }}</th>
            </tr>
          </thead>
          <tbody>
            @for (d of documents.value() ?? []; track d.id) {
              <tr [attr.data-invoice-number]="d.number" [attr.data-kind]="d.kind">
                <td class="whitespace-nowrap">
                  <span class="hn-mono text-xs"><bdi>{{ d.number }}</bdi></span>
                  @if (d.referencedInvoiceNumber) {
                    <span class="block text-xs text-slate-500">{{ 'invoices.corrects' | t: { number: d.referencedInvoiceNumber } }}</span>
                  }
                </td>
                <td><hn-status-badge [status]="d.kind" kind="invoiceKind" [tone]="d.kind === 'CreditNote' ? 'warning' : 'neutral'" /></td>
                <td [title]="d.organizationId">{{ d.recipientName }}</td>
                <td class="whitespace-nowrap">{{ date(d.issueDate) }}</td>
                <td class="num">{{ d.lineCount }}</td>
                <td class="num"><bdi dir="ltr">{{ d.totalNetMinor | money: d.currency }}</bdi></td>
                <td class="num"><bdi dir="ltr">{{ d.totalVatMinor | money: d.currency }}</bdi></td>
                <td class="num font-medium"><bdi dir="ltr">{{ d.totalGrossMinor | money: d.currency }}</bdi></td>
              </tr>
            } @empty {
              @if (!documents.loading()) {
                <tr><td colspan="8"><hn-empty-state [title]="'invoices.admin.noDocuments' | t" /></td></tr>
              }
            }
          </tbody>
          @if (documents.value()?.length) {
            <tfoot>
              <tr data-testid="admin-invoices-total">
                <th scope="row" colspan="5" class="text-end font-medium">{{ 'invoices.admin.total' | t }}</th>
                <td class="num font-medium"><bdi dir="ltr">{{ totals().netMinor | money }}</bdi></td>
                <td class="num font-medium"><bdi dir="ltr">{{ totals().vatMinor | money }}</bdi></td>
                <td class="num font-semibold"><bdi dir="ltr">{{ totals().grossMinor | money }}</bdi></td>
              </tr>
            </tfoot>
          }
        </table>
      </hn-table>
    </section>

    <hn-dialog [open]="confirming()" [title]="'invoices.admin.confirmTitle' | t" (closed)="closeDialog()">
      <div class="space-y-4">
        <hn-banner [error]="confirming() ? issueError() : null" />
        <p class="text-sm" data-testid="invoice-issue-confirm-text">{{ 'invoices.admin.confirmText' | t: { month: monthLabel(month()) } }}</p>
        <p class="text-xs text-slate-600">{{ 'invoices.admin.idempotentShort' | t }}</p>
        <div class="flex justify-end gap-2">
          <button hnButton type="button" [disabled]="busy()" (click)="closeDialog()">{{ 'common.cancel' | t }}</button>
          <button hnButton variant="primary" type="button" [loading]="busy()" [disabled]="busy()" (click)="issue()" data-testid="invoice-issue-confirm">
            {{ 'invoices.admin.issue' | t }}
          </button>
        </div>
      </div>
    </hn-dialog>
  `,
})
export class AdminInvoicesPage {
  private readonly api = inject(AdminInvoicesApi);
  private readonly i18n = inject(I18nService);

  /** Latest selectable month: the previous one (German time); the API refuses months that are not over. */
  protected readonly maxMonth = defaultIssueMonth(new Date());
  protected readonly month = signal(this.maxMonth);
  /** Month whose documents are listed: the selected month once it is valid. */
  protected readonly listedMonth = signal(this.maxMonth);

  protected readonly submitted = signal(false);
  protected readonly validation = signal<string | null>(null);
  protected readonly confirming = signal(false);
  protected readonly busy = signal(false);
  protected readonly issueError = signal<unknown>(null);
  protected readonly result = signal<IssueRunResult | null>(null);

  protected readonly documents = createQuery(
    () => this.listedMonth(),
    (month) => this.api.forMonth(month),
  );
  protected readonly totals = computed(() => sumDocuments(this.documents.value() ?? []));

  protected readonly monthError = computed(() => {
    const key = this.submitted() ? this.validation() : null;
    if (key) return this.i18n.t(key);
    const err = this.issueError();
    const messages = err ? fieldErrors(toApiProblem(err), 'month') : [];
    return messages.length ? messages.join(' ') : null;
  });

  /** Field errors are shown at the field; everything else (e.g. issuer not configured) in the banner. */
  protected readonly issueErrorBanner = computed(() => {
    const err = this.issueError();
    if (!err) return null;
    return fieldErrors(toApiProblem(err), 'month').length ? null : err;
  });

  protected setMonth(value: string): void {
    this.month.set(value ?? '');
    this.issueError.set(null);
    const error = validateIssueMonth(this.month(), new Date());
    this.validation.set(error);
    if (!error) this.listedMonth.set(this.month());
  }

  protected review(): void {
    this.submitted.set(true);
    const error = validateIssueMonth(this.month(), new Date());
    this.validation.set(error);
    if (error) return;
    this.issueError.set(null);
    this.confirming.set(true);
  }

  protected async issue(): Promise<void> {
    if (this.busy()) return;
    this.busy.set(true);
    this.issueError.set(null);
    try {
      const r = await firstValueFrom(this.api.issue(this.month().trim()));
      this.result.set(r);
      this.confirming.set(false);
      this.listedMonth.set(r.month);
      this.documents.reload();
    } catch (e) {
      this.issueError.set(e);
      // Field errors belong to the form behind the dialog.
      if (fieldErrors(toApiProblem(e), 'month').length) this.confirming.set(false);
    } finally {
      this.busy.set(false);
    }
  }

  protected closeDialog(): void {
    if (this.busy()) return;
    this.confirming.set(false);
  }

  protected stats(r: IssueRunResult): { key: string; labelKey: string; value: number }[] {
    return [
      { key: 'sellers', labelKey: 'invoices.admin.sellersChecked', value: r.sellersChecked },
      { key: 'invoices', labelKey: 'invoices.admin.invoicesIssued', value: r.invoicesIssued },
      { key: 'corrections', labelKey: 'invoices.admin.creditNotesIssued', value: r.creditNotesIssued },
      { key: 'existing', labelKey: 'invoices.admin.alreadyIssued', value: r.alreadyIssued },
    ];
  }

  protected tone(r: IssueRunResult): 'warning' | 'success' | 'info' {
    return runOutcomeTone(runOutcome(r));
  }

  protected outcomeKey(r: IssueRunResult): string {
    return runOutcomeKey(runOutcome(r));
  }

  /** Translated text for known codes, otherwise the API's message. */
  protected failureText(f: IssueFailure): string {
    const key = `errors.codes.${f.code}`;
    return this.i18n.has(key) ? this.i18n.t(key) : f.message;
  }

  protected monthLabel(month: string): string {
    return formatBillingMonth(month, this.i18n.locale());
  }

  protected date(value: string): string {
    return formatDateOnly(value, this.i18n.locale());
  }
}
