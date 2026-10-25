import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { fieldErrors, toApiProblem } from '../../core/api/api-problem';
import { AnkaufApi } from '../../core/api/handynet-api';
import { ID_DOCUMENT_TYPES } from '../../core/api/models';
import { I18nService, Lang } from '../../core/i18n/i18n.service';
import { describeProblem } from '../../core/ui-state/toast.service';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';
import { SELLER_MAX_LENGTH, SellerField, SellerForm, emptySellerForm, latestAdultBirthDate, today, validateSellerForm } from './intake.logic';
import { SignaturePadComponent } from './signature-pad';

/**
 * Public QR self check-in form (`/checkin/:token`) on the seller's own phone: no account, single use, valid for a few
 * minutes. The seller enters their data (and may sign); the shop's employee verifies it against the ID document.
 */
@Component({
  selector: 'hn-public-checkin-page',
  imports: [FormsModule, SignaturePadComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-h-dvh bg-gradient-to-b from-accent-50 to-slate-100 px-4 py-6' },
  template: `
    <main class="mx-auto max-w-lg" data-testid="public-checkin">
      <div class="mb-4 flex items-center justify-between gap-3">
        <span class="inline-flex items-center gap-2 text-sm font-semibold text-slate-700">
          <span class="grid size-8 place-items-center rounded-lg bg-accent-600 text-xs font-extrabold text-white" aria-hidden="true">SH</span>SEHA
        </span>
        <label class="flex items-center gap-2 text-xs text-slate-600">
          <span class="sr-only">{{ 'shell.language' | t }}</span>
          <select class="hn-control min-h-11 w-auto" [value]="i18n.lang()" (change)="setLanguage($any($event.target).value)" data-testid="checkin-lang">
            @for (l of i18n.languages; track l.code) {
              <option [value]="l.code" [selected]="l.code === i18n.lang()">{{ l.label }}</option>
            }
          </select>
        </label>
      </div>

      @if (done()) {
        <div class="rounded-2xl bg-white p-6 text-center shadow-sm" role="status" data-testid="checkin-done">
          <span class="mx-auto grid size-14 place-items-center rounded-full bg-emerald-600 text-2xl text-white" aria-hidden="true">✓</span>
          <h1 class="mt-3 text-lg font-bold">{{ 'ankauf.public.doneTitle' | t }}</h1>
          <p class="mt-2 text-sm text-slate-600">{{ 'ankauf.public.doneText' | t }}</p>
        </div>
      } @else if (info.error()) {
        <div class="rounded-2xl bg-white p-6 text-center shadow-sm" role="alert" data-testid="checkin-missing">
          <h1 class="text-lg font-bold">{{ 'ankauf.public.missingTitle' | t }}</h1>
          <p class="mt-2 text-sm text-slate-600">{{ 'ankauf.public.missing' | t }}</p>
        </div>
      } @else if (info.value(); as c) {
        @if (c.status === 'Submitted') {
          <div class="rounded-2xl bg-white p-6 text-center shadow-sm" role="status" data-testid="checkin-already">
            <h1 class="text-lg font-bold">{{ 'ankauf.public.alreadyTitle' | t }}</h1>
            <p class="mt-2 text-sm text-slate-600">{{ 'ankauf.public.already' | t }}</p>
          </div>
        } @else {
          <form class="space-y-4 rounded-2xl bg-white p-5 shadow-sm" (ngSubmit)="submit()" novalidate>
            <header>
              <p class="text-xs font-semibold tracking-wide text-accent-700 uppercase">{{ 'ankauf.public.kicker' | t }}</p>
              <h1 class="text-xl font-bold" data-testid="checkin-shop">{{ c.shopName }}</h1>
              <p class="mt-1 text-sm text-slate-600">{{ 'ankauf.public.intro' | t: { age: c.minimumAge } }}</p>
              <p class="mt-1 text-xs text-slate-500">{{ 'ankauf.public.validUntil' | t: { time: (c.expiresAt | hnDate) } }}</p>
            </header>

            <div class="grid gap-3 sm:grid-cols-2">
              <hn-field [label]="'ankauf.intake.firstName' | t" for="pc-first" [required]="true" [errors]="err('firstName')">
                <input hnInput id="pc-first" name="firstName" autocomplete="given-name" [maxlength]="max.firstName" [(ngModel)]="form.firstName" data-testid="pc-first" />
              </hn-field>
              <hn-field [label]="'ankauf.intake.lastName' | t" for="pc-last" [required]="true" [errors]="err('lastName')">
                <input hnInput id="pc-last" name="lastName" autocomplete="family-name" [maxlength]="max.lastName" [(ngModel)]="form.lastName" data-testid="pc-last" />
              </hn-field>
            </div>
            <hn-field [label]="'ankauf.intake.birthDate' | t" for="pc-birth" [required]="true" [errors]="err('birthDate')">
              <input hnInput id="pc-birth" name="birthDate" type="date" min="1900-01-01" [max]="birthMax" autocomplete="bday" [(ngModel)]="form.birthDate" data-testid="pc-birth" />
            </hn-field>
            <hn-field [label]="'ankauf.intake.street' | t" for="pc-street" [required]="true" [errors]="err('street')">
              <input hnInput id="pc-street" name="street" autocomplete="street-address" [maxlength]="max.street" [(ngModel)]="form.street" data-testid="pc-street" />
            </hn-field>
            <div class="grid grid-cols-[7rem_minmax(0,1fr)] gap-3">
              <hn-field [label]="'ankauf.intake.postalCode' | t" for="pc-zip" [required]="true" [errors]="err('postalCode')">
                <input hnInput id="pc-zip" name="postalCode" inputmode="numeric" autocomplete="postal-code" [maxlength]="max.postalCode" [(ngModel)]="form.postalCode" data-testid="pc-zip" />
              </hn-field>
              <hn-field [label]="'ankauf.intake.city' | t" for="pc-city" [required]="true" [errors]="err('city')">
                <input hnInput id="pc-city" name="city" autocomplete="address-level2" [maxlength]="max.city" [(ngModel)]="form.city" data-testid="pc-city" />
              </hn-field>
            </div>
            <hn-field [label]="'ankauf.seller.nationality' | t" for="pc-nat" [hint]="'ankauf.seller.nationalityHint' | t" [errors]="err('nationality')">
              <input hnInput id="pc-nat" name="nationality" maxlength="3" class="uppercase" [(ngModel)]="form.nationality" data-testid="pc-nat" />
            </hn-field>
            <fieldset class="space-y-3 rounded-xl border border-slate-200 p-3">
              <legend class="px-1 text-xs font-semibold tracking-wide text-slate-500 uppercase">{{ 'ankauf.seller.document' | t }}</legend>
              <hn-field [label]="'ankauf.intake.idDocumentType' | t" for="pc-type" [required]="true" [errors]="err('idDocumentType')">
                <select hnSelect id="pc-type" name="idDocumentType" [(ngModel)]="form.idDocumentType" data-testid="pc-type">
                  <option [ngValue]="null">{{ 'ankauf.intake.choose' | t }}</option>
                  @for (t of idTypes; track t) {
                    <option [ngValue]="t">{{ t | tEnum: 'idDocumentType' }}</option>
                  }
                </select>
              </hn-field>
              <hn-field [label]="'ankauf.intake.idDocumentNumber' | t" for="pc-no" [required]="true" [errors]="err('idDocumentNumber')">
                <input hnInput id="pc-no" name="idDocumentNumber" autocomplete="off" class="hn-mono uppercase" [maxlength]="max.idDocumentNumber" [(ngModel)]="form.idDocumentNumber" data-testid="pc-no" />
              </hn-field>
              <hn-field [label]="'ankauf.seller.expiry' | t" for="pc-exp" [required]="true" [errors]="err('idExpiryDate')">
                <input hnInput id="pc-exp" name="idExpiryDate" type="date" [(ngModel)]="form.idExpiryDate" data-testid="pc-exp" />
              </hn-field>
            </fieldset>

            <div>
              <p class="mb-1 text-sm font-medium">{{ 'ankauf.public.signTitle' | t }}</p>
              <p class="mb-2 text-xs text-slate-600" lang="de">{{ c.declaration }}</p>
              <hn-signature-pad testId="pc-signature" (changed)="signature.set($event)" />
            </div>

            <label class="flex items-start gap-2 text-sm">
              <input type="checkbox" class="hn-checkbox mt-0.5" name="consent" [(ngModel)]="consent" data-testid="pc-consent" />
              <span>{{ 'ankauf.public.consent' | t }}</span>
            </label>
            @if (consentError()) {
              <p class="text-xs text-red-700" role="alert">{{ 'ankauf.public.consentRequired' | t }}</p>
            }
            <hn-banner [text]="submitError()" />
            <button hnButton variant="primary" type="submit" class="w-full min-h-12 text-base" [loading]="busy()" [disabled]="busy()" data-testid="pc-submit">
              {{ 'ankauf.public.submit' | t }}
            </button>
            <p class="text-center text-xs text-slate-500">{{ 'ankauf.public.privacy' | t }}</p>
          </form>
        }
      } @else {
        <p class="py-10 text-center text-sm text-slate-500">{{ 'common.loading' | t }}</p>
      }
    </main>
  `,
})
export class PublicCheckInPage {
  private readonly api = inject(AnkaufApi);
  protected readonly i18n = inject(I18nService);
  readonly token = input.required<string>();
  protected readonly info = createQuery(() => this.token(), (token) => this.api.publicCheckIn(token));
  protected readonly idTypes = ID_DOCUMENT_TYPES;
  protected readonly max = SELLER_MAX_LENGTH;
  protected readonly birthMax = latestAdultBirthDate(today());
  protected form: SellerForm = emptySellerForm();
  protected consent = false;
  protected readonly errors = signal<Partial<Record<SellerField, string>>>({});
  protected readonly signature = signal<string | null>(null);
  protected readonly consentError = signal(false);
  protected readonly busy = signal(false);
  private readonly apiError = signal<unknown>(null);
  protected readonly submitError = signal<string | null>(null);
  protected readonly done = signal(false);

  protected err(field: SellerField): string | null {
    const key = this.errors()[field];
    if (key) return this.i18n.t(key);
    const api = fieldErrors(this.apiError() ? toApiProblem(this.apiError()) : null, 'seller.' + field);
    return api.length ? api.join(' ') : null;
  }

  protected setLanguage(lang: string): void {
    void this.i18n.use(lang as Lang);
  }

  protected async submit(): Promise<void> {
    const v = validateSellerForm(this.form, today());
    this.errors.set(v.errors);
    this.consentError.set(!this.consent);
    this.submitError.set(null);
    this.apiError.set(null);
    if (!v.seller || !this.consent) return;
    this.busy.set(true);
    try {
      await firstValueFrom(this.api.submitPublicCheckIn(this.token(), { seller: v.seller, signature: this.signature(), consent: true }));
      this.done.set(true);
    } catch (e) {
      this.apiError.set(e);
      const p = toApiProblem(e);
      this.submitError.set(p.code === 'validation.failed' ? this.i18n.t('ankauf.intake.fixFields') : describeProblem(this.i18n, p));
    } finally {
      this.busy.set(false);
    }
  }
}
