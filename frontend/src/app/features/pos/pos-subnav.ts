import { ChangeDetectionStrategy, Component, computed, effect, inject, input, untracked } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { PosApi } from '../../core/api/handynet-api';
import { TranslatePipe } from '../../core/i18n/translate.pipe';
import { createQuery } from '../../shared/data/query';
import { PosContext } from './pos-context';

/**
 * Tabs of one register on the POS app's secondary pages (selling, receipts, sessions). Also tells the top bar which
 * register and session the page belongs to.
 */
@Component({
  selector: 'hn-pos-subnav',
  imports: [RouterLink, RouterLinkActive, TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'hn-no-print mb-4 block' },
  template: `
    <nav class="flex flex-wrap items-center gap-1 border-b border-slate-200 text-sm" [attr.aria-label]="'pos.subnav.label' | t">
      @for (tab of tabs; track tab.path) {
        <a [routerLink]="['/kasse', registerId(), ...tab.path]" routerLinkActive="border-accent-700 text-accent-800 font-semibold"
          [routerLinkActiveOptions]="{ exact: true }" #rla="routerLinkActive" [attr.aria-current]="rla.isActive ? 'page' : null"
          class="-mb-px inline-flex min-h-11 items-center border-b-2 border-transparent px-3 text-slate-700 hover:text-slate-900" [attr.data-testid]="'pos-tab-' + tab.id">
          {{ tab.label | t }}
        </a>
      }
    </nav>
  `,
})
export class PosSubnavComponent {
  private readonly api = inject(PosApi);
  private readonly ctx = inject(PosContext);

  readonly registerId = input.required<string>();
  protected readonly tabs = [
    { id: 'till', path: [] as string[], label: 'pos.subnav.till' },
    { id: 'receipts', path: ['belege'], label: 'pos.subnav.receipts' },
    { id: 'sessions', path: ['sitzungen'], label: 'pos.subnav.sessions' },
  ];

  private readonly register = createQuery(
    () => this.registerId(),
    (id) => this.api.register(id),
  );
  private readonly session = createQuery(
    () => this.registerId(),
    (id) => this.api.currentSession(id),
  );
  private readonly sessionLoaded = computed(() => !this.session.loading());

  constructor() {
    effect(() => {
      const id = this.registerId();
      const name = this.register.value()?.name ?? null;
      untracked(() => this.ctx.setRegister(id, name));
    });
    effect(() => {
      const s = this.session.value();
      const loaded = this.sessionLoaded();
      untracked(() => this.ctx.session.set(loaded || s ? s : undefined));
    });
  }

  /** After closing a session or other changes. */
  reloadSession(): void {
    this.session.reload();
  }
}
