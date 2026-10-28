import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { TranslatePipe } from '../../../core/i18n/translate.pipe';

/** Second-level navigation inside Administration → Ankaufspreise (Preislisten / Preisregeln). */
@Component({
  selector: 'hn-ankauf-admin-nav',
  imports: [RouterLink, RouterLinkActive, TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block mb-4' },
  template: `
    <nav class="flex flex-wrap gap-1" [attr.aria-label]="'ankaufAdmin.nav.label' | t">
      @for (item of items; track item.path) {
        <a
          [routerLink]="item.path"
          routerLinkActive="bg-slate-200 text-slate-900 font-semibold"
          [routerLinkActiveOptions]="{ exact: item.exact }"
          #rla="routerLinkActive"
          [attr.aria-current]="rla.isActive ? 'page' : null"
          class="rounded-md px-3 py-2.5 text-xs text-slate-700 md:px-2.5 md:py-1 hover:bg-slate-100 focus-visible:outline-2 focus-visible:outline-accent-600"
          >{{ item.labelKey | t }}</a
        >
      }
    </nav>
  `,
})
export class AnkaufAdminNavComponent {
  protected readonly items = [
    { path: '/app/administration/ankauf', labelKey: 'ankaufAdmin.nav.lists', exact: true },
    { path: '/app/administration/ankauf/preisregeln', labelKey: 'ankaufAdmin.nav.policies', exact: true },
  ];
}
