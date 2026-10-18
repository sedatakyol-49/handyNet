import { ChangeDetectionStrategy, Component, DOCUMENT, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { PosApi } from '../../core/api/handynet-api';
import { PosInvoice, PosInvoiceCustomerInput, Receipt } from '../../core/api/models';
import { I18nService } from '../../core/i18n/i18n.service';
import { ToastService } from '../../core/ui-state/toast.service';
import { UI } from '../../shared/ui';
import { posErrorText } from './pos-error';

export interface InvoiceForm {
  name: string;
  company: string;
  street: string;
  postalCode: string;
  city: string;
  countryCode: string;
  vatId: string;
}

/** Prefill from the receipt's customer snapshot (name, company, address, VAT id). */
export function invoiceFormFrom(receipt: Pick<Receipt, 'customer'>): InvoiceForm {
  const c = receipt.customer;
  return {
    name: c?.name ?? '',
    company: c?.company ?? '',
    street: c?.street ?? '',
    postalCode: c?.postalCode ?? '',
    city: c?.city ?? '',
    countryCode: c?.countryCode ?? 'DE',
    vatId: c?.vatId ?? '',
  };
}

/** §14 UStG: name and full address are required; returns the request body or the missing field keys. */
export function invoiceRequestFrom(form: InvoiceForm): { ok: true; body: PosInvoiceCustomerInput } | { ok: false; missing: (keyof InvoiceForm)[] } {
  const t = (s: string) => s.trim();
  const missing: (keyof InvoiceForm)[] = (['name', 'street', 'postalCode', 'city'] as const).filter((k) => !t(form[k]));
  if (!/^[A-Za-z]{2}$/.test(t(form.countryCode) || 'DE')) missing.push('countryCode');
  if (missing.length) return { ok: false, missing };
  return {
    ok: true,
    body: {
      name: t(form.name),
      company: t(form.company) || null,
      street: t(form.street),
      postalCode: t(form.postalCode),
      city: t(form.city),
      countryCode: (t(form.countryCode) || 'DE').toUpperCase(),
      vatId: t(form.vatId) || null,
    },
  };
}

/**
 * Invoice (Rechnung, §14 UStG) for a sale receipt, on the receipt detail: shows the issued invoice with its PDF, or a
 * form with the customer's name and address (prefilled from the receipt's customer) to issue it once.
 */
@Component({
  selector: 'hn-pos-invoice',
  imports: [...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    <section class="pos-card p-4 text-sm" aria-labelledby="pos-invoice-heading" data-testid="pos-invoice">
      <h2 id="pos-invoice-heading" class="mb-2 text-sm">{{ 'pos.invoice.title' | t }}</h2>
      @if (invoice(); as inv) {
        <div class="flex flex-wrap items-center justify-between gap-2">
          <p>
            <span class="font-semibold" data-testid="pos-invoice-number">{{ inv.number }}</span>
            <span class="text-pos-muted"> · {{ inv.issueDate | hnDate: 'date' }} · {{ inv.document.customer.company ?? inv.document.customer.name }}</span>
          </p>
          <button hnButton type="button" class="min-h-11" [loading]="downloading()" [disabled]="downloading()" (click)="download(inv)" data-testid="pos-invoice-pdf">
            {{ 'pos.invoice.pdf' | t }}
          </button>
        </div>
      } @else if (loaded()) {
        @if (!open()) {
          <p class="mb-2 text-pos-muted">{{ 'pos.invoice.none' | t }}</p>
          <button hnButton type="button" class="min-h-11" (click)="start()" data-testid="pos-invoice-start">{{ 'pos.invoice.create' | t }}</button>
        } @else {
          <form class="grid gap-2 sm:grid-cols-2" (submit)="$event.preventDefault(); create()" novalidate data-testid="pos-invoice-form">
            @for (f of fields; track f.key) {
              <hn-field [class.sm:col-span-2]="f.wide" [label]="'pos.invoice.fields.' + f.key | t" [for]="'pos-invoice-' + f.key" [required]="f.required"
                [errors]="missing().includes(f.key) ? ('pos.invoice.required' | t) : null">
                <input hnInput [id]="'pos-invoice-' + f.key" [name]="f.key" autocomplete="off" class="min-h-11" [value]="form()[f.key]"
                  (input)="set(f.key, $any($event.target).value)" [attr.aria-invalid]="missing().includes(f.key) || null" [attr.data-testid]="'pos-invoice-' + f.key" />
              </hn-field>
            }
            <p class="text-xs text-pos-muted sm:col-span-2">{{ 'pos.invoice.hint' | t }}</p>
            @if (error()) {
              <p class="text-sm text-pos-danger sm:col-span-2" role="alert" data-testid="pos-invoice-error">{{ error() }}</p>
            }
            <div class="flex justify-end gap-2 sm:col-span-2">
              <button hnButton type="button" class="min-h-11" (click)="open.set(false)">{{ 'common.cancel' | t }}</button>
              <button hnButton variant="primary" type="submit" class="min-h-11" [loading]="busy()" [disabled]="busy()" data-testid="pos-invoice-submit">
                {{ 'pos.invoice.issue' | t }}
              </button>
            </div>
          </form>
        }
      }
    </section>
  `,
})
export class PosInvoiceComponent {
  private readonly api = inject(PosApi);
  private readonly i18n = inject(I18nService);
  private readonly toast = inject(ToastService);
  private readonly document = inject(DOCUMENT);

  readonly receipt = input.required<Receipt>();

  protected readonly fields: { key: keyof InvoiceForm; required: boolean; wide: boolean }[] = [
    { key: 'name', required: true, wide: false },
    { key: 'company', required: false, wide: false },
    { key: 'street', required: true, wide: true },
    { key: 'postalCode', required: true, wide: false },
    { key: 'city', required: true, wide: false },
    { key: 'countryCode', required: true, wide: false },
    { key: 'vatId', required: false, wide: false },
  ];
  protected readonly invoice = signal<PosInvoice | null>(null);
  protected readonly loaded = signal(false);
  protected readonly open = signal(false);
  protected readonly form = signal<InvoiceForm>(invoiceFormFrom({ customer: null }));
  protected readonly missing = signal<(keyof InvoiceForm)[]>([]);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly downloading = signal(false);
  protected readonly receiptId = computed(() => this.receipt().id);

  constructor() {
    effect(() => {
      const id = this.receiptId();
      untracked(() => void this.load(id));
    });
  }

  private async load(id: string): Promise<void> {
    this.loaded.set(false);
    this.invoice.set(null);
    this.open.set(false);
    try {
      this.invoice.set(await firstValueFrom(this.api.receiptInvoice(id)));
    } catch {
      /* shown as "no invoice yet"; issuing reports errors */
    }
    this.loaded.set(true);
  }

  protected start(): void {
    this.form.set(invoiceFormFrom(this.receipt()));
    this.missing.set([]);
    this.error.set(null);
    this.open.set(true);
  }

  protected set(key: keyof InvoiceForm, value: string): void {
    this.form.update((f) => ({ ...f, [key]: value }));
    this.missing.update((m) => m.filter((k) => k !== key));
  }

  protected async create(): Promise<void> {
    const request = invoiceRequestFrom(this.form());
    if (!request.ok) {
      this.missing.set(request.missing);
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    try {
      const invoice = await firstValueFrom(this.api.createInvoice(this.receipt().id, request.body));
      this.invoice.set(invoice);
      this.open.set(false);
      this.toast.success(this.i18n.t('pos.invoice.issued', { number: invoice.number }));
    } catch (e) {
      this.error.set(posErrorText(this.i18n, e));
    } finally {
      this.busy.set(false);
    }
  }

  protected async download(invoice: PosInvoice): Promise<void> {
    this.downloading.set(true);
    try {
      const blob = await firstValueFrom(this.api.invoicePdf(invoice.id));
      const url = URL.createObjectURL(blob);
      const a = this.document.createElement('a');
      a.href = url;
      a.download = `${invoice.number}.pdf`;
      this.document.body.appendChild(a);
      a.click();
      a.remove();
      setTimeout(() => URL.revokeObjectURL(url), 60_000);
    } catch (e) {
      this.toast.error(e);
    } finally {
      this.downloading.set(false);
    }
  }
}
