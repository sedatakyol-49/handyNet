import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { SessionStore } from '../../../core/auth/session.store';
import { I18nService } from '../../../core/i18n/i18n.service';
import { ToastService } from '../../../core/ui-state/toast.service';
import { UI } from '../../../shared/ui';
import { PosSubnavComponent } from '../pos-subnav';
import { posErrorText } from '../pos-error';
import { PRINTER_CONNECTIONS, PrinterApi, PrinterProfile } from './printer-api';
import {
  BAUD_RATES,
  PRINTER_LIMITS,
  PrinterForm,
  PrinterFormErrors,
  connectionSummary,
  defaultChars,
  emptyPrinterForm,
  formFromProfile,
  hexId,
  isNetwork,
  isUsb,
  printerInput,
} from './printer-form';
import { DEVICE_BROWSER, PrinterService, effectiveProfile } from './printer.service';
import { PrintError, hasPairedDevice, webSerialSupported, webUsbSupported } from './printer-transports';

type Status = { tone: 'ok' | 'danger'; text: string; fallback: boolean };

/**
 * Kasse → Drucker: receipt printer profiles of one register (ADR-0018). Everyone who sells chooses the profile this device
 * uses and runs test prints; profiles are created, changed and deleted with `pos.manage`.
 */
@Component({
  selector: 'hn-pos-printers-page',
  host: { class: 'pos-page block' },
  imports: [FormsModule, PosSubnavComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-pos-subnav [registerId]="registerId()" />
    <div class="mb-4 flex flex-wrap items-end justify-between gap-3">
      <div class="min-w-0">
        <h1 class="text-2xl font-extrabold tracking-tight">{{ 'pos.printers.title' | t }}</h1>
        <p class="mt-1 max-w-3xl text-sm text-pos-muted">{{ 'pos.printers.subtitle' | t }}</p>
      </div>
      @if (canManage()) {
        <button type="button" class="pos-soft pos-soft-accent" (click)="openCreate()" data-testid="pos-printer-new">{{ 'pos.printers.new' | t }}</button>
      }
    </div>

    <hn-banner class="mb-3" [text]="loadError()" />

    <section class="pos-card mb-4 p-4" aria-labelledby="pos-printer-device-heading" data-testid="pos-printer-device">
      <h2 id="pos-printer-device-heading" class="pos-section-label">{{ 'pos.printers.device.title' | t }}</h2>
      <div class="mt-2 flex flex-wrap items-end gap-3">
        <hn-field class="min-w-64 flex-1" [label]="'pos.printers.device.choice' | t" for="pos-printer-choice" [hint]="'pos.printers.device.hint' | t">
          <select hnSelect id="pos-printer-choice" class="min-h-11" [ngModel]="choice()" (ngModelChange)="setChoice($event)" aria-describedby="pos-printer-choice-hint" data-testid="pos-printer-choice">
            <option value="">{{ 'pos.printers.device.registerDefault' | t: { name: defaultName() } }}</option>
            <option [value]="browserChoice">{{ 'pos.printers.device.browser' | t }}</option>
            @for (p of profiles(); track p.id) {
              <option [value]="p.id">{{ p.name }}</option>
            }
          </select>
        </hn-field>
        <p class="pb-2 text-sm" data-testid="pos-printer-effective">
          {{ 'pos.printers.device.effective' | t }}: <strong>{{ effective()?.name ?? ('pos.printers.device.browser' | t) }}</strong>
        </p>
      </div>
      <p class="mt-2 flex flex-wrap gap-1.5 text-xs" data-testid="pos-printer-capabilities">
        <span class="pos-pill" [class]="usbSupported ? 'bg-pos-ok-soft text-pos-ok-text' : 'bg-pos-key text-pos-muted'">WebUSB: {{ (usbSupported ? 'pos.printers.device.available' : 'pos.printers.device.unavailable') | t }}</span>
        <span class="pos-pill" [class]="serialSupported ? 'bg-pos-ok-soft text-pos-ok-text' : 'bg-pos-key text-pos-muted'">WebSerial: {{ (serialSupported ? 'pos.printers.device.available' : 'pos.printers.device.unavailable') | t }}</span>
      </p>
    </section>

    @if (loaded() && profiles().length === 0) {
      <div class="pos-card mb-4">
        <hn-empty-state [title]="'pos.printers.emptyTitle' | t" [text]="(canManage() ? 'pos.printers.emptyManage' : 'pos.printers.emptySell') | t" />
      </div>
    }

    <ul class="grid gap-4 lg:grid-cols-2" data-testid="pos-printers">
      @for (p of profiles(); track p.id) {
        @let st = status()[p.id];
        <li class="pos-card flex flex-col p-4" [attr.data-printer]="p.name">
          <div class="flex items-start justify-between gap-3">
            <div class="min-w-0">
              <h2 class="truncate text-lg font-bold">{{ p.name }}</h2>
              <p class="text-sm text-pos-muted">{{ 'pos.printers.connection.' + p.connection | t }}</p>
            </div>
            <div class="flex flex-wrap justify-end gap-1.5">
              @if (p.isDefault) {
                <span class="pos-pill bg-pos-accent-soft text-pos-accent-text" data-testid="pos-printer-default">{{ 'pos.printers.default' | t }}</span>
              }
              @if (effective()?.id === p.id) {
                <span class="pos-pill bg-pos-ok-soft text-pos-ok-text">{{ 'pos.printers.thisDevice' | t }}</span>
              }
            </div>
          </div>
          <dl class="mt-3 grid grid-cols-2 gap-2 text-sm sm:grid-cols-4">
            <div class="rounded-[10px] bg-pos-surface px-3 py-2"><dt class="text-xs text-pos-muted">{{ 'pos.printers.form.paper' | t }}</dt><dd class="font-semibold">{{ p.paperWidthMm }} mm</dd></div>
            <div class="rounded-[10px] bg-pos-surface px-3 py-2"><dt class="text-xs text-pos-muted">{{ 'pos.printers.form.chars' | t }}</dt><dd class="font-semibold tabular-nums">{{ p.charsPerLine }}</dd></div>
            <div class="rounded-[10px] bg-pos-surface px-3 py-2"><dt class="text-xs text-pos-muted">{{ 'pos.printers.form.copies' | t }}</dt><dd class="font-semibold tabular-nums">{{ p.copies }}</dd></div>
            <div class="rounded-[10px] bg-pos-surface px-3 py-2"><dt class="text-xs text-pos-muted">{{ 'pos.printers.form.codepage' | t }}</dt><dd class="font-semibold">{{ p.codepage === 'Cp858' ? 'CP858' : 'PC437' }}</dd></div>
          </dl>
          @if (summary(p); as s) {
            <p class="mt-2 truncate font-mono text-xs text-pos-muted" [attr.title]="s" data-testid="pos-printer-address">{{ s }}</p>
          }
          <p class="mt-2 flex flex-wrap gap-1.5 text-xs">
            <span class="pos-pill" [class]="p.autoPrint ? 'bg-pos-ok-soft text-pos-ok-text' : 'bg-pos-key text-pos-muted'">{{ 'pos.printers.form.autoPrint' | t }}: {{ (p.autoPrint ? 'pos.printers.on' : 'pos.printers.off') | t }}</span>
            <span class="pos-pill" [class]="p.openDrawerOnCash ? 'bg-pos-ok-soft text-pos-ok-text' : 'bg-pos-key text-pos-muted'">{{ 'pos.printers.form.drawer' | t }}: {{ (p.openDrawerOnCash ? 'pos.printers.on' : 'pos.printers.off') | t }}</span>
            <span class="pos-pill" [class]="p.cutPaper ? 'bg-pos-ok-soft text-pos-ok-text' : 'bg-pos-key text-pos-muted'">{{ 'pos.printers.form.cut' | t }}: {{ (p.cutPaper ? 'pos.printers.on' : 'pos.printers.off') | t }}</span>
            @if (isUsbProfile(p)) {
              <span class="pos-pill" [class]="paired()[p.id] ? 'bg-pos-ok-soft text-pos-ok-text' : 'bg-pos-warn-soft text-pos-ink'" data-testid="pos-printer-paired">
                {{ (paired()[p.id] ? 'pos.printers.paired' : 'pos.printers.notPaired') | t }}
              </span>
            }
          </p>
          @if (st) {
            <p class="mt-3 rounded-[10px] px-3 py-2 text-sm" [class]="st.tone === 'ok' ? 'bg-pos-ok-soft text-pos-ok-text' : 'bg-pos-danger-soft text-pos-ink'" role="status" data-testid="pos-printer-status">
              {{ st.text }}
            </p>
          }
          <div class="mt-auto flex flex-wrap gap-2 pt-3">
            <button type="button" class="pos-soft pos-soft-accent" [disabled]="busyId() === p.id" [attr.aria-busy]="busyId() === p.id || null" (click)="test(p)" data-testid="pos-printer-test">
              {{ 'pos.printers.test' | t }}
            </button>
            @if (isUsbProfile(p)) {
              <button type="button" class="pos-soft" (click)="pairExisting(p)" data-testid="pos-printer-pair">{{ 'pos.printers.pairHere' | t }}</button>
            }
            @if (canManage()) {
              <button type="button" class="pos-soft" (click)="openEdit(p)" data-testid="pos-printer-edit">{{ 'common.edit' | t }}</button>
              @if (!p.isDefault) {
                <button type="button" class="pos-soft" (click)="makeDefault(p)" data-testid="pos-printer-make-default">{{ 'pos.printers.makeDefault' | t }}</button>
              }
              <button type="button" class="pos-soft text-pos-danger" (click)="deleting.set(p)" data-testid="pos-printer-delete">{{ 'pos.printers.delete' | t }}</button>
            }
          </div>
        </li>
      }
    </ul>

    <hn-dialog [title]="(editing() ? 'pos.printers.editTitle' : 'pos.printers.newTitle') | t" [open]="formOpen()" size="lg" testId="pos-printer-dialog" (closed)="closeForm()">
      @if (formOpen()) {
        <hn-banner class="mb-3" [text]="formError()" />
        <form class="grid gap-3 sm:grid-cols-2" (ngSubmit)="save()" novalidate>
          <hn-field class="sm:col-span-2" [label]="'common.name' | t" for="pf-name" [required]="true" [errors]="err('name')">
            <input hnInput id="pf-name" name="name" autocomplete="off" [maxlength]="limits.name" [(ngModel)]="form.name" data-testid="pf-name" />
          </hn-field>
          <hn-field class="sm:col-span-2" [label]="'pos.printers.form.connection' | t" for="pf-connection">
            <select hnSelect id="pf-connection" name="connection" class="min-h-11" [(ngModel)]="form.connection" data-testid="pf-connection">
              @for (c of connections; track c) {
                <option [value]="c">{{ 'pos.printers.connection.' + c | t }}</option>
              }
            </select>
          </hn-field>
          <p class="rounded-[10px] bg-pos-surface px-3 py-2 text-sm text-pos-muted sm:col-span-2" data-testid="pf-help">{{ 'pos.printers.help.' + form.connection | t }}</p>

          @if (network()) {
            <hn-field [label]="'pos.printers.form.host' | t" for="pf-host" [required]="true" [hint]="'pos.printers.form.hostHint' | t" [errors]="err('host')">
              <input hnInput id="pf-host" name="host" dir="ltr" autocomplete="off" inputmode="url" [maxlength]="limits.host" [(ngModel)]="form.host" aria-describedby="pf-host-hint" data-testid="pf-host" />
            </hn-field>
            <hn-field [label]="'pos.printers.form.port' | t" for="pf-port" [hint]="(form.connection === 'EscPosBridge' ? 'pos.printers.form.portRawHint' : 'pos.printers.form.portHint') | t" [errors]="err('port')">
              <input hnInput id="pf-port" name="port" dir="ltr" inputmode="numeric" autocomplete="off" class="w-32" [(ngModel)]="form.port" aria-describedby="pf-port-hint" data-testid="pf-port" />
            </hn-field>
          }
          @if (form.connection === 'EpsonEpos' || form.connection === 'StarWebPrnt') {
            <label class="flex min-h-11 items-start gap-2 text-sm sm:col-span-2">
              <input type="checkbox" class="hn-checkbox mt-1" name="https" [(ngModel)]="form.useHttps" data-testid="pf-https" />
              <span><span class="font-medium">{{ 'pos.printers.form.https' | t }}</span><span class="block text-xs text-pos-muted">{{ 'pos.printers.form.httpsHint' | t }}</span></span>
            </label>
          }
          @if (form.connection === 'EpsonEpos') {
            <hn-field [label]="'pos.printers.form.deviceId' | t" for="pf-devid" [hint]="'pos.printers.form.deviceIdHint' | t" [errors]="err('eposDeviceId')">
              <input hnInput id="pf-devid" name="devid" dir="ltr" autocomplete="off" [maxlength]="limits.deviceId" [(ngModel)]="form.eposDeviceId" aria-describedby="pf-devid-hint" />
            </hn-field>
          }
          @if (form.connection === 'EscPosBridge') {
            <hn-field class="sm:col-span-2" [label]="'pos.printers.form.bridgeUrl' | t" for="pf-bridge" [required]="true" [hint]="'pos.printers.form.bridgeUrlHint' | t" [errors]="err('bridgeUrl')">
              <input hnInput id="pf-bridge" name="bridge" dir="ltr" autocomplete="off" inputmode="url" [maxlength]="limits.bridgeUrl" [(ngModel)]="form.bridgeUrl" aria-describedby="pf-bridge-hint" data-testid="pf-bridge" />
            </hn-field>
          }
          @if (usb()) {
            <div class="flex flex-wrap items-end gap-3 sm:col-span-2">
              <hn-field [label]="'pos.printers.form.vendorId' | t" for="pf-vendor" [errors]="err('usbVendorId')">
                <input hnInput id="pf-vendor" name="vendor" dir="ltr" autocomplete="off" class="w-28 font-mono" maxlength="6" [(ngModel)]="form.usbVendorId" />
              </hn-field>
              <hn-field [label]="'pos.printers.form.productId' | t" for="pf-product">
                <input hnInput id="pf-product" name="product" dir="ltr" autocomplete="off" class="w-28 font-mono" maxlength="6" [(ngModel)]="form.usbProductId" />
              </hn-field>
              <button type="button" class="pos-soft" (click)="pairInForm()" data-testid="pf-pair">{{ 'pos.printers.form.pair' | t }}</button>
            </div>
            @if (form.connection === 'WebSerial') {
              <hn-field [label]="'pos.printers.form.baud' | t" for="pf-baud">
                <select hnSelect id="pf-baud" name="baud" class="min-h-11" [(ngModel)]="form.baudRate">
                  @for (b of baudRates; track b) {
                    <option [ngValue]="b">{{ b }}</option>
                  }
                </select>
              </hn-field>
            }
          }

          <hn-field [label]="'pos.printers.form.paper' | t" for="pf-paper">
            <select hnSelect id="pf-paper" name="paper" class="min-h-11" [ngModel]="form.paperWidthMm" (ngModelChange)="setPaper($event)" data-testid="pf-paper">
              <option [ngValue]="80">80 mm</option>
              <option [ngValue]="58">58 mm</option>
            </select>
          </hn-field>
          <hn-field [label]="'pos.printers.form.chars' | t" for="pf-chars" [hint]="'pos.printers.form.charsHint' | t" [errors]="err('charsPerLine')">
            <input hnInput id="pf-chars" name="chars" inputmode="numeric" autocomplete="off" class="w-24 text-end" [(ngModel)]="form.charsPerLine" aria-describedby="pf-chars-hint" data-testid="pf-chars" />
          </hn-field>
          <hn-field [label]="'pos.printers.form.copies' | t" for="pf-copies" [errors]="err('copies')">
            <input hnInput id="pf-copies" name="copies" inputmode="numeric" autocomplete="off" class="w-24 text-end" [(ngModel)]="form.copies" data-testid="pf-copies" />
          </hn-field>
          @if (form.connection !== 'Browser') {
            <hn-field [label]="'pos.printers.form.codepage' | t" for="pf-codepage" [hint]="'pos.printers.form.codepageHint' | t">
              <select hnSelect id="pf-codepage" name="codepage" class="min-h-11" [(ngModel)]="form.codepage" aria-describedby="pf-codepage-hint">
                <option value="Cp858">CP858 (€, ÄÖÜ)</option>
                <option value="Pc437">PC437 (ÄÖÜ)</option>
              </select>
            </hn-field>
          }
          <fieldset class="grid gap-1 sm:col-span-2 sm:grid-cols-2">
            <legend class="sr-only">{{ 'pos.printers.form.options' | t }}</legend>
            <label class="flex min-h-11 items-start gap-2 text-sm">
              <input type="checkbox" class="hn-checkbox mt-1" name="auto" [(ngModel)]="form.autoPrint" data-testid="pf-auto" />
              <span><span class="font-medium">{{ 'pos.printers.form.autoPrint' | t }}</span><span class="block text-xs text-pos-muted">{{ 'pos.printers.form.autoPrintHint' | t }}</span></span>
            </label>
            <label class="flex min-h-11 items-start gap-2 text-sm">
              <input type="checkbox" class="hn-checkbox mt-1" name="drawer" [(ngModel)]="form.openDrawerOnCash" data-testid="pf-drawer" />
              <span><span class="font-medium">{{ 'pos.printers.form.drawer' | t }}</span><span class="block text-xs text-pos-muted">{{ 'pos.printers.form.drawerHint' | t }}</span></span>
            </label>
            <label class="flex min-h-11 items-start gap-2 text-sm">
              <input type="checkbox" class="hn-checkbox mt-1" name="cut" [(ngModel)]="form.cutPaper" />
              <span class="font-medium">{{ 'pos.printers.form.cut' | t }}</span>
            </label>
            <label class="flex min-h-11 items-start gap-2 text-sm">
              <input type="checkbox" class="hn-checkbox mt-1" name="qr" [(ngModel)]="form.printQr" />
              <span class="font-medium">{{ 'pos.printers.form.qr' | t }}</span>
            </label>
            @if (!editing()?.isDefault) {
              <label class="flex min-h-11 items-start gap-2 text-sm">
                <input type="checkbox" class="hn-checkbox mt-1" name="isDefault" [(ngModel)]="form.isDefault" data-testid="pf-default" />
                <span class="font-medium">{{ 'pos.printers.form.isDefault' | t }}</span>
              </label>
            }
          </fieldset>
          <div class="flex justify-end gap-2 sm:col-span-2">
            <button hnButton class="min-h-11" type="button" (click)="closeForm()">{{ 'common.cancel' | t }}</button>
            <button hnButton class="min-h-11" variant="primary" type="submit" [loading]="saving()" [disabled]="saving()" data-testid="pf-save">{{ 'common.save' | t }}</button>
          </div>
        </form>
      }
    </hn-dialog>

    <hn-dialog [title]="'pos.printers.deleteTitle' | t" [open]="!!deleting()" (closed)="deleting.set(null)">
      @if (deleting(); as p) {
        <p class="text-sm">{{ 'pos.printers.deleteText' | t: { name: p.name } }}</p>
        <div class="mt-4 flex justify-end gap-2">
          <button hnButton class="min-h-11" type="button" (click)="deleting.set(null)">{{ 'common.cancel' | t }}</button>
          <button hnButton class="min-h-11" variant="danger" type="button" [loading]="saving()" [disabled]="saving()" (click)="remove(p)" data-testid="pos-printer-delete-confirm">{{ 'pos.printers.delete' | t }}</button>
        </div>
      }
    </hn-dialog>
  `,
})
export class PosPrintersPage {
  readonly registerId = input.required<string>();

  private readonly api = inject(PrinterApi);
  private readonly printer = inject(PrinterService);
  private readonly session = inject(SessionStore);
  private readonly i18n = inject(I18nService);
  private readonly toast = inject(ToastService);

  protected readonly limits = PRINTER_LIMITS;
  protected readonly connections = PRINTER_CONNECTIONS;
  protected readonly baudRates = BAUD_RATES;
  protected readonly browserChoice = DEVICE_BROWSER;
  protected readonly usbSupported = webUsbSupported();
  protected readonly serialSupported = webSerialSupported();
  protected readonly canManage = computed(() => this.session.can('pos.manage'));

  protected readonly profiles = signal<PrinterProfile[]>([]);
  protected readonly loaded = signal(false);
  protected readonly loadError = signal<string | null>(null);
  protected readonly choice = signal('');
  protected readonly effective = computed(() => effectiveProfile(this.profiles(), this.choice()));
  protected readonly defaultName = computed(() => this.profiles().find((p) => p.isDefault)?.name ?? this.i18n.t('pos.printers.device.browser'));
  protected readonly status = signal<Record<string, Status>>({});
  protected readonly paired = signal<Record<string, boolean>>({});
  protected readonly busyId = signal<string | null>(null);

  protected readonly formOpen = signal(false);
  protected readonly editing = signal<PrinterProfile | null>(null);
  protected form: PrinterForm = emptyPrinterForm();
  protected readonly formErrors = signal<PrinterFormErrors>({});
  protected readonly formError = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly deleting = signal<PrinterProfile | null>(null);

  ngOnInit(): void {
    this.choice.set(this.printer.deviceChoice(this.registerId()));
    void this.load();
  }

  protected network(): boolean {
    return isNetwork(this.form.connection);
  }

  protected usb(): boolean {
    return isUsb(this.form.connection);
  }

  protected isUsbProfile(p: PrinterProfile): boolean {
    return isUsb(p.connection);
  }

  protected summary(p: PrinterProfile): string {
    return connectionSummary(p);
  }

  protected err(field: keyof PrinterForm): string | null {
    const key = this.formErrors()[field];
    return key ? this.i18n.t(key) : null;
  }

  private async load(): Promise<void> {
    this.loadError.set(null);
    try {
      const list = await firstValueFrom(this.api.list(this.registerId()));
      this.profiles.set(list);
      this.printer.invalidate(this.registerId());
      const paired: Record<string, boolean> = {};
      for (const p of list.filter((x) => isUsb(x.connection))) paired[p.id] = await hasPairedDevice(p);
      this.paired.set(paired);
    } catch (e) {
      this.loadError.set(posErrorText(this.i18n, e));
    } finally {
      this.loaded.set(true);
    }
  }

  protected setChoice(value: string): void {
    this.choice.set(value);
    this.printer.setDeviceChoice(this.registerId(), value);
    this.toast.success(this.i18n.t('pos.printers.device.saved'));
  }

  protected async test(p: PrinterProfile): Promise<void> {
    this.busyId.set(p.id);
    try {
      const r = await this.printer.testPrint(p);
      const text = r.ok
        ? this.i18n.t(r.via === 'Browser' ? 'pos.printers.testBrowser' : 'pos.printers.testSent', { name: p.name })
        : `${this.printer.errorText(r.code, r.detail)} ${this.i18n.t('pos.printers.testFallback')}`;
      this.status.update((s) => ({ ...s, [p.id]: { tone: r.ok ? 'ok' : 'danger', text, fallback: !r.ok } }));
    } finally {
      this.busyId.set(null);
    }
  }

  protected async pairExisting(p: PrinterProfile): Promise<void> {
    try {
      const ids = await this.printer.pair(p.connection);
      const fits = (p.usbVendorId === null || p.usbVendorId === ids.usbVendorId) && (p.usbProductId === null || p.usbProductId === ids.usbProductId);
      this.paired.update((m) => ({ ...m, [p.id]: fits }));
      this.status.update((s) => ({
        ...s,
        [p.id]: fits
          ? { tone: 'ok', text: this.i18n.t('pos.printers.pairedOk'), fallback: false }
          : { tone: 'danger', text: this.i18n.t('pos.printers.pairedOther', { ids: `${hexId(ids.usbVendorId)}:${hexId(ids.usbProductId)}` }), fallback: false },
      }));
    } catch (e) {
      this.status.update((s) => ({ ...s, [p.id]: { tone: 'danger', text: this.pairErrorText(e), fallback: false } }));
    }
  }

  protected async pairInForm(): Promise<void> {
    try {
      const ids = await this.printer.pair(this.form.connection);
      this.form.usbVendorId = hexId(ids.usbVendorId);
      this.form.usbProductId = hexId(ids.usbProductId);
      if (!this.form.name.trim() && ids.label) this.form.name = ids.label.slice(0, PRINTER_LIMITS.name);
      this.formError.set(null);
    } catch (e) {
      this.formError.set(this.pairErrorText(e));
    }
  }

  private pairErrorText(e: unknown): string {
    return e instanceof PrintError ? this.printer.errorText(e.code, e.detail) : this.printer.errorText('unsupported');
  }

  protected setPaper(width: number): void {
    // Keep a custom value; replace the typical value of the other width.
    if (this.form.charsPerLine === String(defaultChars(this.form.paperWidthMm))) this.form.charsPerLine = String(defaultChars(width));
    this.form.paperWidthMm = width;
  }

  protected openCreate(): void {
    this.form = emptyPrinterForm();
    this.form.isDefault = this.profiles().length === 0;
    this.editing.set(null);
    this.formErrors.set({});
    this.formError.set(null);
    this.formOpen.set(true);
  }

  protected openEdit(p: PrinterProfile): void {
    this.form = formFromProfile(p);
    this.editing.set(p);
    this.formErrors.set({});
    this.formError.set(null);
    this.formOpen.set(true);
  }

  protected closeForm(): void {
    if (!this.saving()) this.formOpen.set(false);
  }

  protected async save(): Promise<void> {
    const editing = this.editing();
    const { input, errors } = printerInput(this.form, editing?.version ?? null);
    this.formErrors.set(errors);
    if (!input) return;
    this.saving.set(true);
    this.formError.set(null);
    try {
      await firstValueFrom(editing ? this.api.update(editing.id, input) : this.api.create(this.registerId(), input));
      this.toast.success(this.i18n.t('pos.printers.saved', { name: input.name }));
      this.formOpen.set(false);
      await this.load();
    } catch (e) {
      this.formError.set(posErrorText(this.i18n, e));
    } finally {
      this.saving.set(false);
    }
  }

  protected async makeDefault(p: PrinterProfile): Promise<void> {
    try {
      await firstValueFrom(this.api.setDefault(p.id));
      this.toast.success(this.i18n.t('pos.printers.defaultSet', { name: p.name }));
      await this.load();
    } catch (e) {
      this.toast.error(posErrorText(this.i18n, e));
    }
  }

  protected async remove(p: PrinterProfile): Promise<void> {
    this.saving.set(true);
    try {
      await firstValueFrom(this.api.delete(p.id));
      if (this.choice() === p.id) this.setChoice('');
      this.toast.success(this.i18n.t('pos.printers.deleted', { name: p.name }));
      this.deleting.set(null);
      await this.load();
    } catch (e) {
      this.toast.error(posErrorText(this.i18n, e));
    } finally {
      this.saving.set(false);
    }
  }
}
