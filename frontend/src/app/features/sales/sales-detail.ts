import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { Observable, firstValueFrom } from 'rxjs';
import { SalesApi } from '../../core/api/handynet-api';
import { InvoicePaymentMethod, PosPaymentMethod, Quotation, SalesInvoice, SalesLine, SalesVat } from '../../core/api/models';
import { SessionStore } from '../../core/auth/session.store';
import { minorToInput } from '../../core/format/money';
import { I18nService } from '../../core/i18n/i18n.service';
import { ToastService } from '../../core/ui-state/toast.service';
import { UI } from '../../shared/ui';
import { PosCashierStore } from '../pos/pos-cashier.store';
import { PosContext } from '../pos/pos-context';
import { posErrorText } from '../pos/pos-error';
import { posRights } from '../pos/pos-rights';
import { newIdempotencyKey } from '../pos/pos.logic';
import { SalesNav, openBlob, receiptLink, saveBlob } from './sales-nav';
import { customerLines, invoiceBadge, isoToday, parseAmount, quotationTone, rateLabel } from './sales.logic';

type Kind = 'quotation' | 'invoice';
type DialogName = 'email' | 'payment' | 'collect' | 'creditNote' | 'issue' | null;

/**
 * Detail of a quotation or invoice with its actions (shared by POS and back office): PDF / print / email, duplicate,
 * quotation status and conversion, issue (stock is deducted), payments (transfer/card/other), collect at the till (POS
 * receipt, signed by the TSE), credit note (pos.refund or a manager approval in the POS).
 */
