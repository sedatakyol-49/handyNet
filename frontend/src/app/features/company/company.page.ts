import { ChangeDetectionStrategy, Component, ElementRef, computed, effect, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { toApiProblem } from '../../core/api/api-problem';
import { OrganizationApi } from '../../core/api/handynet-api';
import { CompanyProfile } from '../../core/api/models';
import { CompanyBrandingStore } from '../../core/company/company-branding.store';
import { I18nService, LANGUAGES } from '../../core/i18n/i18n.service';
import { ToastService, describeProblem } from '../../core/ui-state/toast.service';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';
import {
  COMPANY_LIMITS,
  CompanyField,
  CompanyForm,
  fieldOfApiError,
  formFromProfile,
  formatIban,
  isDirty,
  legalChanges,
  logoFileProblem,
  receiptLines,
  requestFromForm,
  validateCompanyForm,
} from './company.logic';
import { LogoCropDialogComponent } from './logo-crop.dialog';

interface FieldDef {
  key: CompanyField;
  type?: 'text' | 'email' | 'tel' | 'url';
  autocomplete?: string;
  required?: boolean;
  hint?: string;
  inputmode?: string;
  /** Spans both grid columns. */
  wide?: boolean;
  dir?: 'ltr';
}

interface SectionDef {
  id: 'master' | 'contact' | 'tax' | 'bank';
  fields: FieldDef[];
}

/**
 * Own company data ("Unternehmen", Verwaltung): every member sees it, owners (organization.manage) edit it. Sections
 * master data, contact, tax and register, bank details, receipts (header/footer with a live 80 mm preview) and logo
 * (crop + upload, remove). Saving sends the loaded version (409 on a concurrent change). Changes of the legal name or the
 * VAT ID are reported to the platform administration (no re-verification).
 */
@Component({
  selector: 'hn-company-page',
  imports: [...UI, LogoCropDialogComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-page-header [title]="'company.title' | t" [description]="'company.subtitle' | t" />
    <hn-banner class="mb-3" [error]="profile.error()" />

    @if (profile.value(); as p) {
      @if (!p.canEdit) {
        <hn-banner tone="info" class="mb-3" [text]="'company.readOnly' | t" data-testid="company-read-only" />
      }
      @if (conflict()) {
        <hn-banner tone="warning" class="mb-3" [heading]="'company.conflict.title' | t" [text]="'company.conflict.text' | t" data-testid="company-conflict">
          <button hnButton size="sm" class="mt-2" type="button" (click)="reloadAfterConflict()">{{ 'company.conflict.reload' | t }}</button>
        </hn-banner>
      }

      <form class="grid gap-4 xl:grid-cols-[minmax(0,1fr)_22rem]" (submit)="$event.preventDefault(); save(p)" novalidate data-testid="company-form">
        <div class="min-w-0 space-y-4">
          <fieldset [disabled]="!p.canEdit || busy()" class="min-w-0 space-y-4">
            @for (s of sections; track s.id) {
              <section class="hn-card p-4" [attr.aria-labelledby]="'company-' + s.id + '-heading'" [attr.data-testid]="'company-section-' + s.id">
                <h2 class="mb-3 text-sm" [id]="'company-' + s.id + '-heading'">{{ 'company.sections.' + s.id | t }}</h2>
                @if (s.id === 'tax') {
                  <p class="mb-3 text-xs text-slate-600">{{ 'company.legalHint' | t }}</p>
                }
                <div class="grid gap-3 sm:grid-cols-2">
                  @for (f of s.fields; track f.key) {
                    <hn-field [class]="f.wide ? 'sm:col-span-2' : ''" [label]="'company.fields.' + f.key | t" [for]="'company-' + f.key" [required]="!!f.required"
                      [hint]="f.hint ? (f.hint | t) : null" [errors]="errorOf(f.key)">
                      <input hnInput [id]="'company-' + f.key" [name]="f.key" [type]="f.type ?? 'text'" [attr.autocomplete]="f.autocomplete ?? 'off'"
                        [attr.inputmode]="f.inputmode ?? null" [attr.dir]="f.dir ?? null" [value]="form()[f.key]" (input)="set(f.key, $any($event.target).value)"
                        (blur)="f.key === 'iban' && formatIbanField()" [attr.aria-invalid]="!!errorOf(f.key)"
                        [attr.aria-describedby]="errorOf(f.key) ? 'company-' + f.key + '-error' : f.hint ? 'company-' + f.key + '-hint' : null"
                        [attr.data-testid]="'company-' + f.key" />
                    </hn-field>
                  }
                </div>
              </section>
            }

            <section class="hn-card p-4" aria-labelledby="company-receipts-heading" data-testid="company-section-receipts">
              <h2 id="company-receipts-heading" class="mb-1 text-sm">{{ 'company.sections.receipts' | t }}</h2>
              <p class="mb-3 text-xs text-slate-600">{{ 'company.receiptsHint' | t }}</p>
              <div class="grid gap-3">
                @for (key of receiptFields; track key) {
                  <hn-field [label]="'company.fields.' + key | t" [for]="'company-' + key" [errors]="errorOf(key)"
                    [hint]="'company.receiptCounter' | t: { used: form()[key].length, max: limit(key), lines: lineCount(form()[key]), maxLines: maxLines }">
                    <textarea hnInput [id]="'company-' + key" [name]="key" rows="4" [attr.maxlength]="limit(key)" [value]="form()[key]"
                      (input)="set(key, $any($event.target).value)" [attr.aria-invalid]="!!errorOf(key)"
                      [attr.aria-describedby]="errorOf(key) ? 'company-' + key + '-error' : 'company-' + key + '-hint'"
                      [placeholder]="'company.placeholders.' + key | t" [attr.data-testid]="'company-' + key"></textarea>
                  </hn-field>
                }
              </div>
            </section>

            <section class="hn-card p-4" aria-labelledby="company-emails-heading" data-testid="company-section-emails">
              <h2 id="company-emails-heading" class="mb-3 text-sm">{{ 'company.sections.emails' | t }}</h2>
              <hn-field [label]="'company.fields.emailLanguage' | t" for="company-emailLanguage" [hint]="'company.hints.emailLanguage' | t" [errors]="errorOf('emailLanguage')">
                <select hnSelect id="company-emailLanguage" name="emailLanguage" [value]="form().emailLanguage" (change)="set('emailLanguage', $any($event.target).value)"
                  aria-describedby="company-emailLanguage-hint" data-testid="company-emailLanguage">
                  @for (l of languages; track l.code) {
                    <option [value]="l.code" [selected]="form().emailLanguage === l.code">{{ l.label }}</option>
                  }
                </select>
              </hn-field>
            </section>
          </fieldset>

          @if (p.canEdit) {
            <div class="hn-card sticky bottom-2 z-10 flex flex-wrap items-center gap-2 p-3 shadow-md" data-testid="company-actions">
              @if (legal().length > 0) {
                <p class="me-auto text-xs text-amber-900" role="status" data-testid="company-legal-notice">{{ 'company.legalNotice' | t }}</p>
              } @else if (dirty()) {
                <p class="me-auto text-xs text-slate-600" role="status">{{ 'company.unsaved' | t }}</p>
              } @else {
                <span class="me-auto"></span>
              }
              <button hnButton type="button" [disabled]="!dirty() || busy()" (click)="discard(p)" data-testid="company-discard">{{ 'company.discard' | t }}</button>
              <button hnButton variant="primary" type="submit" [loading]="busy()" [disabled]="busy() || !dirty()" data-testid="company-save">{{ 'common.save' | t }}</button>
            </div>
          }
        </div>

        <aside class="min-w-0 space-y-4 xl:sticky xl:top-16 xl:self-start">
          <section class="hn-card p-4" aria-labelledby="company-logo-heading" data-testid="company-section-logo">
            <h2 id="company-logo-heading" class="mb-1 text-sm">{{ 'company.sections.logo' | t }}</h2>
            <p class="mb-3 text-xs text-slate-600">{{ 'company.logo.hint' | t }}</p>
            <div class="mb-3 grid min-h-24 place-items-center rounded-md border border-dashed border-slate-300 bg-slate-50 p-3">
              @if (logoUrl(); as url) {
                <img [src]="url" [alt]="'company.logo.alt' | t: { name: p.name }" class="max-h-24 max-w-full object-contain" data-testid="company-logo-image" />
              } @else {
                <span class="text-sm text-slate-500" data-testid="company-logo-empty">{{ 'company.logo.none' | t }}</span>
              }
            </div>
            @if (p.canEdit) {
              <div class="flex flex-wrap gap-2">
                <label for="company-logo-file">
                  <span class="inline-flex min-h-9 cursor-pointer items-center rounded-md border border-slate-300 bg-white px-3 text-sm font-medium text-slate-800 hover:bg-slate-100 focus-within:ring-2 focus-within:ring-accent-600 active:bg-slate-200">
                    {{ (p.logo ? 'company.logo.replace' : 'company.logo.upload') | t }}
                    <input id="company-logo-file" type="file" accept="image/png,image/jpeg,image/webp" class="sr-only" (change)="pick($event)" data-testid="company-logo-file" />
                  </span>
                </label>
                @if (p.logo) {
                  <button hnButton variant="ghost" type="button" [disabled]="logoBusy()" (click)="confirmRemove.set(true)" data-testid="company-logo-remove">
                    {{ 'company.logo.remove' | t }}
                  </button>
                }
              </div>
              @if (logoError()) {
                <p class="mt-2 text-xs text-red-700" role="alert" data-testid="company-logo-error">{{ logoError() }}</p>
              }
            }
          </section>

          <section class="hn-card p-4" aria-labelledby="company-preview-heading" data-testid="company-receipt-preview">
            <h2 id="company-preview-heading" class="mb-1 text-sm">{{ 'company.preview.title' | t }}</h2>
            <p class="mb-3 text-xs text-slate-600">{{ 'company.preview.hint' | t }}</p>
            <div class="overflow-x-auto">
              <div class="mx-auto bg-white p-3 font-mono text-[11px] leading-snug text-slate-900 shadow-sm ring-1 ring-slate-300" style="width: 80mm; max-width: 100%">
                @if (logoUrl(); as url) {
                  <img [src]="url" alt="" class="hn-receipt-logo mx-auto mb-1" />
                }
                @for (l of preview().header; track $index) {
                  <p class="min-h-[1em] text-center" data-testid="company-preview-header">{{ l }}</p>
                }
                <p class="mt-1 text-center font-bold">{{ form().name || p.name }}</p>
                <p class="text-center">{{ form().street }}</p>
                <p class="text-center">{{ form().postalCode }} {{ form().city }}</p>
                @if (form().vatId) {
                  <p class="text-center">{{ 'pos.receipt.vatId' | t }}: {{ form().vatId }}</p>
                }
                <div class="my-2 border-y border-dashed border-slate-400 py-1">
                  <p class="flex justify-between"><span>{{ 'company.preview.sampleItem' | t }}</span><span>19,99 €</span></p>
                </div>
                <p class="flex justify-between font-bold"><span>{{ 'pos.receipt.total' | t }}</span><span>19,99 €</span></p>
                @if (preview().footer.length) {
                  <div class="mt-2 border-t border-dashed border-slate-400 pt-1">
                    @for (l of preview().footer; track $index) {
                      <p class="min-h-[1em] text-center" data-testid="company-preview-footer">{{ l }}</p>
                    }
                  </div>
                }
              </div>
            </div>
          </section>
        </aside>
      </form>

      <hn-logo-crop-dialog [file]="pendingFile()" [busy]="logoBusy()" (cropped)="upload($event.blob, $event.fileName)" (cancelled)="pendingFile.set(null)" />

      <hn-dialog [open]="confirmRemove()" [title]="'company.logo.removeTitle' | t" (closed)="confirmRemove.set(false)">
        <p class="text-sm">{{ 'company.logo.removeText' | t }}</p>
        <div class="mt-4 flex justify-end gap-2">
          <button hnButton type="button" (click)="confirmRemove.set(false)">{{ 'common.cancel' | t }}</button>
          <button hnButton variant="danger" type="button" [loading]="logoBusy()" (click)="removeLogo()" data-testid="company-logo-remove-confirm">
            {{ 'company.logo.remove' | t }}
          </button>
        </div>
      </hn-dialog>
    } @else if (profile.loading()) {
      <p class="py-10 text-center text-sm text-slate-500">{{ 'common.loading' | t }}</p>
    }
  `,
})
export class CompanyPage {
  private readonly api = inject(OrganizationApi);
  private readonly store = inject(CompanyBrandingStore);
  private readonly toast = inject(ToastService);
  private readonly i18n = inject(I18nService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  protected readonly profile = createQuery(() => true, () => this.api.profile());

  protected readonly sections: SectionDef[] = [
    {
      id: 'master',
      fields: [
        { key: 'name', required: true, autocomplete: 'organization', hint: 'company.hints.name' },
        { key: 'legalName', required: true, hint: 'company.hints.legalName' },
        { key: 'street', required: true, autocomplete: 'street-address', wide: true },
        { key: 'postalCode', required: true, autocomplete: 'postal-code', inputmode: 'numeric' },
        { key: 'city', required: true, autocomplete: 'address-level2' },
        { key: 'countryCode', autocomplete: 'country', hint: 'company.hints.countryCode' },
      ],
    },
    {
      id: 'contact',
      fields: [
        { key: 'contactEmail', type: 'email', autocomplete: 'email', dir: 'ltr' },
        { key: 'contactPhone', type: 'tel', autocomplete: 'tel', dir: 'ltr' },
        { key: 'website', type: 'url', autocomplete: 'url', wide: true, dir: 'ltr', hint: 'company.hints.website' },
      ],
    },
    {
      id: 'tax',
      fields: [
        { key: 'vatId', dir: 'ltr', hint: 'company.hints.vatId' },
        { key: 'taxNumber', dir: 'ltr', inputmode: 'numeric' },
        { key: 'managingDirector', wide: true },
        { key: 'registerCourt', hint: 'company.hints.registerCourt' },
        { key: 'registerNumber', dir: 'ltr', hint: 'company.hints.registerNumber' },
      ],
    },
    {
      id: 'bank',
      fields: [
        { key: 'bankName' },
        { key: 'bic', dir: 'ltr' },
        { key: 'iban', dir: 'ltr', wide: true, hint: 'company.hints.iban' },
        { key: 'paymentTermsDays', inputmode: 'numeric', hint: 'company.hints.paymentTermsDays' },
      ],
    },
  ];
  protected readonly receiptFields = ['receiptHeader', 'receiptFooter'] as const;
  protected readonly maxLines = COMPANY_LIMITS.receiptLines;
  protected readonly languages = LANGUAGES;

  protected readonly form = signal<CompanyForm>(emptyForm());
  private readonly original = signal<CompanyForm>(emptyForm());
  protected readonly errors = signal<Partial<Record<CompanyField, string>>>({});
  protected readonly busy = signal(false);
  protected readonly conflict = signal(false);

  protected readonly dirty = computed(() => isDirty(this.form(), this.original()));
  protected readonly legal = computed(() => legalChanges(this.form(), this.original()));
  protected readonly preview = computed(() => ({ header: receiptLines(this.form().receiptHeader), footer: receiptLines(this.form().receiptFooter) }));

  // ---- logo
  protected readonly pendingFile = signal<File | null>(null);
  protected readonly logoBusy = signal(false);
  protected readonly logoError = signal<string | null>(null);
  protected readonly confirmRemove = signal(false);
  protected readonly logoUrl = computed(() => this.api.assetUrl(this.profile.value()?.logo?.screenUrl));

  constructor() {
    effect(() => {
      const p = this.profile.value();
      if (p) this.load(p);
    });
  }

  protected errorOf(key: CompanyField): string | null {
    const e = this.errors()[key];
    return e ? (e.startsWith('company.') ? this.i18n.t(e) : e) : null;
  }

  protected limit(key: 'receiptHeader' | 'receiptFooter'): number {
    return COMPANY_LIMITS[key];
  }

  protected lineCount(v: string): number {
    return v.trim() === '' ? 0 : v.trim().split(/\r?\n/).length;
  }

  protected set(key: CompanyField, value: string): void {
    this.form.update((f) => ({ ...f, [key]: value }));
    if (this.errors()[key]) this.errors.update((e) => ({ ...e, [key]: undefined }));
  }

  protected formatIbanField(): void {
    const v = this.form().iban;
    if (v.trim()) this.set('iban', formatIban(v));
  }

  protected discard(p: CompanyProfile): void {
    this.load(p);
  }

  protected async save(p: CompanyProfile): Promise<void> {
    const errors = validateCompanyForm(this.form());
    this.errors.set(errors);
    if (Object.keys(errors).length > 0) {
      this.focusFirstError();
      return;
    }
    this.busy.set(true);
    try {
      const saved = await firstValueFrom(this.api.updateProfile(requestFromForm(this.form(), p.version)));
      this.profile.set(saved);
      this.store.set(saved);
      this.toast.success(this.i18n.t('company.saved'));
    } catch (e) {
      const problem = toApiProblem(e);
      if (problem.code === 'concurrency') {
        this.conflict.set(true);
      } else if (problem.code === 'validation.failed') {
        const mapped: Partial<Record<CompanyField, string>> = {};
        for (const [k, messages] of Object.entries(problem.errors)) {
          const field = fieldOfApiError(k);
          if (field) mapped[field] = messages.join(' ');
        }
        this.errors.set(mapped);
        this.focusFirstError();
      } else if (problem.code === 'organization.vat_taken') {
        this.errors.set({ vatId: this.i18n.t('company.errors.vatTaken') });
        this.focusFirstError();
      } else {
        this.toast.error(describeProblem(this.i18n, problem));
      }
    } finally {
      this.busy.set(false);
    }
  }

  protected reloadAfterConflict(): void {
    this.conflict.set(false);
    this.profile.reload();
  }

  protected pick(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0] ?? null;
    input.value = '';
    this.logoError.set(null);
    if (!file) return;
    const problem = logoFileProblem(file);
    if (problem) {
      this.logoError.set(this.i18n.t(problem));
      return;
    }
    this.pendingFile.set(file);
  }

  protected async upload(blob: Blob, fileName: string): Promise<void> {
    this.logoBusy.set(true);
    try {
      const logo = await firstValueFrom(this.api.uploadLogo(blob, fileName));
      this.pendingFile.set(null);
      const p = this.profile.value();
      if (p) {
        const updated = { ...p, logo };
        this.profile.set(updated);
        this.store.set(updated);
      }
      this.toast.success(this.i18n.t('company.logo.uploaded'));
    } catch (e) {
      this.pendingFile.set(null);
      this.logoError.set(this.logoProblemText(e));
    } finally {
      this.logoBusy.set(false);
    }
  }

  protected async removeLogo(): Promise<void> {
    this.logoBusy.set(true);
    try {
      await firstValueFrom(this.api.removeLogo());
      const p = this.profile.value();
      if (p) {
        const updated = { ...p, logo: null };
        this.profile.set(updated);
        this.store.set(updated);
      }
      this.confirmRemove.set(false);
      this.toast.success(this.i18n.t('company.logo.removed'));
    } catch (e) {
      this.confirmRemove.set(false);
      this.logoError.set(this.logoProblemText(e));
    } finally {
      this.logoBusy.set(false);
    }
  }

  private logoProblemText(e: unknown): string {
    const problem = toApiProblem(e);
    const known: Record<string, string> = {
      'image.unsupported_type': 'company.logo.errors.type',
      'image.too_large': 'company.logo.errors.size',
      'image.too_small': 'company.logo.errors.small',
      'image.corrupt': 'company.logo.errors.read',
      'image.dimensions': 'company.logo.errors.dimensions',
    };
    return problem.code && known[problem.code] ? this.i18n.t(known[problem.code]) : describeProblem(this.i18n, problem);
  }

  private load(p: CompanyProfile): void {
    const form = formFromProfile(p);
    this.form.set(form);
    this.original.set(form);
    this.errors.set({});
  }

  private focusFirstError(): void {
    setTimeout(() => {
      const el = this.host.nativeElement.querySelector<HTMLElement>('[aria-invalid="true"]');
      el?.focus();
    });
  }
}

function emptyForm(): CompanyForm {
  return {
    name: '', legalName: '', street: '', postalCode: '', city: '', countryCode: 'DE', vatId: '', taxNumber: '', contactEmail: '', contactPhone: '',
    website: '', receiptHeader: '', receiptFooter: '', bankName: '', iban: '', bic: '', managingDirector: '', registerCourt: '', registerNumber: '',
    paymentTermsDays: '', emailLanguage: 'de',
  };
}
