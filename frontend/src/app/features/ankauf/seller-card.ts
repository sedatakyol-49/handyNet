import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { SellerCaptureMethod, SellerCard } from '../../core/api/models';
import { UI } from '../../shared/ui';
import { daysUntilExpiry, today } from './intake.logic';

/** The seller of an intake as a card: person, address, ID document (masked) with expiry, and how the data was captured. */
@Component({
  selector: 'hn-seller-card',
  imports: [...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    <section class="hn-card overflow-hidden" aria-labelledby="seller-card-heading" data-testid="seller-card">
      <div class="flex items-center gap-3 border-b border-slate-200 bg-gradient-to-r from-accent-50 to-white px-4 py-3">
        @if (seller(); as s) {
          <span class="grid size-11 shrink-0 place-items-center rounded-full bg-accent-600 text-base font-bold text-white" aria-hidden="true">{{ s.firstName.charAt(0) }}{{ s.lastName.charAt(0) }}</span>
          <div class="min-w-0">
            <h2 id="seller-card-heading" class="truncate text-sm font-semibold" data-testid="intake-seller">{{ s.firstName }} {{ s.lastName }}</h2>
            <p class="text-xs text-slate-600">{{ 'ankauf.seller.title' | t }}</p>
          </div>
        } @else {
          <span class="grid size-11 shrink-0 place-items-center rounded-full bg-slate-200 text-base text-slate-600" aria-hidden="true">?</span>
          <h2 id="seller-card-heading" class="text-sm font-semibold" data-testid="intake-seller">{{ name() }}</h2>
        }
        @if (capture(); as c) {
          <span class="ms-auto shrink-0 rounded-full border border-slate-200 bg-white px-2 py-0.5 text-[11px] font-medium text-slate-700" data-testid="seller-capture">
            {{ 'ankauf.capture.' + c | t }}
          </span>
        }
      </div>
      @if (seller(); as s) {
        <dl class="grid grid-cols-[auto_minmax(0,1fr)] gap-x-4 gap-y-1.5 px-4 py-3 text-sm">
          <dt class="text-slate-500">{{ 'ankauf.intake.birthDate' | t }}</dt>
          <dd>{{ s.birthDate | hnDate: 'date' }}</dd>
          <dt class="text-slate-500">{{ 'ankauf.seller.address' | t }}</dt>
          <dd>{{ s.street }}<br />{{ s.postalCode }} {{ s.city }}@if (s.country) { ({{ s.country }}) }</dd>
          @if (s.nationality) {
            <dt class="text-slate-500">{{ 'ankauf.seller.nationality' | t }}</dt>
            <dd>{{ s.nationality }}</dd>
          }
          <dt class="text-slate-500">{{ 'ankauf.intake.idDocumentType' | t }}</dt>
          <dd>{{ s.idDocumentType | tEnum: 'idDocumentType' }}</dd>
          <dt class="text-slate-500">{{ 'ankauf.intake.idDocumentNumber' | t }}</dt>
          <dd class="hn-mono" dir="ltr">{{ s.idDocumentNumberMasked }}</dd>
          @if (s.idExpiryDate) {
            <dt class="text-slate-500">{{ 'ankauf.seller.expiry' | t }}</dt>
            <dd>
              {{ s.idExpiryDate | hnDate: 'date' }}
              @if (expiresSoon()) {
                <span class="ms-1 rounded-full bg-amber-50 px-2 py-0.5 text-xs text-amber-800">{{ 'ankauf.seller.expiresSoon' | t }}</span>
              }
            </dd>
          }
          @if (s.idIssuingAuthority || s.idIssuingCountry) {
            <dt class="text-slate-500">{{ 'ankauf.seller.issuedBy' | t }}</dt>
            <dd>{{ s.idIssuingAuthority }}@if (s.idIssuingAuthority && s.idIssuingCountry) {, }{{ s.idIssuingCountry }}</dd>
          }
        </dl>
      } @else if (anonymizedAt()) {
        <p class="px-4 py-3 text-sm text-slate-600">{{ 'ankauf.seller.anonymized' | t: { date: (anonymizedAt() | hnDate: 'date') } }}</p>
      }
    </section>
  `,
})
export class SellerCardComponent {
  readonly seller = input<SellerCard | null | undefined>(null);
  readonly name = input('');
  readonly capture = input<SellerCaptureMethod | null | undefined>(null);
  readonly anonymizedAt = input<string | null | undefined>(null);
  protected readonly expiresSoon = computed(() => {
    const d = daysUntilExpiry(this.seller()?.idExpiryDate, today());
    return d !== null && d >= 0 && d <= 30;
  });
}
