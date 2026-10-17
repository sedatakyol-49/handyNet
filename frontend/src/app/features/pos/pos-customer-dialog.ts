import { ChangeDetectionStrategy, Component, DestroyRef, effect, inject, input, output, signal, untracked } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiProblem, fieldErrors, toApiProblem } from '../../core/api/api-problem';
import { PosApi } from '../../core/api/handynet-api';
import { PosCustomer } from '../../core/api/models';
import { I18nService } from '../../core/i18n/i18n.service';
import { ToastService } from '../../core/ui-state/toast.service';
import { UI } from '../../shared/ui';
import { CUSTOMER_LIMITS, CustomerField, CustomerForm, CustomerFormErrors, customerRequest, customerSummary, customerToForm, emptyCustomerForm, validateCustomer } from './customer-form';
import { posErrorText } from './pos-error';

const PAGE_SIZE = 30;

/**
 * Odoo-like customer picker of the till (`GET/POST/PUT /api/pos/customers`, no delete): server search, list with
 * "load more", create and edit form. `attach` mode (ticket) offers "remove customer"; `pick` mode (history filter)
 * only chooses.
 */
@Component({
  selector: 'hn-pos-customer-dialog',
  imports: [...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-dialog [title]="(view() === 'form' ? (editing() ? 'pos.customers.editTitle' : 'pos.customers.newTitle') : 'pos.customers.title') | t" [open]="open()" [wide]="true" (closed)="close()">
      @if (open()) {
        @if (view() === 'list') {
          <div class="flex flex-wrap gap-2" role="search">
            <label for="pos-cust-search" class="sr-only">{{ 'pos.customers.search' | t }}</label>
            <input hnInput id="pos-cust-search" type="search" class="h-12 min-w-0 flex-1 text-base" autocomplete="off" [placeholder]="'pos.customers.searchPlaceholder' | t"
              [value]="search()" (input)="search.set($any($event.target).value)" data-testid="pos-cust-search" />
            <button hnButton variant="primary" type="button" class="min-h-12" (click)="startCreate()" data-testid="pos-cust-new">{{ 'pos.customers.new' | t }}</button>
          </div>
          @if (listError()) {
            <hn-banner class="mt-3" [error]="listError()" />
          }
          <ul class="mt-3 max-h-[55vh] divide-y divide-slate-100 overflow-y-auto rounded-lg border border-slate-200" [attr.aria-busy]="loading() || null" data-testid="pos-cust-list">
            @for (c of items(); track c.id) {
              @let attached = c.id === attachedId();
              <li class="flex items-stretch" [class.bg-accent-50]="attached" [attr.data-customer]="c.name">
                <button type="button" class="min-h-14 min-w-0 flex-1 px-3 py-2 text-start hover:bg-slate-50" (click)="choose(c)" [attr.aria-pressed]="attached" data-testid="pos-cust-choose">
                  <span class="block font-semibold text-slate-900">{{ c.name }}</span>
                  <span class="block truncate text-xs text-slate-600">{{ summary(c) }}</span>
                  @if (c.email || c.phone) {
                    <span class="block truncate text-xs text-slate-500">{{ c.email }}{{ c.email && c.phone ? ' · ' : '' }}{{ c.phone }}</span>
                  }
                </button>
                <button type="button" class="grid w-14 shrink-0 place-items-center text-slate-500 hover:bg-slate-50 hover:text-accent-800" (click)="startEdit(c)"
                  [attr.aria-label]="'pos.customers.editFor' | t: { name: c.name }" data-testid="pos-cust-edit">
                  <svg class="size-5" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" aria-hidden="true"><path d="M4 20h4L19 9l-4-4L4 16zM13.5 6.5l4 4" /></svg>
                </button>
              </li>
            } @empty {
              <li class="px-3 py-8 text-center text-sm text-slate-500" data-testid="pos-cust-empty">
                {{ (loading() ? 'common.loading' : appliedSearch() ? 'pos.customers.noMatch' : 'pos.customers.empty') | t }}
              </li>
            }
          </ul>
          <div class="mt-3 flex flex-wrap items-center gap-2">
            @if (items().length < total()) {
              <button hnButton type="button" class="min-h-11" [loading]="loading()" (click)="loadMore()">{{ 'pos.sell.loadMore' | t: { shown: items().length, total: total() } }}</button>
            }
            @if (mode() === 'attach' && attachedId()) {
              <button hnButton variant="ghost" type="button" class="ms-auto min-h-11" (click)="detach.emit()" data-testid="pos-cust-detach">{{ 'pos.customers.detach' | t }}</button>
            }
          </div>
        } @else {
          <form class="grid gap-3 sm:grid-cols-2" (submit)="$event.preventDefault(); save()" novalidate data-testid="pos-cust-form">
            @if (formProblem()) {
              <hn-banner class="sm:col-span-2" [text]="formProblem()" />
            }
            @for (f of fields; track f.key) {
              <hn-field [class]="f.wide ? 'sm:col-span-2' : ''"[label]="'pos.customers.fields.' + f.key | t" [for]="'pos-cust-' + f.key" [required]="f.key === 'name'" [errors]="errorFor(f.key)">
                <input hnInput [id]="'pos-cust-' + f.key" [name]="f.key" class="h-11" [attr.type]="f.type" [attr.inputmode]="f.inputmode" [attr.autocomplete]="f.autocomplete"
                  [attr.maxlength]="f.max" [value]="form()[f.key]" (input)="setField(f.key, $any($event.target).value)"
                  [attr.aria-invalid]="!!errorFor(f.key).length" [attr.aria-describedby]="errorFor(f.key).length ? 'pos-cust-' + f.key + '-error' : null"
                  [attr.data-testid]="'pos-cust-' + f.key" />
              </hn-field>
            }
            <p class="text-xs text-slate-500 sm:col-span-2">{{ 'pos.customers.privacy' | t }}</p>
            <div class="flex flex-wrap justify-end gap-2 sm:col-span-2">
              <button hnButton type="button" class="min-h-11" (click)="backToList()">{{ 'pos.sell.back' | t }}</button>
              <button hnButton variant="primary" type="submit" class="min-h-11" [loading]="busy()" [disabled]="busy()" data-testid="pos-cust-save">
                {{ (mode() === 'attach' && !editing() ? 'pos.customers.saveAndAttach' : 'common.save') | t }}
              </button>
            </div>
          </form>
        }
      }
    </hn-dialog>
  `,
})
export class PosCustomerDialogComponent {
  private readonly api = inject(PosApi);
  private readonly i18n = inject(I18nService);
  private readonly toast = inject(ToastService);

  readonly open = input(false);
  readonly attachedId = input<string | null>(null);
  readonly mode = input<'attach' | 'pick'>('attach');
  readonly closed = output<void>();
  readonly chosen = output<PosCustomer>();
  readonly detach = output<void>();

  protected readonly fields: { key: CustomerField; max: number; wide?: boolean; type?: string; inputmode?: string; autocomplete: string }[] = [
    { key: 'name', max: CUSTOMER_LIMITS.name, autocomplete: 'off' },
    { key: 'company', max: CUSTOMER_LIMITS.company, autocomplete: 'off' },
    { key: 'email', max: CUSTOMER_LIMITS.email, type: 'email', inputmode: 'email', autocomplete: 'off' },
    { key: 'phone', max: CUSTOMER_LIMITS.phone, type: 'tel', inputmode: 'tel', autocomplete: 'off' },
    { key: 'street', max: CUSTOMER_LIMITS.street, wide: true, autocomplete: 'off' },
    { key: 'postalCode', max: CUSTOMER_LIMITS.postalCode, autocomplete: 'off' },
    { key: 'city', max: CUSTOMER_LIMITS.city, autocomplete: 'off' },
    { key: 'countryCode', max: 2, autocomplete: 'off' },
    { key: 'vatId', max: CUSTOMER_LIMITS.vatId + 5, autocomplete: 'off' },
  ];

  protected readonly view = signal<'list' | 'form'>('list');
  protected readonly search = signal('');
  protected readonly appliedSearch = signal('');
  protected readonly items = signal<PosCustomer[]>([]);
  protected readonly total = signal(0);
  protected readonly loading = signal(false);
  protected readonly listError = signal<unknown>(null);
  private page = 0;
  private token = 0;
  private destroyed = false;

  protected readonly editing = signal<PosCustomer | null>(null);
  protected readonly form = signal<CustomerForm>(emptyCustomerForm());
  protected readonly clientErrors = signal<CustomerFormErrors>({});
  protected readonly serverProblem = signal<ApiProblem | null>(null);
  protected readonly formProblem = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly summary = customerSummary;

  constructor() {
    inject(DestroyRef).onDestroy(() => (this.destroyed = true));
    effect(() => {
      if (!this.open()) return;
      untracked(() => {
        this.view.set('list');
        this.search.set('');
        this.appliedSearch.set('');
        void this.loadFirst();
        setTimeout(() => document.getElementById('pos-cust-search')?.focus());
      });
    });
    effect((onCleanup) => {
      const wanted = this.search().trim().slice(0, 100);
      if (wanted === untracked(() => this.appliedSearch())) return;
      const timer = setTimeout(() => {
        this.appliedSearch.set(wanted);
        void this.loadFirst();
      }, 250);
      onCleanup(() => clearTimeout(timer));
    });
  }

  protected close(): void {
    if (!this.busy()) this.closed.emit();
  }

  protected choose(c: PosCustomer): void {
    this.chosen.emit(c);
  }

  // ---- list
  private loadFirst(): Promise<void> {
    this.page = 0;
    this.items.set([]);
    this.total.set(0);
    return this.load(1);
  }

  protected loadMore(): void {
    if (!this.loading() && this.items().length < this.total()) void this.load(this.page + 1);
  }

  private async load(page: number): Promise<void> {
    const token = ++this.token;
    this.loading.set(true);
    this.listError.set(null);
    try {
      const r = await firstValueFrom(this.api.customers({ search: this.appliedSearch() || null, page, pageSize: PAGE_SIZE }));
      if (token !== this.token || this.destroyed) return;
      const seen = new Set(page === 1 ? [] : this.items().map((c) => c.id));
      this.items.set(page === 1 ? r.items : [...this.items(), ...r.items.filter((c) => !seen.has(c.id))]);
      this.total.set(r.totalCount);
      this.page = page;
    } catch (e) {
      if (token === this.token) this.listError.set(e);
    } finally {
      if (token === this.token) this.loading.set(false);
    }
  }

  // ---- form
  protected startCreate(): void {
    const f = emptyCustomerForm();
    // A typed search that is not an email/phone is usually the new customer's name.
    const typed = this.search().trim();
    if (typed && !/[@\d]/.test(typed)) f.name = typed.slice(0, CUSTOMER_LIMITS.name);
    this.openForm(null, f);
  }

  protected startEdit(c: PosCustomer): void {
    this.openForm(c, customerToForm(c));
  }

  private openForm(c: PosCustomer | null, f: CustomerForm): void {
    this.editing.set(c);
    this.form.set(f);
    this.clientErrors.set({});
    this.serverProblem.set(null);
    this.formProblem.set(null);
    this.view.set('form');
    setTimeout(() => document.getElementById('pos-cust-name')?.focus());
  }

  protected backToList(): void {
    this.view.set('list');
    setTimeout(() => document.getElementById('pos-cust-search')?.focus());
  }

  protected setField(key: CustomerField, value: string): void {
    this.form.update((f) => ({ ...f, [key]: value }));
    if (this.clientErrors()[key]) this.clientErrors.update((e) => ({ ...e, [key]: undefined }));
  }

  protected errorFor(key: CustomerField): string[] {
    const client = this.clientErrors()[key];
    if (client) return [this.i18n.t('pos.customers.errors.' + client)];
    return fieldErrors(this.serverProblem(), key);
  }

  protected async save(): Promise<void> {
    const errors = validateCustomer(this.form());
    this.clientErrors.set(errors);
    if (Object.values(errors).some(Boolean)) return;
    this.busy.set(true);
    this.serverProblem.set(null);
    this.formProblem.set(null);
    const body = customerRequest(this.form());
    const editing = this.editing();
    try {
      const saved = await firstValueFrom(editing ? this.api.updateCustomer(editing.id, body) : this.api.createCustomer(body));
      this.toast.success(this.i18n.t(editing ? 'pos.customers.updated' : 'pos.customers.created', { name: saved.name }));
      if (editing && this.mode() === 'attach' && editing.id !== this.attachedId()) {
        this.items.update((list) => list.map((c) => (c.id === saved.id ? saved : c)));
        this.view.set('list');
      } else {
        this.chosen.emit(saved);
      }
    } catch (e) {
      const problem = toApiProblem(e);
      this.serverProblem.set(problem);
      if (!Object.keys(problem.errors).length) this.formProblem.set(posErrorText(this.i18n, e));
    } finally {
      this.busy.set(false);
    }
  }
}
