import { ChangeDetectionStrategy, Component, DOCUMENT, computed, effect, inject, input, signal, untracked, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom, of } from 'rxjs';
import { fieldErrors, toApiProblem } from '../../core/api/api-problem';
import { AnkaufApi, InventoryApi } from '../../core/api/handynet-api';
import {
  CheckIn,
  DEVICE_CONDITIONS,
  DEVICE_DEFECTS,
  ID_DOCUMENT_TYPES,
  INTAKE_PAYMENT_METHODS,
  Intake,
  IntakePaymentMethod,
  SellerSuggestion,
} from '../../core/api/models';
import { SessionStore } from '../../core/auth/session.store';
import { minorToDecimal, minorToInput } from '../../core/format/money';
import { I18nService } from '../../core/i18n/i18n.service';
import { ToastService, describeProblem } from '../../core/ui-state/toast.service';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';
import { PosSubnavComponent } from '../pos/pos-subnav';
import { ankaufLinks } from './ankauf-links';
import { ankaufProblemText, modelLabel } from './ankauf.logic';
import { BelegPrintComponent, OWNERSHIP_DECLARATION } from './beleg-print';
import { CheckInPanelComponent } from './checkin-panel';
import {
  INTAKE_STEPS,
  InspectForm,
  NOTES_MAX_LENGTH,
  REASON_MAX_LENGTH,
  SELLER_MAX_LENGTH,
  SellerField,
  SellerForm,
  applyMrz,
  applySellerDetails,
  applySuggestion,
  defaultPaidMinor,
  defaultPayoutRegister,
  emptySellerForm,
  inspectFormFrom,
  intakeActions,
  isQuoteUsable,
  latestAdultBirthDate,
  matchesSignature,
  offerDifferenceMinor,
  signatureState,
  stepStates,
  today,
  validateInspectForm,
  validatePaidAmount,
  validatePayout,
  validateReason,
  validateReceiveForm,
} from './intake.logic';
import { MrzCapture, MrzInputComponent } from './mrz-input';
import { SellerCardComponent } from './seller-card';
import { SellerSearchComponent } from './seller-search';
import { SignaturePadComponent } from './signature-pad';

type Action = 'receive' | 'inspect' | 'sign' | 'complete' | 'reject' | 'beleg';
type CaptureTab = 'mrz' | 'checkin' | 'returning' | 'manual';

/** API field path for each seller form field (422 `errors` keys, case-insensitive). */
const SELLER_API_FIELDS: Record<SellerField, string> = {
  firstName: 'seller.firstName',
  lastName: 'seller.lastName',
  street: 'seller.street',
  postalCode: 'seller.postalCode',
  city: 'seller.city',
  birthDate: 'seller.birthDate',
  idDocumentType: 'seller.idDocumentType',
  idDocumentNumber: 'seller.idDocumentNumber',
  idExpiryDate: 'seller.idExpiryDate',
  nationality: 'seller.nationality',
  idIssuingAuthority: 'seller.idIssuingAuthority',
  idIssuingCountry: 'seller.idIssuingCountry',
  country: 'seller.country',
  imei: 'imei',
};

/** API codes (without the `ankauf.` prefix) after which the shown intake is probably stale → reload it. */
const RELOAD_CODES = new Set(['intake_closed', 'not_inspected', 'concurrency', 'signature_invalid', 'signature_required']);

/**
 * Ankauf device intake as a wizard: 1 seller (ID scan/MRZ, QR self check-in, returning seller or manual) → 2 device
 * inspection (recalculated offer) → 3 price + the seller's signature → 4 payout (cash from the till or transfer) and
 * stock → Ankaufbeleg. Routes `…/ankaeufe/neu/:quoteId` (new) and `…/ankaeufe/:id`, in the back office and in the POS app.
 */
