import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { debounceTime, distinctUntilChanged, firstValueFrom, of } from 'rxjs';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { fieldErrors, toApiProblem } from '../../core/api/api-problem';
import { AnkaufApi } from '../../core/api/handynet-api';
import { AnkaufModel, DEVICE_CONDITIONS, DEVICE_DEFECTS, Quote } from '../../core/api/models';
import { SessionStore } from '../../core/auth/session.store';
import { I18nService } from '../../core/i18n/i18n.service';
import { ToastService, describeProblem } from '../../core/ui-state/toast.service';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';
import { formatBp } from '../admin/ankauf/ankauf-admin.logic';
import { QuoteForm, ankaufProblemText, modelLabel, validateQuoteForm } from './ankauf.logic';
import { intakeByQuote, isQuoteUsable } from './intake.logic';
import { ankaufLinks } from './ankauf-links';
import { PosSubnavComponent } from '../pos/pos-subnav';

/** Dealer: preliminary buyback quotes for used devices (module Ankauf). */
@Component({
  selector: 'hn-ankauf-page',
  imports: [FormsModule, RouterLink, PosSubnavComponent, ...UI],
  host: { '[class]': 'links.registerId ? "pos-page block" : "block"' },
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './ankauf.page.html',
})
export class AnkaufPage {
  private readonly api = inject(AnkaufApi);
  private readonly i18n = inject(I18nService);
  private readonly toast = inject(ToastService);
  protected readonly session = inject(SessionStore);
  protected readonly links = ankaufLinks(inject(Router).url);

  protected readonly conditions = DEVICE_CONDITIONS;
  protected readonly defects = DEVICE_DEFECTS;
  protected readonly canQuote = computed(() => this.session.can('orders.place'));

  // ---- model search
  protected readonly searchText = signal('');
  private readonly debouncedSearch = toSignal(toObservable(this.searchText).pipe(debounceTime(250), distinctUntilChanged()), { initialValue: '' });
  protected readonly models = createQuery(
    () => this.debouncedSearch().trim(),
    (search) => this.api.models(search),
  );
  protected readonly selectedModel = signal<AnkaufModel | null>(null);

  // ---- quote form
  protected form: QuoteForm = this.emptyForm();
  protected readonly errors = signal<ReturnType<typeof validateQuoteForm>['errors']>({});
  protected readonly submitting = signal(false);
  private readonly submitError = signal<unknown>(null);
  protected readonly submitErrorText = computed(() => {
    const e = this.submitError();
    if (!e) return null;
    return ankaufProblemText(toApiProblem(e), (k) => this.i18n.t(k), (k) => this.i18n.has(k), (p) => describeProblem(this.i18n, p));
  });
  protected readonly result = signal<Quote | null>(null);

  protected readonly quotes = createQuery(
    () => true,
    () => this.api.quotes(),
  );

  /** Device intake ("Ankauf starten") needs inventory.manage, like the API. */
  protected readonly canIntake = computed(() => this.session.can('inventory.manage'));
  private readonly intakes = createQuery(
    () => this.canIntake(),
    (allowed) => (allowed ? this.api.intakes() : of([])),
  );
  /** Quote id → its running/finished intake (rejected intakes free the quote again). */
  protected readonly intakeFor = computed(() => intakeByQuote(this.intakes.value() ?? []));

  protected label = modelLabel;

  protected usable(q: Quote): boolean {
    return isQuoteUsable(q);
  }

  protected selectModel(m: AnkaufModel): void {
    this.selectedModel.set(m);
    this.form.modelKey = m.modelKey;
    this.errors.update(({ model: _, ...rest }) => rest);
  }

  protected clearModel(): void {
    this.selectedModel.set(null);
    this.form.modelKey = null;
  }

  protected fieldError(field: 'model' | 'condition' | 'battery' | 'consumerPrice' | 'imei', apiField: string): string | null {
    const key = this.errors()[field];
    if (key) return this.i18n.t(key);
    const api = fieldErrors(this.submitError() ? toApiProblem(this.submitError()) : null, apiField);
    return api.length ? api.join(' ') : null;
  }

  protected pct(bp: number, signed = true): string {
    return formatBp(bp, this.i18n.locale(), signed);
  }

  protected async submit(): Promise<void> {
    const v = validateQuoteForm(this.form);
    this.errors.set(v.errors);
    this.submitError.set(null);
    if (!v.request) return;
    this.submitting.set(true);
    try {
      const quote = await firstValueFrom(this.api.createQuote(v.request));
      this.result.set(quote);
      this.toast.success(this.i18n.t('ankauf.created'));
      this.quotes.reload();
    } catch (e) {
      this.submitError.set(e);
    } finally {
      this.submitting.set(false);
    }
  }

  protected newQuote(): void {
    this.result.set(null);
    this.form = this.emptyForm();
    this.selectedModel.set(null);
    this.errors.set({});
    this.submitError.set(null);
  }

  private emptyForm(): QuoteForm {
    return { modelKey: null, condition: null, defects: {}, batteryHealth: '', consumerPrice: '', imei: '' };
  }
}
