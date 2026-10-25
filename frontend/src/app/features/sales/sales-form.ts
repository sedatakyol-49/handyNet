import { ChangeDetectionStrategy, Component, ElementRef, computed, effect, inject, input, signal, untracked, viewChild } from '@angular/core';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { SalesApi } from '../../core/api/handynet-api';
import { PosCustomer, PosProduct, PriceMode, SALES_VAT_RATES, SalesCustomer, SalesInvoice, Quotation } from '../../core/api/models';
import { I18nService } from '../../core/i18n/i18n.service';
import { ToastService } from '../../core/ui-state/toast.service';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';
import { PosCustomerDialogComponent } from '../pos/pos-customer-dialog';
import { posErrorText } from '../pos/pos-error';
import { SalesNav } from './sales-nav';
import { SalesProductPickerComponent } from './sales-product-picker';
import {
  DraftLine,
  LineErrors,
  MAX_LINES,
  addDays,
  computeTotals,
  customerLines,
  emptyLine,
  isoToday,
  lineFromStored,
  lineRequest,
  missingInvoiceAddress,
  parseLine,
  percentToDiscount,
  rateLabel,
} from './sales.logic';
import { minorToInput } from '../../core/format/money';

type Kind = 'quotation' | 'invoice';

/**
 * Create/edit form of a quotation or an invoice draft (shared by POS and back office). Touch first (44 px targets, big scan
 * field) and keyboard friendly (scan/search + Enter adds a line, Tab through the line fields). Lines: catalog products,
 * devices by IMEI (§25a follows the unit) or free text; per line quantity, unit price (gross or net), discount (amount or
 * %), VAT 19/7/0 %. The totals preview uses the same arithmetic as the API.
 */