@Component({
  selector: 'hn-sales-detail',
  imports: [RouterLink, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    @if (error()) {
      <hn-banner class="mb-3" [error]="error()" />
    }
    @if (doc(); as d) {
      <hn-page-header [title]="heading()">
        <a hnButton class="min-h-11" [routerLink]="listLink()">{{ 'sales.detail.back' | t }}</a>
      </hn-page-header>

      <div class="mb-3 flex flex-wrap items-center gap-2" data-testid="sales-detail-status">
        @if (quotation(); as q) {
          <hn-status-badge [status]="q.status" kind="sales.quotationStatus" [tone]="qTone(q.status)" />
          <span class="text-sm text-slate-600">{{ 'sales.fields.validUntil' | t }}: {{ q.validUntil | hnDate }}</span>
          @if (q.convertedInvoiceId) {
            <a class="hn-link text-sm" [routerLink]="[...nav().invoices, q.convertedInvoiceId]" data-testid="sales-converted-link">
              {{ 'sales.detail.convertedTo' | t: { number: q.convertedInvoiceNumber ?? ('sales.invoices.draft' | t) } }}</a>
          }
        }
        @if (invoice(); as i) {
          @let b = badge(i);
          <hn-status-badge [status]="b.key" kind="sales.badge" [tone]="b.tone" />
          @if (i.quotationId) {
            <a class="hn-link text-sm" [routerLink]="[...nav().quotations, i.quotationId]">{{ 'sales.detail.fromQuotation' | t: { number: i.quotationNumber ?? '' } }}</a>
          }
          @if (i.originalInvoiceId) {
            <a class="hn-link text-sm" [routerLink]="[...nav().invoices, i.originalInvoiceId]">{{ 'sales.detail.creditNoteFor' | t: { number: i.originalNumber ?? '' } }}</a>
          }
          @if (i.creditNoteId) {
            <a class="hn-link text-sm" [routerLink]="[...nav().invoices, i.creditNoteId]" data-testid="sales-credit-note-link">
              {{ 'sales.detail.cancelledBy' | t: { number: i.creditNoteNumber ?? '' } }}</a>
          }
        }
      </div>

      <!-- actions -->
      <div class="mb-4 flex flex-wrap gap-2" role="toolbar" [attr.aria-label]="'sales.detail.actions' | t">
        @if (editable()) {
          <a hnButton class="min-h-11" [routerLink]="[...base(), d.id, 'bearbeiten']" data-testid="sales-edit">{{ 'sales.detail.edit' | t }}</a>
        }
        <button hnButton type="button" class="min-h-11" [loading]="busy() === 'pdf'" (click)="pdf(false)" data-testid="sales-pdf">{{ 'sales.detail.pdf' | t }}</button>
        <button hnButton type="button" class="min-h-11" [loading]="busy() === 'print'" (click)="pdf(true)" data-testid="sales-print">{{ 'sales.detail.print' | t }}</button>
        @if (canEmail()) {
          <button hnButton type="button" class="min-h-11" (click)="openDialog('email')" data-testid="sales-email">{{ 'sales.detail.email' | t }}</button>
        }
        @if (quotation(); as q) {
          @if (!q.convertedInvoiceId && q.storedStatus !== 'Rejected') {
            <button hnButton variant="primary" type="button" class="min-h-11" [loading]="busy() === 'convert'" (click)="convert()" data-testid="sales-convert">
              {{ 'sales.detail.convert' | t }}</button>
          }
          @if (q.convertedInvoiceId) {
            <a hnButton variant="primary" class="min-h-11" [routerLink]="[...nav().invoices, q.convertedInvoiceId]">{{ 'sales.detail.openInvoice' | t }}</a>
          }
          @if (!q.convertedInvoiceId) {
            @if (q.storedStatus === 'Draft') {
              <button hnButton type="button" class="min-h-11" (click)="setStatus('Sent')" data-testid="sales-mark-sent">{{ 'sales.detail.markSent' | t }}</button>
            }
            @if (q.storedStatus === 'Draft' || q.storedStatus === 'Sent') {
              <button hnButton type="button" class="min-h-11" (click)="setStatus('Accepted')" data-testid="sales-mark-accepted">{{ 'sales.detail.markAccepted' | t }}</button>
              <button hnButton type="button" class="min-h-11" (click)="setStatus('Rejected')" data-testid="sales-mark-rejected">{{ 'sales.detail.markRejected' | t }}</button>
            }
            @if (q.storedStatus === 'Accepted' || q.storedStatus === 'Rejected') {
              <button hnButton type="button" class="min-h-11" (click)="setStatus('Sent')">{{ 'sales.detail.reopen' | t }}</button>
            }
          }
        }
        @if (invoice(); as i) {
          @if (i.status === 'Draft') {
            <button hnButton variant="primary" type="button" class="min-h-11" (click)="openDialog('issue')" data-testid="sales-issue">{{ 'sales.detail.issue' | t }}</button>
            <button hnButton variant="danger" type="button" class="min-h-11" [loading]="busy() === 'discard'" (click)="discard()" data-testid="sales-discard">{{ 'sales.detail.discard' | t }}</button>
          }
          @if (i.kind === 'Invoice' && i.status === 'Issued' && !i.cancelled && i.openMinor > 0) {
            @if (nav().area === 'pos') {
              <button hnButton variant="primary" type="button" class="min-h-11" (click)="openDialog('collect')" data-testid="sales-collect">{{ 'sales.detail.collect' | t }}</button>
            }
            <button hnButton type="button" class="min-h-11" (click)="openDialog('payment')" data-testid="sales-record-payment">{{ 'sales.detail.recordPayment' | t }}</button>
          }
          @if (i.kind === 'Invoice' && i.status === 'Issued' && !i.cancelled) {
            <button hnButton variant="danger" type="button" class="min-h-11" (click)="startCreditNote()" data-testid="sales-credit-note">{{ 'sales.detail.creditNote' | t }}</button>
          }
        }
        @if (d.kind !== 'CreditNote') {
          <button hnButton variant="ghost" type="button" class="min-h-11" [loading]="busy() === 'duplicate'" (click)="duplicate()" data-testid="sales-duplicate">{{ 'sales.detail.duplicate' | t }}</button>
        }
      </div>

      @if (invoice()?.warnings?.length) {
        @for (w of invoice()!.warnings; track w) {
          <hn-banner class="mb-3" tone="warning" [text]="'sales.warnings.' + w | t" />
        }
      }
      @if (nav().area === 'pos' && invoice()?.status === 'Issued' && invoice()!.openMinor > 0 && !ctx?.session()) {
        <p class="mb-3 text-sm text-slate-600">{{ 'sales.detail.collectNeedsSession' | t }}</p>
      }

      <div class="grid gap-4 lg:grid-cols-[minmax(0,1fr)_20rem]">
        <div class="min-w-0 space-y-4">
          <section class="hn-card p-4">
            <dl class="grid gap-3 text-sm sm:grid-cols-2">
              <div>
                <dt class="text-xs font-medium text-slate-500">{{ 'sales.fields.customer' | t }}</dt>
                <dd data-testid="sales-detail-customer">
                  @for (line of customer(); track $index) {
                    <span class="block">{{ line }}</span>
                  } @empty {
                    –
                  }
                  @if (d.customer?.vatId) {
                    <span class="block text-xs text-slate-500">{{ 'sales.fields.vatId' | t }}: {{ d.customer!.vatId }}</span>
                  }
                </dd>
              </div>
              <div class="grid grid-cols-2 gap-2">
                @for (f of facts(); track f.label) {
                  <div><dt class="text-xs font-medium text-slate-500">{{ f.label | t }}</dt><dd [attr.data-testid]="f.testId">{{ f.value }}</dd></div>
                }
              </div>
            </dl>
          </section>

          <hn-table>
            <table class="hn-table" data-testid="sales-detail-lines">
              <thead>
                <tr>
                  <th>#</th>
                  <th>{{ 'sales.fields.description' | t }}</th>
                  <th class="num">{{ 'sales.fields.quantity' | t }}</th>
                  <th class="num">{{ (d.priceMode === 'Net' ? 'sales.form.unitNet' : 'sales.form.unitGross') | t }}</th>
                  <th class="num">{{ 'sales.fields.discount' | t }}</th>
                  <th>{{ 'sales.fields.vat' | t }}</th>
                  <th class="num">{{ 'sales.fields.amount' | t }}</th>
                </tr>
              </thead>
              <tbody>
                @for (l of d.lines; track l.position) {
                  <tr class="hn-row-static">
                    <td>{{ l.position }}</td>
                    <td>{{ l.description }}
                      @if (l.imei) {<span class="block text-xs text-slate-500">IMEI {{ l.imei }}</span>}
                      @if (l.note) {<span class="block text-xs text-slate-500">{{ l.note }}</span>}
                    </td>
                    <td class="num">{{ l.quantity }}</td>
                    <td class="num">{{ l.unitPriceMinor | money }}</td>
                    <td class="num">{{ l.discountMinor ? (l.discountMinor | money) : '' }}</td>
                    <td>{{ taxLabel(l) }}</td>
                    <td class="num">{{ l.amountMinor | money }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </hn-table>

          @if (d.notes) {
            <section class="hn-card p-4 text-sm whitespace-pre-line">{{ d.notes }}</section>
          }

          @if (invoice(); as i) {
            @if (i.kind === 'Invoice' && i.status === 'Issued') {
              <section class="hn-card p-4" aria-labelledby="sales-payments-h">
                <h2 id="sales-payments-h" class="mb-2 text-sm">{{ 'sales.detail.payments' | t }}</h2>
                <ul class="divide-y divide-slate-100 text-sm" data-testid="sales-payments">
                  @for (p of i.payments; track p.id) {
                    <li class="flex flex-wrap justify-between gap-2 py-2">
                      <span>{{ p.paidOn | hnDate }} · {{ 'sales.paymentMethod.' + p.method | t }}
                        @if (p.receiptId) {
                          · <a class="hn-link" [routerLink]="receipt(p.receiptId)">{{ 'sales.detail.receipt' | t: { number: p.receiptNumber ?? '' } }}</a>
                        } @else if (p.reference) {
                          · {{ p.reference }}
                        }
                      </span>
                      <span class="font-semibold tabular-nums">{{ p.amountMinor | money }}</span>
                    </li>
                  } @empty {
                    <li class="py-2 text-slate-500">{{ 'sales.detail.noPayments' | t }}</li>
                  }
                </ul>
              </section>
            }
          }
        </div>

        <aside class="space-y-4">
          <section class="hn-card p-4" data-testid="sales-detail-totals">
            <dl class="space-y-1 text-sm">
              @for (v of vat(); track v.rateBasisPoints) {
                <div class="flex justify-between"><dt>{{ 'sales.totals.net' | t: { rate: rate(v.rateBasisPoints) } }}</dt><dd class="tabular-nums">{{ v.netMinor | money }}</dd></div>
                <div class="flex justify-between text-slate-600"><dt>{{ 'sales.totals.vat' | t: { rate: rate(v.rateBasisPoints) } }}</dt><dd class="tabular-nums">{{ v.vatMinor | money }}</dd></div>
              }
              @if (d.marginGrossMinor) {
                <div class="flex justify-between"><dt>{{ 'sales.totals.margin' | t }}</dt><dd class="tabular-nums">{{ d.marginGrossMinor | money }}</dd></div>
              }
              <div class="flex justify-between border-t border-slate-200 pt-2 text-base font-bold"><dt>{{ 'sales.fields.total' | t }}</dt>
                <dd class="tabular-nums" data-testid="sales-detail-total">{{ d.totalGrossMinor | money }}</dd></div>
              @if (invoice(); as i) {
                @if (i.kind === 'Invoice' && i.status === 'Issued') {
                  <div class="flex justify-between"><dt>{{ 'sales.fields.paid' | t }}</dt><dd class="tabular-nums">{{ i.paidMinor | money }}</dd></div>
                  <div class="flex justify-between font-semibold"><dt>{{ 'sales.fields.open' | t }}</dt><dd class="tabular-nums" data-testid="sales-detail-open">{{ i.openMinor | money }}</dd></div>
                }
              }
            </dl>
            @if (d.marginGrossMinor) {
              <p class="mt-2 text-xs text-slate-500">{{ 'sales.form.marginHint' | t }}</p>
            }
          </section>
        </aside>
      </div>
    } @else if (!error()) {
      <hn-skeleton-rows [rows]="6" />
    }

    <!-- dialogs -->
    <hn-dialog [title]="'sales.detail.email' | t" [open]="dialog() === 'email'" (closed)="closeDialog()" testId="sales-email-dialog">
      <form class="space-y-3" (submit)="$event.preventDefault(); sendEmail()">
        <hn-field [label]="'sales.detail.emailAddress' | t" for="sales-email-to" [hint]="'sales.detail.emailHint' | t">
          <input hnInput id="sales-email-to" type="email" class="h-11" autocomplete="off" [value]="emailTo()" (input)="emailTo.set($any($event.target).value)" data-testid="sales-email-to" />
        </hn-field>
        @if (dialogError()) {<hn-banner [text]="dialogError()" />}
        <div class="flex justify-end gap-2">
          <button hnButton type="button" class="min-h-11" (click)="closeDialog()">{{ 'common.cancel' | t }}</button>
          <button hnButton variant="primary" type="submit" class="min-h-11" [loading]="dialogBusy()" data-testid="sales-email-send">{{ 'sales.detail.send' | t }}</button>
        </div>
      </form>
    </hn-dialog>

    <hn-dialog [title]="'sales.detail.issueTitle' | t" [open]="dialog() === 'issue'" (closed)="closeDialog()" testId="sales-issue-dialog">
      <p class="text-sm">{{ 'sales.detail.issueText' | t }}</p>
      @if (dialogError()) {<hn-banner class="mt-3" [text]="dialogError()" />}
      <div class="mt-4 flex justify-end gap-2">
        <button hnButton type="button" class="min-h-11" (click)="closeDialog()">{{ 'common.cancel' | t }}</button>
        <button hnButton variant="primary" type="button" class="min-h-11" [loading]="dialogBusy()" (click)="issue()" data-testid="sales-issue-confirm">{{ 'sales.detail.issue' | t }}</button>
      </div>
    </hn-dialog>

    <hn-dialog [title]="'sales.detail.recordPayment' | t" [open]="dialog() === 'payment'" (closed)="closeDialog()" testId="sales-payment-dialog">
      <form class="space-y-3" (submit)="$event.preventDefault(); recordPayment()">
        <div role="group" [attr.aria-label]="'sales.detail.method' | t" class="grid grid-cols-3 gap-1.5">
          @for (m of manualMethods; track m) {
            <button type="button" class="pos-mode" [attr.aria-pressed]="payMethod() === m" (click)="payMethod.set(m)" [attr.data-testid]="'sales-pay-' + m">{{ 'sales.paymentMethod.' + m | t }}</button>
          }
        </div>
        <p class="text-xs text-slate-500">{{ 'sales.detail.cashAtTill' | t }}</p>
        <hn-field [label]="'sales.fields.amount' | t" for="sales-pay-amount">
          <input hnInput id="sales-pay-amount" class="h-11" inputmode="decimal" [value]="payAmount()" (input)="payAmount.set($any($event.target).value)" data-testid="sales-pay-amount" />
        </hn-field>
        <div class="grid grid-cols-2 gap-2">
          <hn-field [label]="'sales.detail.paidOn' | t" for="sales-pay-date">
            <input hnInput id="sales-pay-date" type="date" class="h-11" [value]="payDate()" (input)="payDate.set($any($event.target).value)" />
          </hn-field>
          <hn-field [label]="'sales.detail.reference' | t" for="sales-pay-ref">
            <input hnInput id="sales-pay-ref" class="h-11" maxlength="200" [value]="payRef()" (input)="payRef.set($any($event.target).value)" />
          </hn-field>
        </div>
        @if (dialogError()) {<hn-banner [text]="dialogError()" />}
        <div class="flex justify-end gap-2">
          <button hnButton type="button" class="min-h-11" (click)="closeDialog()">{{ 'common.cancel' | t }}</button>
          <button hnButton variant="primary" type="submit" class="min-h-11" [loading]="dialogBusy()" data-testid="sales-pay-save">{{ 'common.save' | t }}</button>
        </div>
      </form>
    </hn-dialog>

    <hn-dialog [title]="'sales.detail.collect' | t" [open]="dialog() === 'collect'" (closed)="closeDialog()" testId="sales-collect-dialog">
      <form class="space-y-3" (submit)="$event.preventDefault(); collect()">
        <p class="text-sm text-slate-700">{{ 'sales.detail.collectText' | t }}</p>
        <div role="group" [attr.aria-label]="'sales.detail.method' | t" class="grid grid-cols-2 gap-1.5">
          @for (m of tillMethods; track m) {
            <button type="button" class="pos-mode" [attr.aria-pressed]="tillMethod() === m" (click)="tillMethod.set(m)" [attr.data-testid]="'sales-till-' + m">{{ 'posPaymentMethod.' + m | t }}</button>
          }
        </div>
        <hn-field [label]="'sales.detail.collectAmount' | t" for="sales-collect-amount">
          <input hnInput id="sales-collect-amount" class="h-12 text-lg" inputmode="decimal" [value]="collectAmount()" (input)="collectAmount.set($any($event.target).value)" data-testid="sales-collect-amount" />
        </hn-field>
        @if (tillMethod() === 'Cash') {
          <hn-field [label]="'sales.detail.tendered' | t" for="sales-collect-tendered" [hint]="changeText()">
            <input hnInput id="sales-collect-tendered" class="h-12 text-lg" inputmode="decimal" [value]="tendered()" (input)="tendered.set($any($event.target).value)" data-testid="sales-collect-tendered" />
          </hn-field>
        }
        @if (dialogError()) {<hn-banner [text]="dialogError()" data-testid="sales-collect-error" />}
        <div class="flex justify-end gap-2">
          <button hnButton type="button" class="min-h-11" (click)="closeDialog()">{{ 'common.cancel' | t }}</button>
          <button type="submit" class="pos-pay-btn max-w-xs" [disabled]="dialogBusy()" data-testid="sales-collect-confirm">
            <span>{{ 'sales.detail.collectConfirm' | t }}</span><span class="tabular-nums">{{ collectAmountText() }}</span>
          </button>
        </div>
      </form>
    </hn-dialog>

    <hn-dialog [title]="'sales.detail.creditNote' | t" [open]="dialog() === 'creditNote'" (closed)="closeDialog()" testId="sales-credit-dialog">
      <form class="space-y-3" (submit)="$event.preventDefault(); creditNote()">
        <p class="text-sm text-slate-700">{{ 'sales.detail.creditNoteText' | t }}</p>
        <hn-field [label]="'sales.detail.reason' | t" for="sales-credit-reason" [required]="true">
          <textarea hnInput id="sales-credit-reason" rows="2" maxlength="500" [value]="reason()" (input)="reason.set($any($event.target).value)" data-testid="sales-credit-reason"></textarea>
        </hn-field>
        @if (dialogError()) {<hn-banner [text]="dialogError()" />}
        <div class="flex justify-end gap-2">
          <button hnButton type="button" class="min-h-11" (click)="closeDialog()">{{ 'common.cancel' | t }}</button>
          <button hnButton variant="danger" type="submit" class="min-h-11" [loading]="dialogBusy()" [disabled]="!reason().trim()" data-testid="sales-credit-confirm">
            {{ 'sales.detail.creditNoteConfirm' | t }}</button>
        </div>
      </form>
    </hn-dialog>
  `,
})
export class SalesDetailComponent {
  private readonly api = inject(SalesApi);
  private readonly i18n = inject(I18nService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);
  private readonly document = inject(DOCUMENT);
  private readonly session = inject(SessionStore);
  private readonly cashiers = inject(PosCashierStore);
  protected readonly ctx = inject(PosContext, { optional: true });

  readonly kind = input.required<Kind>();
  readonly id = input.required<string>();
  readonly nav = input.required<SalesNav>();

  protected readonly quotation = signal<Quotation | null>(null);
  protected readonly invoice = signal<SalesInvoice | null>(null);
  protected readonly error = signal<unknown>(null);
  protected readonly busy = signal<string | null>(null);
  protected readonly dialog = signal<DialogName>(null);
  protected readonly dialogBusy = signal(false);
  protected readonly dialogError = signal<string | null>(null);

  protected readonly manualMethods: Exclude<InvoicePaymentMethod, 'Cash'>[] = ['BankTransfer', 'Card', 'Other'];
  protected readonly tillMethods: PosPaymentMethod[] = ['Cash', 'Card'];
  protected readonly emailTo = signal('');
  protected readonly payMethod = signal<Exclude<InvoicePaymentMethod, 'Cash'>>('BankTransfer');
  protected readonly payAmount = signal('');
  protected readonly payDate = signal(isoToday());
  protected readonly payRef = signal('');
  protected readonly tillMethod = signal<PosPaymentMethod>('Cash');
  protected readonly collectAmount = signal('');
  protected readonly tendered = signal('');
  protected readonly reason = signal('');
  private collectKey: string | null = null;

  protected readonly doc = computed(() => {
    const q = this.quotation();
    if (q) return { id: q.id, kind: 'Quotation', customer: q.customer, lines: q.lines, priceMode: q.priceMode, marginGrossMinor: q.marginGrossMinor, totalGrossMinor: q.totalGrossMinor, notes: q.notes };
    const i = this.invoice();
    if (i) return { id: i.id, kind: i.kind, customer: i.customer, lines: i.lines, priceMode: i.priceMode, marginGrossMinor: i.marginGrossMinor, totalGrossMinor: i.totalGrossMinor, notes: i.notes };
    return null;
  });
  protected readonly vat = computed<SalesVat[]>(() => this.quotation()?.vat ?? this.invoice()?.vat ?? []);
  protected readonly base = computed(() => (this.kind() === 'quotation' ? this.nav().quotations : this.nav().invoices));
  protected readonly listLink = computed(() => this.base());
  protected readonly customer = computed(() => customerLines(this.doc()?.customer ?? null));
  protected readonly editable = computed(() => this.quotation()?.editable ?? this.invoice()?.status === 'Draft');
  protected readonly canEmail = computed(() => !!this.quotation() || this.invoice()?.status === 'Issued');
  protected readonly heading = computed(() => {
    const q = this.quotation();
    if (q) return `${this.i18n.t('sales.detail.quotation')} ${q.number}`;
    const i = this.invoice();
    if (!i) return '';
    const title = this.i18n.t(i.kind === 'CreditNote' ? 'sales.detail.creditNoteTitle' : 'sales.detail.invoice');
    return i.number ? `${title} ${i.number}` : `${title} (${this.i18n.t('sales.invoices.draft')})`;
  });
  protected readonly facts = computed(() => {
    const q = this.quotation();
    const date = (d: string | null) => (d ? formatDate(d) : '–');
    if (q) {
      return [
        { label: 'sales.fields.number', value: q.number, testId: 'sales-detail-number' },
        { label: 'sales.fields.date', value: date(q.issueDate), testId: null },
        { label: 'sales.fields.validUntil', value: date(q.validUntil), testId: null },
        { label: 'sales.fields.priceMode', value: this.i18n.t(`sales.priceMode.${q.priceMode}`), testId: null },
      ];
    }
    const i = this.invoice();
    if (!i) return [];
    return [
      { label: 'sales.fields.number', value: i.number ?? this.i18n.t('sales.invoices.draft'), testId: 'sales-detail-number' },
      { label: 'sales.fields.invoiceDate', value: date(i.invoiceDate), testId: null },
      { label: 'sales.fields.serviceDate', value: date(i.serviceDate), testId: null },
      { label: 'sales.fields.dueDate', value: i.kind === 'Invoice' ? date(i.dueDate) : '–', testId: null },
      { label: 'sales.fields.paymentTermDays', value: String(i.paymentTermDays), testId: null },
      { label: 'sales.fields.priceMode', value: this.i18n.t(`sales.priceMode.${i.priceMode}`), testId: null },
    ];
  });
  protected readonly changeText = computed(() => {
    const amount = parseAmount(this.collectAmount());
    const given = parseAmount(this.tendered());
    if (amount === null || given === null || given < amount) return null;
    return this.i18n.t('sales.detail.change', { amount: formatEuro(given - amount) });
  });
  protected readonly collectAmountText = computed(() => {
    const a = parseAmount(this.collectAmount());
    return a === null ? '' : formatEuro(a);
  });
  private readonly refundRight = computed(() =>
    this.nav().area === 'pos' ? posRights(this.cashiers.membership()).refund : this.session.can('pos.refund'),
  );

  constructor() {
    effect(() => {
      const kind = this.kind();
      const id = this.id();
      untracked(() => void this.load(kind, id));
    });
  }

  private async load(kind: Kind, id: string): Promise<void> {
    this.error.set(null);
    try {
      if (kind === 'quotation') this.quotation.set(await firstValueFrom(this.api.quotation(id)));
      else this.invoice.set(await firstValueFrom(this.api.invoice(id)));
    } catch (e) {
      this.error.set(e);
    }
  }

  protected qTone(status: string) {
    return quotationTone(status);
  }

  protected badge(i: SalesInvoice) {
    return invoiceBadge(i);
  }

  protected rate(bp: number): string {
    return rateLabel(bp);
  }

  protected taxLabel(l: SalesLine): string {
    return l.margin25a ? '§25a' : rateLabel(l.vatRateBasisPoints);
  }

  protected receipt(id: string): string[] {
    return receiptLink(id);
  }

  protected openDialog(name: DialogName): void {
    this.dialogError.set(null);
    const i = this.invoice();
    if (name === 'email') this.emailTo.set('');
    if (name === 'payment' && i) {
      this.payAmount.set(minorToInput(i.openMinor));
      this.payDate.set(isoToday());
      this.payRef.set('');
    }
    if (name === 'collect' && i) {
      if (!this.ctx?.session()) {
        this.toast.error(this.i18n.t('sales.detail.collectNeedsSession'));
        return;
      }
      this.collectAmount.set(minorToInput(i.openMinor));
      this.tendered.set('');
      this.collectKey = newIdempotencyKey();
    }
    if (name === 'creditNote') this.reason.set('');
    this.dialog.set(name);
  }

  protected closeDialog(): void {
    this.dialog.set(null);
  }

  protected async pdf(print: boolean): Promise<void> {
    const d = this.doc();
    if (!d) return;
    this.busy.set(print ? 'print' : 'pdf');
    try {
      const blob = await firstValueFrom(this.kind() === 'quotation' ? this.api.quotationPdf(d.id) : this.api.invoicePdf(d.id));
      const name = `${this.quotation()?.number ?? this.invoice()?.number ?? 'Rechnung-Entwurf'}.pdf`;
      if (print) openBlob(this.document, blob, name);
      else saveBlob(this.document, blob, name);
    } catch (e) {
      this.toast.error(posErrorText(this.i18n, e) ?? e);
    } finally {
      this.busy.set(null);
    }
  }

  protected async sendEmail(): Promise<void> {
    const d = this.doc();
    if (!d) return;
    const email = this.emailTo().trim() || null;
    await this.runDialog(this.kind() === 'quotation' ? this.api.emailQuotation(d.id, email) : this.api.emailInvoice(d.id, email), (r) => {
      this.toast.success(this.i18n.t('sales.detail.emailSent', { to: r.recipientMasked }));
      if (this.kind() === 'quotation') void this.load('quotation', d.id);
    });
  }

  protected async setStatus(status: 'Draft' | 'Sent' | 'Accepted' | 'Rejected'): Promise<void> {
    const q = this.quotation();
    if (!q) return;
    try {
      this.quotation.set(await firstValueFrom(this.api.setQuotationStatus(q.id, status)));
    } catch (e) {
      this.toast.error(posErrorText(this.i18n, e) ?? e);
    }
  }

  protected async convert(): Promise<void> {
    const q = this.quotation();
    if (!q) return;
    await this.runBusy('convert', this.api.convertQuotation(q.id), (inv) => {
      this.toast.success(this.i18n.t('sales.detail.converted'));
      void this.router.navigate([...this.nav().invoices, inv.id]);
    });
  }

  protected async duplicate(): Promise<void> {
    const d = this.doc();
    if (!d) return;
    const call: Observable<{ id: string }> = this.kind() === 'quotation' ? this.api.duplicateQuotation(d.id) : this.api.duplicateInvoice(d.id);
    await this.runBusy('duplicate', call, (copy) => {
      this.toast.success(this.i18n.t('sales.detail.duplicated'));
      void this.router.navigate([...this.base(), copy.id, 'bearbeiten']);
    });
  }

  protected async discard(): Promise<void> {
    const i = this.invoice();
    if (!i || !globalThis.confirm?.(this.i18n.t('sales.detail.discardConfirm'))) return;
    await this.runBusy('discard', this.api.discardInvoice(i.id), () => void this.router.navigate(this.nav().invoices));
  }

  protected async issue(): Promise<void> {
    const i = this.invoice();
    if (!i) return;
    await this.runDialog(this.api.issueInvoice(i.id), (inv) => {
      this.invoice.set(inv);
      this.toast.success(this.i18n.t('sales.detail.issued', { number: inv.number ?? '' }));
    });
  }

  protected async recordPayment(): Promise<void> {
    const i = this.invoice();
    const amount = parseAmount(this.payAmount());
    if (!i) return;
    if (amount === null || amount <= 0) {
      this.dialogError.set(this.i18n.t('sales.form.errors.amount'));
      return;
    }
    await this.runDialog(
      this.api.recordPayment(i.id, { method: this.payMethod(), amountMinor: amount, paidOn: this.payDate() || null, reference: this.payRef().trim() || null }),
      (inv) => {
        this.invoice.set(inv);
        this.toast.success(this.i18n.t('sales.detail.paymentRecorded'));
      },
    );
  }

  protected async collect(): Promise<void> {
    const i = this.invoice();
    const registerId = this.nav().registerId;
    const amount = parseAmount(this.collectAmount());
    if (!i || !registerId || !this.collectKey) return;
    if (amount === null || amount <= 0 || amount > i.openMinor) {
      this.dialogError.set(this.i18n.t('sales.detail.collectAmountInvalid'));
      return;
    }
    let paid = amount;
    if (this.tillMethod() === 'Cash' && this.tendered().trim()) {
      const given = parseAmount(this.tendered());
      if (given === null || given < amount) {
        this.dialogError.set(this.i18n.t('sales.detail.tenderedTooLow'));
        return;
      }
      paid = given;
    }
    await this.runDialog(
      this.api.collect(i.id, { registerId, amountMinor: amount, payments: [{ method: this.tillMethod(), amountMinor: paid }] }, this.collectKey),
      (r) => {
        this.collectKey = null;
        this.toast.success(this.i18n.t('sales.detail.collected', { number: r.receipt.number }));
        void this.router.navigate(receiptLink(r.receipt.id));
      },
    );
  }

  /** Without pos.refund the POS asks a manager to approve first (PIN pad); the approval then travels with the request. */
  protected startCreditNote(): void {
    if (!this.refundRight()) {
      if (this.nav().area === 'pos' && this.ctx) {
        this.ctx.requestApproval(['pos.refund']);
        return;
      }
      this.toast.error(this.i18n.t('sales.detail.refundRequired'));
      return;
    }
    this.openDialog('creditNote');
  }

  protected async creditNote(): Promise<void> {
    const i = this.invoice();
    if (!i || !this.reason().trim()) return;
    await this.runDialog(this.api.creditNote(i.id, { reason: this.reason().trim(), registerId: this.nav().registerId }), (note) => {
      this.toast.success(this.i18n.t('sales.detail.creditNoteIssued', { number: note.number ?? '' }));
      void this.router.navigate([...this.nav().invoices, note.id]);
    });
  }

  private async runDialog<T>(call: Observable<T>, done: (value: T) => void): Promise<void> {
    this.dialogBusy.set(true);
    this.dialogError.set(null);
    try {
      const value = await firstValueFrom(call);
      this.dialog.set(null);
      done(value);
    } catch (e) {
      this.dialogError.set(posErrorText(this.i18n, e));
    } finally {
      this.dialogBusy.set(false);
    }
  }

  private async runBusy<T>(name: string, call: Observable<T>, done: (value: T) => void): Promise<void> {
    this.busy.set(name);
    try {
      done(await firstValueFrom(call));
    } catch (e) {
      this.toast.error(posErrorText(this.i18n, e) ?? e);
    } finally {
      this.busy.set(null);
    }
  }
}

function formatDate(iso: string): string {
  const [y, m, d] = iso.slice(0, 10).split('-');
  return `${d}.${m}.${y}`;
}

function formatEuro(minor: number): string {
  return new Intl.NumberFormat('de-DE', { style: 'currency', currency: 'EUR' }).format(minor / 100);
}
