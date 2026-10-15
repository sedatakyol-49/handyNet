import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { TranslatePipe } from '../../core/i18n/translate.pipe';

/** Tabs of one register: till, receipts, sessions (plus the way back to the register list). */
@Component({
  selector: 'hn-pos-subnav',
  imports: [RouterLink, RouterLinkActive, TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'hn-no-print mb-3 block' },
  template: `
    <nav class="flex flex-wrap items-center gap-1 border-b border-slate-200 text-sm" [attr.aria-label]="'pos.subnav.label' | t">
      <a routerLink="/app/kasse" class="me-2 px-1 py-2 text-slate-600 hover:text-slate-900"><span class="inline-block rtl:-scale-x-100" aria-hidden="true">←</span> {{ 'pos.subnav.registers' | t }}</a>
      @for (tab of tabs; track tab.path) {
        <a [routerLink]="['/app/kasse', registerId(), ...tab.path]" routerLinkActive="border-accent-700 text-accent-800 font-semibold"
          [routerLinkActiveOptions]="{ exact: true }" #rla="routerLinkActive" [attr.aria-current]="rla.isActive ? 'page' : null"
          class="-mb-px border-b-2 border-transparent px-3 py-2 text-slate-700 hover:text-slate-900" [attr.data-testid]="'pos-tab-' + tab.id">
          {{ tab.label | t }}
        </a>
      }
    </nav>
  `,
})
export class PosSubnavComponent {
  readonly registerId = input.required<string>();
  protected readonly tabs = [
    { id: 'till', path: [] as string[], label: 'pos.subnav.till' },
    { id: 'receipts', path: ['belege'], label: 'pos.subnav.receipts' },
    { id: 'sessions', path: ['sitzungen'], label: 'pos.subnav.sessions' },
  ];
}