@Component({
  selector: 'hn-sales-form',
  imports: [PosCustomerDialogComponent, SalesProductPickerComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    <hn-page-header [title]="title()">
      <button hnButton type="button" class="min-h-11" (click)="cancel()" data-testid="sales-form-cancel">{{ 'common.cancel' | t }}</button>
      <button hnButton variant="primary" type="button" class="min-h-11" [loading]="saving()" [disabled]="saving()" (click)="save()" data-testid="sales-form-save">
        {{ 'common.save' | t }}
      </button>
    </hn-page-header>

    @if (loadError()) {
      <hn-banner class="mb-3" [error]="loadError()" />
    }
    @if (saveError()) {
      <hn-banner class="mb-3" [text]="saveError()" data-testid="sales-form-error" />
    }

    <div class="grid gap-4 lg:grid-cols-[minmax(0,1fr)_20rem]">
      <div class="min-w-0 space-y-4">
        <!-- customer -->
        <section class="hn-card p-4" aria-labelledby="sales-customer-h">
          <div class="flex flex-wrap items-center justify-between gap-2">
            <h2 id="sales-customer-h" class="text-sm">{{ 'sales.fields.customer' | t }}</h2>
            <button hnButton type="button" class="min-h-11" (click)="pickCustomer.set(true)" data-testid="sales-customer-pick">
              {{ (customer() ? 'sales.form.changeCustomer' : 'sales.form.chooseCustomer') | t }}
            </button>
          </div>
          @if (customer(); as c) {
            <address class="mt-2 text-sm not-italic text-slate-800" data-testid="sales-customer">
              @for (line of customerText(); track $index) {
                <span class="block">{{ line }}</span>
              }
              @if (c.vatId) {
                <span class="block text-xs text-slate-500">{{ 'sales.fields.vatId' | t }}: {{ c.vatId }}</span>
              }
            </address>
            @if (kind() === 'invoice' && missingAddress()) {
              <p class="mt-2 text-sm text-amber-800" role="note" data-testid="sales-address-missing">{{ 'sales.form.addressMissing' | t }}</p>
            }
            @if (kind() === 'invoice' && c.company && !c.vatId) {
              <p class="mt-1 text-sm text-amber-800" role="note">{{ 'sales.form.vatIdMissing' | t }}</p>
            }
          } @else {
            <p class="mt-2 text-sm text-slate-500">{{ 'sales.form.noCustomer' | t }}</p>
            @if (showErrors() && kind() === 'quotation') {
              <p class="mt-1 text-sm text-red-700" role="alert">{{ 'sales.form.customerRequired' | t }}</p>
            }
          }
        </section>

        <!-- lines -->
        <section class="hn-card p-4" aria-labelledby="sales-lines-h">
          <h2 id="sales-lines-h" class="mb-2 text-sm">{{ 'sales.form.lines' | t }}</h2>
          <form class="flex flex-wrap gap-2" (submit)="$event.preventDefault(); scan()">
            <label for="sales-scan" class="sr-only">{{ 'sales.form.scan' | t }}</label>
            <input #scanInput hnInput id="sales-scan" class="h-12 min-w-0 flex-1 basis-60 text-base" autocomplete="off" [placeholder]="'sales.form.scan' | t"
              [value]="scanText()" (input)="scanText.set($any($event.target).value)" data-testid="sales-scan" />
            <button hnButton type="submit" class="min-h-12" [loading]="scanning()">{{ 'sales.form.add' | t }}</button>
            <button hnButton type="button" class="min-h-12" (click)="openPicker('')" data-testid="sales-search-product">{{ 'sales.form.searchProduct' | t }}</button>
            <button hnButton type="button" class="min-h-12" (click)="addFree()" data-testid="sales-add-free">{{ 'sales.form.addFree' | t }}</button>
          </form>
          @if (scanError()) {
            <p class="mt-2 text-sm text-red-700" role="alert" data-testid="sales-scan-error">{{ scanError() }}</p>
          }

          <ol class="mt-3 space-y-3" data-testid="sales-lines">
            @for (l of lines(); track l.key; let i = $index) {
              @let e = errorsOf(l);
              <li class="rounded-lg border border-slate-200 p-3" [attr.data-line]="i + 1">
                <div class="grid gap-2 sm:grid-cols-12">
                  <div class="sm:col-span-12">
                    <label [for]="'d-' + l.key" class="mb-1 block text-xs font-medium text-slate-700">{{ i + 1 }}. {{ 'sales.fields.description' | t }}</label>
                    <input hnInput [id]="'d-' + l.key" class="h-11" [value]="l.description" maxlength="300" (input)="patch(l, { description: $any($event.target).value })"
                      [attr.aria-invalid]="!!e.description" data-testid="line-description" />
                    <p class="mt-1 text-xs text-slate-500">
                      @if (l.imei) {
                        IMEI {{ l.imei }}
                      }
                      @if (l.margin25a) {
                        · {{ 'sales.form.marginLine' | t }}
                      }
                      @if (!l.productId) {
                        {{ 'sales.form.freeLine' | t }}
                      } @else if (l.available !== null && !l.imei) {
                        {{ 'sales.picker.available' | t: { count: l.available } }}
                      }
                    </p>
                  </div>
                  <div class="sm:col-span-2">
                    <label [for]="'q-' + l.key" class="mb-1 block text-xs font-medium text-slate-700">{{ 'sales.fields.quantity' | t }}</label>
                    <input hnInput [id]="'q-' + l.key" class="h-11" inputmode="numeric" [value]="l.quantity" [readOnly]="!!l.imei"
                      (input)="patch(l, { quantity: $any($event.target).value })" [attr.aria-invalid]="!!e.quantity" data-testid="line-quantity" />
                  </div>
                  <div class="sm:col-span-3">
                    <label [for]="'p-' + l.key" class="mb-1 block text-xs font-medium text-slate-700">
                      {{ (l.margin25a ? 'sales.form.finalPrice' : priceMode() === 'Net' ? 'sales.form.unitNet' : 'sales.form.unitGross') | t }}
                    </label>
                    <input hnInput [id]="'p-' + l.key" class="h-11" inputmode="decimal" [value]="l.unitPrice" (input)="patch(l, { unitPrice: $any($event.target).value })"
                      [attr.aria-invalid]="!!e.unitPrice" data-testid="line-price" />
                  </div>
                  <div class="sm:col-span-3">
                    <label [for]="'r-' + l.key" class="mb-1 block text-xs font-medium text-slate-700">{{ 'sales.fields.discount' | t }}</label>
                    <input hnInput [id]="'r-' + l.key" class="h-11" inputmode="decimal" [value]="l.discount" (input)="patch(l, { discount: $any($event.target).value })"
                      (blur)="percentDiscount(l)" [attr.aria-invalid]="!!e.discount" [attr.aria-describedby]="'rh-' + l.key" data-testid="line-discount" />
                    <p class="mt-0.5 text-[11px] text-slate-500" [id]="'rh-' + l.key">{{ 'sales.form.discountHint' | t }}</p>
                  </div>
                  <div class="sm:col-span-2">
                    <label [for]="'v-' + l.key" class="mb-1 block text-xs font-medium text-slate-700">{{ 'sales.fields.vat' | t }}</label>
                    @if (l.margin25a) {
                      <p class="flex h-11 items-center text-sm font-semibold" [id]="'v-' + l.key">§25a</p>
                    } @else {
                      <select hnSelect [id]="'v-' + l.key" class="h-11" [value]="l.vatRateBasisPoints" (change)="patch(l, { vatRateBasisPoints: +$any($event.target).value })"
                        data-testid="line-vat">
                        @for (r of rates; track r) {
                          <option [value]="r" [selected]="r === l.vatRateBasisPoints">{{ rateText(r) }}</option>
                        }
                      </select>
                    }
                  </div>
                  <div class="flex items-end justify-between gap-2 sm:col-span-2 sm:flex-col sm:items-end">
                    <span class="flex h-11 items-center font-semibold tabular-nums" data-testid="line-amount">{{ amountOf(l) === null ? '–' : (amountOf(l)! | money) }}</span>
                    <button type="button" class="hn-icon-btn min-h-11 min-w-11 text-red-700" (click)="remove(l)" [attr.aria-label]="'sales.form.removeLine' | t: { n: i + 1 }"
                      data-testid="line-remove">✕</button>
                  </div>
                  <div class="sm:col-span-12">
                    <label [for]="'n-' + l.key" class="sr-only">{{ 'sales.fields.note' | t }}</label>
                    <input hnInput [id]="'n-' + l.key" class="h-10 text-sm" maxlength="500" [placeholder]="'sales.form.notePlaceholder' | t" [value]="l.note"
                      (input)="patch(l, { note: $any($event.target).value })" />
                  </div>
                </div>
                @if (errorText(e); as msg) {
                  <p class="mt-1 text-sm text-red-700" role="alert">{{ msg }}</p>
                }
              </li>
            } @empty {
              <li class="rounded-lg border border-dashed border-slate-300 px-3 py-6 text-center text-sm text-slate-500" data-testid="sales-lines-empty">
                {{ 'sales.form.noLines' | t }}
              </li>
            }
          </ol>
        </section>

        <section class="hn-card p-4">
          <label for="sales-notes" class="mb-1 block text-xs font-medium text-slate-700">{{ 'sales.fields.notes' | t }}</label>
          <textarea hnInput id="sales-notes" rows="3" maxlength="2000" [value]="notes()" (input)="notes.set($any($event.target).value)" data-testid="sales-notes"></textarea>
        </section>
      </div>

      <!-- settings and totals -->
      <aside class="space-y-4">
        <section class="hn-card space-y-3 p-4" aria-labelledby="sales-settings-h">
          <h2 id="sales-settings-h" class="text-sm">{{ 'sales.form.settings' | t }}</h2>
          <div role="group" [attr.aria-label]="'sales.fields.priceMode' | t">
            <p class="mb-1 text-xs font-medium text-slate-700" aria-hidden="true">{{ 'sales.fields.priceMode' | t }}</p>
            <div class="grid grid-cols-2 gap-1.5">
              @for (m of modes; track m) {
                <button type="button" class="pos-mode" [attr.aria-pressed]="priceMode() === m" (click)="priceMode.set(m)" [attr.data-testid]="'sales-mode-' + m">
                  {{ 'sales.priceMode.' + m | t }}
                </button>
              }
            </div>
          </div>
          @if (kind() === 'quotation') {
            <div>
              <label for="sales-valid" class="mb-1 block text-xs font-medium text-slate-700">{{ 'sales.fields.validUntil' | t }}</label>
              <input hnInput id="sales-valid" type="date" class="h-11" [value]="validUntil()" (input)="validUntil.set($any($event.target).value)" data-testid="sales-valid-until" />
            </div>
          } @else {
            <div>
              <label for="sales-service" class="mb-1 block text-xs font-medium text-slate-700">{{ 'sales.fields.serviceDate' | t }}</label>
              <input hnInput id="sales-service" type="date" class="h-11" [value]="serviceDate()" (input)="serviceDate.set($any($event.target).value)" />
              <p class="mt-1 text-xs text-slate-500">{{ 'sales.form.serviceDateHint' | t }}</p>
            </div>
            <div>
              <label for="sales-terms" class="mb-1 block text-xs font-medium text-slate-700">{{ 'sales.fields.paymentTermDays' | t }}</label>
              <input hnInput id="sales-terms" class="h-11" inputmode="numeric" [value]="termDays()" (input)="termDays.set($any($event.target).value)"
                [attr.aria-invalid]="showErrors() && termInvalid()" data-testid="sales-terms" />
            </div>
            <div>
              <label for="sales-warehouse" class="mb-1 block text-xs font-medium text-slate-700">{{ 'sales.fields.warehouse' | t }}</label>
              <select hnSelect id="sales-warehouse" class="h-11" [value]="warehouseId() ?? ''" (change)="warehouseId.set($any($event.target).value || null)" data-testid="sales-warehouse">
                @for (w of warehouses.value() ?? []; track w.id) {
                  <option [value]="w.id" [selected]="w.id === warehouseId()">{{ w.name }}</option>
                }
              </select>
            </div>
            <label class="flex min-h-11 items-start gap-2 text-sm">
              <input type="checkbox" class="hn-checkbox mt-1" [checked]="reverseCharge()" (change)="setReverseCharge($any($event.target).checked)" data-testid="sales-reverse-charge" />
              <span>{{ 'sales.fields.reverseCharge' | t }}</span>
            </label>
            @if (reverseCharge()) {
              <hn-banner tone="warning" [text]="'sales.form.reverseChargeWarning' | t" data-testid="sales-rc-warning" />
            }
          }
        </section>

        <section class="hn-card p-4" aria-labelledby="sales-totals-h" data-testid="sales-totals">
          <h2 id="sales-totals-h" class="mb-2 text-sm">{{ 'sales.fields.totals' | t }}</h2>
          <dl class="space-y-1 text-sm">
            @for (v of totals().vat; track v.rateBasisPoints) {
              <div class="flex justify-between"><dt>{{ 'sales.totals.net' | t: { rate: rateText(v.rateBasisPoints) } }}</dt><dd class="tabular-nums">{{ v.netMinor | money }}</dd></div>
              <div class="flex justify-between text-slate-600"><dt>{{ 'sales.totals.vat' | t: { rate: rateText(v.rateBasisPoints) } }}</dt><dd class="tabular-nums">{{ v.vatMinor | money }}</dd></div>
            }
            @if (totals().marginGrossMinor) {
              <div class="flex justify-between"><dt>{{ 'sales.totals.margin' | t }}</dt><dd class="tabular-nums">{{ totals().marginGrossMinor | money }}</dd></div>
            }
            <div class="flex justify-between border-t border-slate-200 pt-2 text-base font-bold"><dt>{{ 'sales.fields.total' | t }}</dt>
              <dd class="tabular-nums" data-testid="sales-total">{{ totals().totalGrossMinor | money }}</dd></div>
          </dl>
          @if (totals().marginGrossMinor) {
            <p class="mt-2 text-xs text-slate-500">{{ 'sales.form.marginHint' | t }}</p>
          }
        </section>
      </aside>
    </div>

    <hn-pos-customer-dialog [open]="pickCustomer()" mode="pick" [attachedId]="customer()?.customerId ?? null" (chosen)="chooseCustomer($event)" (closed)="pickCustomer.set(false)" />
    <hn-sales-product-picker [open]="picker()" [warehouseId]="productWarehouse()" [initialSearch]="pickerSearch()" (picked)="addProduct($event)" (closed)="picker.set(false)" />
  `,
})
export class SalesFormComponent {
  private readonly api = inject(SalesApi);
  private readonly i18n = inject(I18nService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);
  private readonly scanInput = viewChild<ElementRef<HTMLInputElement>>('scanInput');

  readonly kind = input.required<Kind>();
  readonly nav = input.required<SalesNav>();
  /** Edit an existing quotation / invoice draft; null = new. */
  readonly id = input<string | null>(null);

  protected readonly modes: PriceMode[] = ['Gross', 'Net'];
  protected readonly rates = SALES_VAT_RATES;

  protected readonly customer = signal<SalesCustomer | null>(null);
  protected readonly lines = signal<DraftLine[]>([]);
  protected readonly priceMode = signal<PriceMode>('Gross');
  protected readonly validUntil = signal(addDays(isoToday(), 30));
  protected readonly serviceDate = signal('');
  protected readonly termDays = signal('14');
  protected readonly warehouseId = signal<string | null>(null);
  protected readonly reverseCharge = signal(false);
  protected readonly notes = signal('');
  protected readonly showErrors = signal(false);
  protected readonly saving = signal(false);
  protected readonly saveError = signal<string | null>(null);
  protected readonly loadError = signal<unknown>(null);
  protected readonly pickCustomer = signal(false);
  protected readonly picker = signal(false);
  protected readonly pickerSearch = signal('');
  protected readonly scanText = signal('');
  protected readonly scanning = signal(false);
  protected readonly scanError = signal<string | null>(null);

  protected readonly warehouses = createQuery(
    () => true,
    () => this.api.warehouses(),
  );
  /** Products are searched in the invoice's warehouse (quotations: the default one). */
  protected readonly productWarehouse = computed(() => this.warehouseId() ?? this.warehouses.value()?.[0]?.id ?? null);

  protected readonly title = computed(() => {
    const k = this.kind() === 'quotation' ? 'quotation' : 'invoice';
    return this.i18n.t(this.id() ? `sales.form.edit.${k}` : `sales.form.new.${k}`);
  });
  protected readonly customerText = computed(() => customerLines(this.customer()));
  protected readonly missingAddress = computed(() => missingInvoiceAddress(this.customer()));
  protected readonly termInvalid = computed(() => !/^\d{1,3}$/.test(this.termDays().trim()) || Number(this.termDays()) > 365);
  protected readonly totals = computed(() =>
    computeTotals(
      this.priceMode(),
      this.lines().map((l) => ({ amountMinor: parseLine(l).value?.amountMinor ?? 0, vatRateBasisPoints: l.vatRateBasisPoints, margin25a: l.margin25a })),
    ),
  );

  constructor() {
    effect(() => {
      const id = this.id();
      const kind = this.kind();
      untracked(() => {
        if (id) void this.load(kind, id);
      });
    });
    effect(() => {
      const list = this.warehouses.value();
      untracked(() => {
        if (!this.warehouseId() && list?.length && !this.id()) this.warehouseId.set(list[0].id);
      });
    });
  }

  private async load(kind: Kind, id: string): Promise<void> {
    try {
      if (kind === 'quotation') {
        const q: Quotation = await firstValueFrom(this.api.quotation(id));
        this.customer.set(q.customer);
        this.priceMode.set(q.priceMode);
        this.validUntil.set(q.validUntil);
        this.notes.set(q.notes ?? '');
        this.lines.set(q.lines.map(lineFromStored));
      } else {
        const inv: SalesInvoice = await firstValueFrom(this.api.invoice(id));
        this.customer.set(inv.customer);
        this.priceMode.set(inv.priceMode);
        this.serviceDate.set(inv.serviceDate ?? '');
        this.termDays.set(String(inv.paymentTermDays));
        this.warehouseId.set(inv.warehouseId);
        this.reverseCharge.set(inv.reverseCharge);
        this.notes.set(inv.notes ?? '');
        this.lines.set(inv.lines.map(lineFromStored));
      }
    } catch (e) {
      this.loadError.set(e);
    }
  }

  protected rateText(bp: number): string {
    return rateLabel(bp);
  }

  protected amountOf(l: DraftLine): number | null {
    return parseLine(l).value?.amountMinor ?? null;
  }

  protected errorsOf(l: DraftLine): LineErrors {
    return this.showErrors() ? parseLine(l).errors : {};
  }

  protected errorText(e: LineErrors): string | null {
    const first = Object.values(e)[0];
    return first ? this.i18n.t(`sales.form.errors.${first}`) : null;
  }

  protected patch(line: DraftLine, change: Partial<DraftLine>): void {
    this.lines.update((ls) => ls.map((l) => (l.key === line.key ? { ...l, ...change } : l)));
  }

  protected remove(line: DraftLine): void {
    this.lines.update((ls) => ls.filter((l) => l.key !== line.key));
  }

  /** "10 %" typed into the discount field becomes 10 % of unit price × quantity (cents, truncated). */
  protected percentDiscount(line: DraftLine): void {
    const current = this.lines().find((l) => l.key === line.key);
    const match = current?.discount.trim().match(/^(\d{1,3}(?:[.,]\d{1,2})?)\s*%$/);
    if (!current || !match) return;
    const percent = Number(match[1].replace(',', '.'));
    const parsed = parseLine({ ...current, discount: '' }).value;
    if (!parsed || !(percent > 0 && percent <= 100)) return;
    this.patch(current, { discount: minorToInput(percentToDiscount(parsed.unitPriceMinor, parsed.quantity, percent)) });
  }

  protected setReverseCharge(on: boolean): void {
    this.reverseCharge.set(on);
    if (on) this.lines.update((ls) => ls.map((l) => (l.margin25a ? l : { ...l, vatRateBasisPoints: 0 })));
  }

  protected chooseCustomer(c: PosCustomer): void {
    this.pickCustomer.set(false);
    this.customer.set({
      customerId: c.id, name: c.name, company: c.company, street: c.street, postalCode: c.postalCode, city: c.city, countryCode: c.countryCode, vatId: c.vatId,
    });
  }

  protected addFree(): void {
    if (this.lines().length >= MAX_LINES) return;
    const line = { ...emptyLine(), vatRateBasisPoints: this.reverseCharge() ? 0 : 1900 };
    this.lines.update((ls) => [...ls, line]);
    this.focusLater(`d-${line.key}`);
  }

  protected openPicker(search: string): void {
    this.pickerSearch.set(search);
    this.picker.set(true);
  }

  protected addProduct(p: PosProduct): void {
    this.picker.set(false);
    if (p.serialized) {
      this.scanError.set(this.i18n.t('sales.form.scanImei'));
      this.scanInput()?.nativeElement.focus();
      return;
    }
    this.addOrIncrement({
      productId: p.productId, description: `${p.brand} ${p.name}`.trim(), price: p.priceMinor, available: p.available,
    });
  }

  private addOrIncrement(x: { productId: string; description: string; price: number | null; available: number | null }): void {
    this.scanError.set(null);
    const existing = this.lines().find((l) => l.productId === x.productId && !l.imei);
    if (existing) {
      const q = Number(existing.quantity) || 0;
      this.patch(existing, { quantity: String(q + 1) });
      return;
    }
    const price = x.price === null ? '' : minorToInput(this.priceMode() === 'Net' ? netOf(x.price) : x.price);
    const line: DraftLine = {
      ...emptyLine(), productId: x.productId, description: x.description, unitPrice: price, available: x.available,
      vatRateBasisPoints: this.reverseCharge() ? 0 : 1900,
    };
    this.lines.update((ls) => [...ls, line]);
    this.focusLater(price ? `q-${line.key}` : `p-${line.key}`);
  }

  /** Scanned EAN / IMEI (or typed text) + Enter: device or product line; unknown text opens the product search. */
  protected async scan(): Promise<void> {
    const code = this.scanText().trim();
    if (!code) return;
    this.scanning.set(true);
    this.scanError.set(null);
    try {
      const hit = await firstValueFrom(this.api.lookup(code, this.nav().registerId));
      this.scanText.set('');
      if (hit.kind === 'Unit') {
        if (this.lines().some((l) => l.imei === hit.imei)) {
          this.scanError.set(this.i18n.t('sales.form.deviceTwice'));
          return;
        }
        const margin = hit.taxTreatment === 'Margin25a';
        const price = hit.priceMinor ?? null;
        this.lines.update((ls) => [
          ...ls,
          {
            ...emptyLine(), productId: hit.productId, imei: hit.imei, stockUnitId: hit.stockUnitId, margin25a: margin, description: `${hit.brand} ${hit.name}`.trim(),
            unitPrice: price === null ? '' : minorToInput(margin || this.priceMode() === 'Gross' ? price : netOf(price)), vatRateBasisPoints: margin || !this.reverseCharge() ? 1900 : 0,
          },
        ]);
      } else if (hit.requiresImei) {
        this.scanError.set(this.i18n.t('sales.form.scanImei'));
      } else {
        this.addOrIncrement({ productId: hit.productId, description: `${hit.brand} ${hit.name}`.trim(), price: hit.priceMinor ?? null, available: hit.available });
      }
    } catch (e) {
      if ((e as { status?: number })?.status === 404) this.openPicker(code);
      else this.scanError.set(posErrorText(this.i18n, e));
    } finally {
      this.scanning.set(false);
    }
  }

  protected async save(): Promise<void> {
    this.showErrors.set(true);
    this.saveError.set(null);
    const parsed = this.lines().map((l) => ({ l, p: parseLine(l) }));
    if (parsed.some((x) => !x.p.value)) {
      this.saveError.set(this.i18n.t('sales.form.fixLines'));
      return;
    }
    const c = this.customer();
    if (this.kind() === 'quotation' && !c) {
      this.saveError.set(this.i18n.t('sales.form.customerRequired'));
      return;
    }
    if (this.kind() === 'invoice' && this.termInvalid()) {
      this.saveError.set(this.i18n.t('sales.form.termInvalid'));
      return;
    }
    const lines = parsed.map((x) => lineRequest(x.l, x.p.value!));
    const customer = c?.customerId
      ? { customerId: c.customerId, customer: null }
      : { customerId: null, customer: c ? { name: c.name, company: c.company, street: c.street, postalCode: c.postalCode, city: c.city, countryCode: c.countryCode, vatId: c.vatId } : null };
    this.saving.set(true);
    try {
      const id = this.id();
      let savedId: string;
      if (this.kind() === 'quotation') {
        const body = { ...customer, validUntil: this.validUntil() || null, priceMode: this.priceMode(), lines, notes: this.notes().trim() || null };
        savedId = (await firstValueFrom(id ? this.api.updateQuotation(id, body) : this.api.createQuotation(body))).id;
      } else {
        const body = {
          ...customer, serviceDate: this.serviceDate() || null, paymentTermDays: Number(this.termDays()), priceMode: this.priceMode(),
          reverseCharge: this.reverseCharge(), warehouseId: this.warehouseId(), lines, notes: this.notes().trim() || null,
        };
        savedId = (await firstValueFrom(id ? this.api.updateInvoice(id, body) : this.api.createInvoice(body))).id;
      }
      this.toast.success(this.i18n.t('sales.form.saved'));
      void this.router.navigate([...(this.kind() === 'quotation' ? this.nav().quotations : this.nav().invoices), savedId]);
    } catch (e) {
      this.saveError.set(posErrorText(this.i18n, e));
    } finally {
      this.saving.set(false);
    }
  }

  protected cancel(): void {
    const base = this.kind() === 'quotation' ? this.nav().quotations : this.nav().invoices;
    void this.router.navigate(this.id() ? [...base, this.id()!] : base);
  }

  private focusLater(id: string): void {
    setTimeout(() => (globalThis.document?.getElementById(id) as HTMLInputElement | null)?.focus(), 0);
  }
}

/** Gross till price → net at 19 % (net mode), rounded half away from zero. */
function netOf(grossMinor: number): number {
  return Math.round((grossMinor * 10_000) / 11_900);
}