@Component({
  selector: 'hn-intake-page',
  imports: [
    FormsModule,
    RouterLink,
    ...UI,
    PosSubnavComponent,
    MrzInputComponent,
    CheckInPanelComponent,
    SellerSearchComponent,
    SellerCardComponent,
    SignaturePadComponent,
    BelegPrintComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[class]': 'links.registerId ? "pos-page block" : "block"' },
  templateUrl: './intake.page.html',
})
export class IntakePage {
  private readonly api = inject(AnkaufApi);
  private readonly inventoryApi = inject(InventoryApi);
  private readonly i18n = inject(I18nService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);
  private readonly document = inject(DOCUMENT);
  protected readonly session = inject(SessionStore);
  protected readonly links = ankaufLinks(this.router.url);

  /** Route parameters (component input binding); exactly one is set. */
  readonly id = input<string | undefined>();
  readonly quoteId = input<string | undefined>();

  protected readonly conditions = DEVICE_CONDITIONS;
  protected readonly defects = DEVICE_DEFECTS;
  protected readonly idTypes = ID_DOCUMENT_TYPES;
  protected readonly paymentMethods = INTAKE_PAYMENT_METHODS;
  protected readonly steps = INTAKE_STEPS;
  protected readonly declaration = OWNERSHIP_DECLARATION;
  protected readonly max = { ...SELLER_MAX_LENGTH, notes: NOTES_MAX_LENGTH, reason: REASON_MAX_LENGTH };
  protected readonly label = modelLabel;
  protected readonly hasInventory = computed(() => this.session.hasModule('Inventory'));
  protected readonly canSeeStock = computed(() => this.session.allows('Inventory'));

  // ---- data
  protected readonly intakeQuery = createQuery(
    () => this.id() ?? null,
    (id) => (id ? this.api.intake(id) : of(null)),
  );
  protected readonly intake = computed(() => this.intakeQuery.value());
  protected readonly quotes = createQuery(
    () => true,
    () => this.api.quotes(),
  );
  protected readonly quote = computed(() => {
    const id = this.quoteId() ?? this.intake()?.quoteId;
    return id ? (this.quotes.value()?.find((q) => q.id === id) ?? null) : null;
  });
  protected readonly quoteUsable = computed(() => {
    const q = this.quote();
    return !!q && isQuoteUsable(q);
  });
  protected readonly warehouses = createQuery(
    () => this.hasInventory(),
    (enabled) => (enabled ? this.inventoryApi.warehouses() : of([])),
  );
  /** Tills for cash payouts (loaded once the intake can be paid). */
  protected readonly tills = createQuery(
    () => (this.intake()?.status === 'Inspected' ? this.intake()!.id : null),
    (id) => (id ? this.api.payoutRegisters() : of(null)),
  );

  protected readonly isNew = computed(() => !this.id());
  protected readonly stepState = computed(() => stepStates(this.isNew() ? null : this.intake()));
  protected readonly actions = computed(() => {
    const i = this.intake();
    return i ? intakeActions(i.status) : { inspect: false, sign: false, complete: false, reject: false };
  });
  protected readonly difference = computed(() => {
    const i = this.intake();
    return i ? offerDifferenceMinor(i.quotedOfferMinor, i.inspectedOfferMinor) : null;
  });
  protected readonly sigState = computed(() => {
    const i = this.intake();
    return i ? signatureState(i) : 'none';
  });
  protected readonly tillRequired = computed(() => !!this.tills.value()?.tillRequired);
  protected readonly registers = computed(() => this.tills.value()?.registers ?? []);

  // ---- forms
  protected seller: SellerForm = emptySellerForm();
  protected readonly sellerErrors = signal<Partial<Record<SellerField, string>>>({});
  protected readonly birthDateMax = latestAdultBirthDate(today());
  protected readonly todayIso = isoToday();
  protected readonly captureTab = signal<CaptureTab>('mrz');
  /** Where the seller data came from (sent with the intake; the API verifies the MRZ again). */
  protected readonly mrzRaw = signal<string | null>(null);
  protected readonly checkInId = signal<string | null>(null);
  protected readonly captureSource = signal<'Mrz' | 'SelfCheckIn' | 'Returning' | null>(null);

  protected inspect: InspectForm = { condition: null, defects: {}, batteryHealth: '', notes: '' };
  protected readonly inspectErrors = signal<ReturnType<typeof validateInspectForm>['errors']>({});
  protected paid = { amount: '', method: 'Cash' as IntakePaymentMethod, warehouseId: '', registerId: '' };
  protected readonly paidError = signal<string | null>(null);
  protected readonly payoutError = signal<string | null>(null);
  protected readonly signatureImage = signal<string | null>(null);
  protected readonly signatureError = signal<string | null>(null);
  protected reason = '';
  protected readonly reasonError = signal<string | null>(null);
  private readonly pad = viewChild(SignaturePadComponent);

  protected readonly busy = signal<Action | null>(null);
  protected readonly dialog = signal<'complete' | 'reject' | null>(null);
  protected readonly showBeleg = signal(false);
  private readonly error = signal<{ action: Action; error: unknown } | null>(null);
  /** Pending complete request shown in the confirmation dialog. */
  protected readonly pendingPayment = signal<{ minor: number; method: IntakePaymentMethod; warehouseId: string | null; registerId: string | null } | null>(null);

  protected readonly imeiInStock = computed(() => toApiProblem(this.error()?.error ?? null).code === 'unit.imei_in_stock');

  constructor() {
    // Prefill the seller IMEI from the quote once it is known.
    effect(() => {
      const q = this.quote();
      if (q?.imei && this.isNew()) untracked(() => (this.seller.imei ||= q.imei!));
    });
    // Prefill the inspect/sign forms whenever a (new version of the) intake arrives.
    effect(() => {
      const i = this.intake();
      const q = this.quote();
      if (!i) return;
      untracked(() => {
        this.inspect = inspectFormFrom(i, q);
        const signed = i.signature?.valid ? i.signature : null;
        const def = signed?.paidMinor ?? defaultPaidMinor(i);
        this.paid = {
          ...this.paid,
          amount: def === null ? '' : minorToInput(def),
          method: signed?.paymentMethod ?? this.paid.method,
        };
        this.signatureImage.set(null);
      });
    });
    // Preselect the till (the POS register, or the only one with an open session).
    effect(() => {
      const regs = this.registers();
      untracked(() => {
        if (!this.paid.registerId) this.paid = { ...this.paid, registerId: defaultPayoutRegister(regs, this.links.registerId) ?? '' };
      });
    });
  }

  protected errorFor(action: Action): string | null {
    const e = this.error();
    if (!e || e.action !== action) return null;
    const problem = toApiProblem(e.error);
    // Field errors are shown next to the fields; skip the generic validation banner then.
    if (problem.code === 'validation.failed' && Object.keys(problem.errors).length) return this.i18n.t('ankauf.intake.fixFields');
    return ankaufProblemText(problem, (k) => this.i18n.t(k), (k) => this.i18n.has(k), (p) => describeProblem(this.i18n, p));
  }

  protected sellerError(field: SellerField): string | null {
    const key = this.sellerErrors()[field];
    if (key) return this.i18n.t(key);
    const e = this.error();
    if (e?.action !== 'receive') return null;
    const api = fieldErrors(toApiProblem(e.error), SELLER_API_FIELDS[field]);
    return api.length ? api.join(' ') : null;
  }

  protected inspectError(field: 'condition' | 'battery' | 'notes'): string | null {
    const key = this.inspectErrors()[field];
    return key ? this.i18n.t(key) : null;
  }

  protected warehouseName(id: string | null): string {
    if (!id) return this.i18n.t('ankauf.intake.defaultWarehouse');
    const w = this.warehouses.value()?.find((x) => x.id === id);
    return w ? `${w.code} – ${w.name}` : id;
  }

  protected registerName(id: string | null): string {
    return this.registers().find((r) => r.id === id)?.name ?? '–';
  }

  // ---- seller capture
  protected onMrz(c: MrzCapture): void {
    this.seller = applyMrz(this.seller, c.data);
    this.mrzRaw.set(c.raw);
    this.checkInId.set(null);
    this.captureSource.set('Mrz');
    this.sellerErrors.set({});
    this.toast.success(this.i18n.t('ankauf.mrz.filled'));
  }

  protected onCheckIn(c: CheckIn): void {
    if (!c.seller) return;
    this.seller = applySellerDetails(this.seller, c.seller);
    this.checkInId.set(c.id);
    this.mrzRaw.set(null);
    this.captureSource.set('SelfCheckIn');
    this.sellerErrors.set({});
    this.captureTab.set('manual');
    this.toast.success(this.i18n.t('ankauf.checkin.taken'));
  }

  protected onReturning(s: SellerSuggestion): void {
    this.seller = applySuggestion(this.seller, s);
    this.captureSource.set('Returning');
    this.mrzRaw.set(null);
    this.sellerErrors.set({});
    this.captureTab.set('mrz');
    this.toast.success(this.i18n.t('ankauf.returning.taken'));
  }

  /** Editing a document field after the MRZ scan: the API compares again and refuses a mismatch, so drop the MRZ link. */
  protected documentEdited(): void {
    if (this.mrzRaw()) {
      this.mrzRaw.set(null);
      if (this.captureSource() === 'Mrz') this.captureSource.set(null);
    }
  }

  // ---- receive
  protected async receive(): Promise<void> {
    const quote = this.quote();
    if (!quote) return;
    const v = validateReceiveForm(quote.id, quote.imei, this.seller, today(), { mrz: this.mrzRaw(), checkInId: this.checkInId() });
    this.sellerErrors.set(v.errors);
    this.error.set(null);
    if (!v.request) {
      focusFirstInvalid();
      return;
    }
    await this.run('receive', async () => {
      const created = await firstValueFrom(this.api.receiveIntake(v.request!));
      this.toast.success(this.i18n.t('ankauf.intake.received'));
      await this.router.navigate(this.links.intake(created.id), { replaceUrl: true });
    });
  }

  // ---- inspect
  protected async saveInspection(i: Intake): Promise<void> {
    const v = validateInspectForm(this.inspect);
    this.inspectErrors.set(v.errors);
    this.error.set(null);
    if (!v.request) return;
    await this.run('inspect', async () => {
      this.intakeQuery.set(await firstValueFrom(this.api.inspectIntake(i.id, v.request!)));
      this.toast.success(this.i18n.t('ankauf.intake.inspected'));
    });
  }

  // ---- sign (amount + method + signature, bound together)
  protected async sign(i: Intake): Promise<void> {
    const check = validatePaidAmount(this.paid.amount, i.inspectedOfferMinor);
    this.paidError.set(check.error);
    this.signatureError.set(null);
    this.error.set(null);
    if (check.error || check.minor === null) return;
    const image = this.signatureImage();
    if (!image) {
      this.signatureError.set('ankauf.signature.missing');
      return;
    }
    await this.run('sign', async () => {
      const signed = await firstValueFrom(
        this.api.signIntake(i.id, { paidAmount: minorToDecimal(check.minor!), paymentMethod: this.paid.method, image }),
      );
      this.intakeQuery.set(signed);
      this.pad()?.clear();
      this.toast.success(this.i18n.t('ankauf.signature.saved'));
    });
  }

  /** Signing again (amount/method changed, or the data changed after signing). */
  protected resign(): void {
    this.signatureImage.set(null);
    this.pad()?.clear();
    this.intakeQuery.set({ ...this.intake()!, signature: null });
  }

  // ---- complete
  protected askComplete(i: Intake): void {
    this.error.set(null);
    this.payoutError.set(null);
    const signed = i.signature?.valid ? i.signature : null;
    const minor = signed?.paidMinor ?? validatePaidAmount(this.paid.amount, i.inspectedOfferMinor).minor;
    const method = signed?.paymentMethod ?? this.paid.method;
    if (minor === null || minor === undefined) return;
    if (i.signatureRequired !== false && !matchesSignature(i, minor, method)) {
      this.payoutError.set('ankauf.pay.signFirst');
      return;
    }
    const registerId = this.paid.registerId || null;
    const till = validatePayout(method, this.tillRequired(), registerId, this.registers(), minor);
    if (till) {
      this.payoutError.set(till);
      return;
    }
    this.pendingPayment.set({ minor, method, warehouseId: this.paid.warehouseId || null, registerId: method === 'Cash' && this.tillRequired() ? registerId : null });
    this.dialog.set('complete');
  }

  protected async complete(i: Intake): Promise<void> {
    const p = this.pendingPayment();
    if (!p) return;
    await this.run('complete', async () => {
      const done = await firstValueFrom(
        // Decimal euros for the API; the amount was validated as integer cents.
        this.api.completeIntake(i.id, { paidAmount: minorToDecimal(p.minor), paymentMethod: p.method, warehouseId: p.warehouseId, registerId: p.registerId }),
      );
      this.intakeQuery.set(done);
      this.dialog.set(null);
      this.toast.success(this.i18n.t(p.registerId ? 'ankauf.pay.paidFromTill' : 'ankauf.intake.completed', { register: this.registerName(p.registerId) }));
    });
  }

  // ---- Ankaufbeleg (PDF, completed intakes; fetched with the auth headers as a blob) and print view
  protected async downloadBeleg(i: Intake): Promise<void> {
    await this.run('beleg', async () => {
      const blob = await firstValueFrom(this.api.beleg(i.id));
      const url = URL.createObjectURL(blob);
      const a = this.document.createElement('a');
      a.href = url;
      a.download = `ankaufbeleg-${i.imei}.pdf`;
      this.document.body.appendChild(a);
      a.click();
      a.remove();
      setTimeout(() => URL.revokeObjectURL(url), 60_000);
    });
  }

  protected printBeleg(): void {
    this.showBeleg.set(true);
    setTimeout(() => this.document.defaultView?.print(), 150);
  }

  // ---- reject
  protected openReject(): void {
    this.reason = '';
    this.reasonError.set(null);
    this.error.set(null);
    this.dialog.set('reject');
  }

  protected async reject(i: Intake): Promise<void> {
    const err = validateReason(this.reason);
    this.reasonError.set(err);
    if (err) return;
    await this.run('reject', async () => {
      this.intakeQuery.set(await firstValueFrom(this.api.rejectIntake(i.id, this.reason.trim())));
      this.dialog.set(null);
      this.toast.success(this.i18n.t('ankauf.intake.rejected'));
    });
  }

  protected closeDialog(): void {
    if (this.busy()) return;
    this.dialog.set(null);
  }

  private async run(action: Action, fn: () => Promise<void>): Promise<void> {
    this.busy.set(action);
    this.error.set(null);
    try {
      await fn();
    } catch (e) {
      this.error.set({ action, error: e });
      const code = toApiProblem(e).code;
      if (code && RELOAD_CODES.has(code.replace(/^ankauf\./, ''))) this.intakeQuery.reload();
      if (code?.startsWith('pos.')) this.tills.reload();
    } finally {
      this.busy.set(null);
    }
  }
}

function isoToday(): string {
  const t = today();
  return `${t.y}-${String(t.m).padStart(2, '0')}-${String(t.d).padStart(2, '0')}`;
}

function focusFirstInvalid(): void {
  if (typeof document === 'undefined') return;
  setTimeout(() => document.querySelector<HTMLElement>('[aria-invalid="true"]')?.focus());
}
