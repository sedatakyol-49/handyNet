import { ChangeDetectionStrategy, Component, DOCUMENT, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom, of } from 'rxjs';
import { fieldErrors, toApiProblem } from '../../core/api/api-problem';
import { AnkaufApi, InventoryApi } from '../../core/api/handynet-api';
import {
  DEVICE_CONDITIONS,
  DEVICE_DEFECTS,
  ID_DOCUMENT_TYPES,
  INTAKE_PAYMENT_METHODS,
  Intake,
  IntakePaymentMethod,
} from '../../core/api/models';
import { SessionStore } from '../../core/auth/session.store';
import { minorToDecimal, minorToInput } from '../../core/format/money';
import { I18nService } from '../../core/i18n/i18n.service';
import { ToastService, describeProblem } from '../../core/ui-state/toast.service';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';
import { ankaufProblemText, modelLabel } from './ankauf.logic';
import {
  INTAKE_STEPS,
  InspectForm,
  NOTES_MAX_LENGTH,
  REASON_MAX_LENGTH,
  SELLER_MAX_LENGTH,
  SellerField,
  SellerForm,
  defaultPaidMinor,
  inspectFormFrom,
  intakeActions,
  isQuoteUsable,
  latestAdultBirthDate,
  offerDifferenceMinor,
  stepStates,
  today,
  validateInspectForm,
  validatePaidAmount,
  validateReason,
  validateReceiveForm,
} from './intake.logic';

type Action = 'receive' | 'inspect' | 'complete' | 'reject' | 'beleg';

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
  imei: 'imei',
};

/** API codes (without the `ankauf.` prefix) after which the shown intake is probably stale → reload it. */
const RELOAD_CODES = new Set(['intake_closed', 'not_inspected', 'concurrency']);

/**
 * Ankauf device intake: receive (seller + IMEI) → inspect (recalculated offer) → complete (pay + stock) or reject.
 * Route `ankaeufe/neu/:quoteId` starts a new intake for a quote, `ankaeufe/:id` shows/continues an existing one.
 */
@Component({
  selector: 'hn-intake-page',
  imports: [FormsModule, RouterLink, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
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

  /** Route parameters (component input binding); exactly one is set. */
  readonly id = input<string | undefined>();
  readonly quoteId = input<string | undefined>();

  protected readonly conditions = DEVICE_CONDITIONS;
  protected readonly defects = DEVICE_DEFECTS;
  protected readonly idTypes = ID_DOCUMENT_TYPES;
  protected readonly paymentMethods = INTAKE_PAYMENT_METHODS;
  protected readonly steps = INTAKE_STEPS;
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

  protected readonly isNew = computed(() => !this.id());
  protected readonly stepState = computed(() => stepStates(this.isNew() ? null : this.intake()));
  protected readonly actions = computed(() => {
    const i = this.intake();
    return i ? intakeActions(i.status) : { inspect: false, complete: false, reject: false };
  });
  protected readonly difference = computed(() => {
    const i = this.intake();
    return i ? offerDifferenceMinor(i.quotedOfferMinor, i.inspectedOfferMinor) : null;
  });

  // ---- forms
  protected seller: SellerForm = this.emptySeller();
  protected readonly sellerErrors = signal<Partial<Record<SellerField, string>>>({});
  protected readonly birthDateMax = latestAdultBirthDate(today());
  protected inspect: InspectForm = { condition: null, defects: {}, batteryHealth: '', notes: '' };
  protected readonly inspectErrors = signal<ReturnType<typeof validateInspectForm>['errors']>({});
  protected paid = { amount: '', method: 'Cash' as IntakePaymentMethod, warehouseId: '' };
  protected readonly paidError = signal<string | null>(null);
  protected reason = '';
  protected readonly reasonError = signal<string | null>(null);

  protected readonly busy = signal<Action | null>(null);
  protected readonly dialog = signal<'complete' | 'reject' | null>(null);
  private readonly error = signal<{ action: Action; error: unknown } | null>(null);
  /** Pending complete request shown in the confirmation dialog. */
  protected readonly pendingPayment = signal<{ minor: number; method: IntakePaymentMethod; warehouseId: string | null } | null>(null);

  protected readonly imeiInStock = computed(() => toApiProblem(this.error()?.error ?? null).code === 'unit.imei_in_stock');

  constructor() {
    // Prefill the seller IMEI from the quote once it is known.
    effect(() => {
      const q = this.quote();
      if (q?.imei && this.isNew()) untracked(() => (this.seller.imei ||= q.imei!));
    });
    // Prefill the inspect/complete forms whenever a (new version of the) intake arrives.
    effect(() => {
      const i = this.intake();
      const q = this.quote();
      if (!i) return;
      untracked(() => {
        this.inspect = inspectFormFrom(i, q);
        const def = defaultPaidMinor(i);
        this.paid = { ...this.paid, amount: def === null ? '' : minorToInput(def) };
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

  // ---- receive
  protected async receive(): Promise<void> {
    const quote = this.quote();
    if (!quote) return;
    const v = validateReceiveForm(quote.id, quote.imei, this.seller, today());
    this.sellerErrors.set(v.errors);
    this.error.set(null);
    if (!v.request) {
      focusFirstInvalid();
      return;
    }
    await this.run('receive', async () => {
      const created = await firstValueFrom(this.api.receiveIntake(v.request!));
      this.toast.success(this.i18n.t('ankauf.intake.received'));
      await this.router.navigate(['/app/ankauf/ankaeufe', created.id], { replaceUrl: true });
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

  // ---- complete
  protected askComplete(i: Intake): void {
    const check = validatePaidAmount(this.paid.amount, i.inspectedOfferMinor);
    this.paidError.set(check.error);
    this.error.set(null);
    if (check.error || check.minor === null) return;
    this.pendingPayment.set({ minor: check.minor, method: this.paid.method, warehouseId: this.paid.warehouseId || null });
    this.dialog.set('complete');
  }

  protected async complete(i: Intake): Promise<void> {
    const p = this.pendingPayment();
    if (!p) return;
    await this.run('complete', async () => {
      const done = await firstValueFrom(
        // Decimal euros for the API; the amount was validated as integer cents.
        this.api.completeIntake(i.id, { paidAmount: minorToDecimal(p.minor), paymentMethod: p.method, warehouseId: p.warehouseId }),
      );
      this.intakeQuery.set(done);
      this.dialog.set(null);
      this.toast.success(this.i18n.t('ankauf.intake.completed'));
    });
  }

  // ---- Ankaufbeleg (PDF, completed intakes; fetched with the auth headers as a blob)
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
    } finally {
      this.busy.set(null);
    }
  }

  private emptySeller(): SellerForm {
    return {
      firstName: '',
      lastName: '',
      street: '',
      postalCode: '',
      city: '',
      birthDate: '',
      idDocumentType: null,
      idDocumentNumber: '',
      imei: '',
    };
  }
}

function focusFirstInvalid(): void {
  if (typeof document === 'undefined') return;
  setTimeout(() => document.querySelector<HTMLElement>('[aria-invalid="true"]')?.focus());
}
