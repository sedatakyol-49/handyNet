import { ChangeDetectionStrategy, Component, inject, output, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { debounceTime, distinctUntilChanged, of } from 'rxjs';
import { AnkaufApi } from '../../core/api/handynet-api';
import { SellerSuggestion } from '../../core/api/models';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';

/** Returning sellers of the own shop (name search); the ID document is captured again for every purchase. */
@Component({
  selector: 'hn-seller-search',
  imports: [...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    <hn-field [label]="'ankauf.returning.search' | t" for="in-seller-search" [hint]="'ankauf.returning.hint' | t">
      <input hnInput id="in-seller-search" type="search" autocomplete="off" [placeholder]="'ankauf.returning.placeholder' | t"
        [value]="term()" (input)="term.set($any($event.target).value)" aria-controls="in-seller-results" data-testid="in-seller-search" />
    </hn-field>
    <ul id="in-seller-results" class="mt-2 divide-y divide-slate-100 overflow-hidden rounded-xl border border-slate-200 bg-white" [attr.aria-busy]="results.loading()"
      [attr.aria-label]="'ankauf.returning.results' | t" data-testid="in-seller-results">
      @for (s of results.value() ?? []; track s.latestIntakeId) {
        <li>
          <button type="button" class="flex w-full items-center gap-3 px-3 py-2.5 text-start text-sm transition-colors hover:bg-slate-50 focus-visible:bg-accent-50 focus-visible:outline-2 focus-visible:outline-accent-600"
            (click)="picked.emit(s)" [attr.data-seller]="s.lastName">
            <span class="grid size-9 shrink-0 place-items-center rounded-full bg-accent-50 text-sm font-semibold text-accent-800" aria-hidden="true">{{ s.firstName.charAt(0) }}{{ s.lastName.charAt(0) }}</span>
            <span class="min-w-0 flex-1">
              <span class="block truncate font-medium">{{ s.firstName }} {{ s.lastName }}</span>
              <span class="block truncate text-xs text-slate-500">{{ s.birthDate | hnDate: 'date' }} · {{ s.postalCode }} {{ s.city }}</span>
            </span>
            <span class="shrink-0 rounded-full bg-slate-100 px-2 py-0.5 text-xs text-slate-600">{{ 'ankauf.returning.count' | t: { count: s.intakeCount } }}</span>
          </button>
        </li>
      } @empty {
        <li class="px-3 py-3 text-sm text-slate-500">{{ (debounced().trim().length < 2 ? 'ankauf.returning.typeMore' : 'ankauf.returning.none') | t }}</li>
      }
    </ul>
  `,
})
export class SellerSearchComponent {
  private readonly api = inject(AnkaufApi);
  readonly picked = output<SellerSuggestion>();
  protected readonly term = signal('');
  protected readonly debounced = toSignal(toObservable(this.term).pipe(debounceTime(250), distinctUntilChanged()), { initialValue: '' });
  protected readonly results = createQuery(
    () => this.debounced().trim(),
    (q) => (q.length >= 2 ? this.api.sellers(q) : of([] as SellerSuggestion[])),
  );
}
