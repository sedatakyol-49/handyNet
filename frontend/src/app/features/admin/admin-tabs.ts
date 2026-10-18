import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { TranslatePipe } from '../../core/i18n/translate.pipe';

/** Sub-navigation of the platform administration (Unternehmen / Reklamationen / Marktplatz-Moderation / Ankaufspreise / Provisionsrechnungen). */
@Component({
  selector: 'hn-admin-tabs',
  imports: [RouterLink, RouterLinkActive, TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block mb-4' },
  template: `
    <nav class="flex gap-1 border-b border-slate-200" [attr.aria-label]="'admin.tabs.label' | t" data-testid="admin-tabs">
      @for (tab of tabs; track tab.path) {
        <a
          [routerLink]="tab.path"
          routerLinkActive="border-accent-700 text-slate-900 font-semibold"
          [routerLinkActiveOptions]="{ exact: tab.exact }"
          #rla="routerLinkActive"
          [attr.aria-current]="rla.isActive ? 'page' : null"
          class="-mb-px border-b-2 border-transparent px-3 py-2 text-sm text-slate-600 hover:text-slate-900 focus-visible:outline-2 focus-visible:outline-accent-600"
          >{{ tab.labelKey | t }}</a
        >
      }
    </nav>
  `,
})
export class AdminTabsComponent {
  protected readonly tabs = [
    { path: '/app/administration', labelKey: 'admin.tabs.organizations', exact: true },
    { path: '/app/administration/reklamationen', labelKey: 'admin.tabs.disputes', exact: false },
    { path: '/app/administration/marktplatz', labelKey: 'admin.tabs.moderation', exact: false },
    { path: '/app/administration/ankauf', labelKey: 'admin.tabs.ankauf', exact: false },
    { path: '/app/administration/rechnungen', labelKey: 'admin.tabs.invoices', exact: false },
  ];
}
