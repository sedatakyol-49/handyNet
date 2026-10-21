import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink } from '@angular/router';
import { filter, map } from 'rxjs';
import { TranslatePipe } from '../../core/i18n/translate.pipe';

interface AdminTab {
  path: string;
  labelKey: string;
  /** Further URL prefixes that belong to the tab (detail pages). */
  prefixes: string[];
  exact: boolean;
}

/**
 * Sub-navigation of the platform administration (Unternehmen / Benutzer / Reklamationen / Marktplatz-Moderation /
 * Ankaufspreise / Provisionsrechnungen / Kassen & TSE). Detail pages keep their list tab highlighted.
 */
@Component({
  selector: 'hn-admin-tabs',
  imports: [RouterLink, TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block mb-4' },
  template: `
    <nav
      class="-mx-4 overflow-x-auto px-4 sm:mx-0 sm:px-0"
      [attr.aria-label]="'admin.tabs.label' | t"
      data-testid="admin-tabs"
    >
      <div class="flex min-w-max gap-1 border-b border-slate-200">
        @for (tab of tabs; track tab.path) {
          <a
            [routerLink]="tab.path"
            [attr.aria-current]="isActive(tab) ? 'page' : null"
            [class.border-accent-700]="isActive(tab)"
            [class.text-slate-900]="isActive(tab)"
            [class.font-semibold]="isActive(tab)"
            class="-mb-px rounded-t border-b-2 border-transparent px-3 py-2 text-sm whitespace-nowrap text-slate-600 hover:border-slate-300 hover:bg-slate-50 hover:text-slate-900 focus-visible:outline-2 focus-visible:outline-accent-600"
            >{{ tab.labelKey | t }}</a
          >
        }
      </div>
    </nav>
  `,
})
export class AdminTabsComponent {
  private readonly router = inject(Router);
  private readonly url = toSignal(
    this.router.events.pipe(
      filter((e) => e instanceof NavigationEnd),
      map(() => this.router.url),
    ),
    { initialValue: this.router.url },
  );

  protected readonly tabs: AdminTab[] = [
    {
      path: '/app/administration',
      labelKey: 'admin.tabs.organizations',
      prefixes: ['/app/administration/unternehmen'],
      exact: true,
    },
    {
      path: '/app/administration/benutzer',
      labelKey: 'admin.tabs.users',
      prefixes: [],
      exact: false,
    },
    {
      path: '/app/administration/reklamationen',
      labelKey: 'admin.tabs.disputes',
      prefixes: [],
      exact: false,
    },
    {
      path: '/app/administration/marktplatz',
      labelKey: 'admin.tabs.moderation',
      prefixes: [],
      exact: false,
    },
    {
      path: '/app/administration/ankauf',
      labelKey: 'admin.tabs.ankauf',
      prefixes: [],
      exact: false,
    },
    {
      path: '/app/administration/rechnungen',
      labelKey: 'admin.tabs.invoices',
      prefixes: [],
      exact: false,
    },
    {
      path: '/app/administration/kassen-tse',
      labelKey: 'admin.tabs.fiscal',
      prefixes: [],
      exact: false,
    },
  ];

  protected isActive(tab: AdminTab): boolean {
    const path = (this.url() ?? '').split(/[?#]/)[0];
    if (tab.exact ? path === tab.path : path === tab.path || path.startsWith(tab.path + '/'))
      return true;
    return tab.prefixes.some((p) => path === p || path.startsWith(p + '/'));
  }
}
