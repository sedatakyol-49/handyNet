import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AdminManagementApi } from '../../../core/api/admin-management-api';
import { AdminUser } from '../../../core/api/models';
import { createQuery } from '../../../shared/data/query';
import { UI } from '../../../shared/ui';
import { AdminTabsComponent } from '../admin-tabs';
import { EMPTY_USER_FILTERS, UserFilters, userBadges, userQuery } from './admin-management.logic';

/**
 * Platform admin: user accounts (Benutzer) — search by email or name, filters (verified, locked, disabled, platform admin,
 * without company), status badges and organizations; the row opens the account detail. No passwords or tokens.
 */
@Component({
  selector: 'hn-admin-users-page',
  imports: [FormsModule, RouterLink, AdminTabsComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-page-header [title]="'admin.title' | t" [description]="'adminMgmt.users.subtitle' | t" />
    <hn-admin-tabs />

    <hn-filter-bar>
    <form
      class="mb-3 grid gap-3 sm:grid-cols-2 lg:grid-cols-6"
      role="search"
      (submit)="$event.preventDefault(); apply()"
      data-testid="users-filters"
    >
      <hn-field [label]="'common.search' | t" for="users-search" class="sm:col-span-2">
        <input
          hnInput
          id="users-search"
          name="search"
          type="search"
          [(ngModel)]="form.search"
          [placeholder]="'adminMgmt.users.searchPlaceholder' | t"
          data-testid="users-search"
        />
      </hn-field>
      @for (f of triFilters; track f.key) {
        <hn-field [label]="f.labelKey | t" [for]="'users-f-' + f.key">
          <select
            hnSelect
            [id]="'users-f-' + f.key"
            [name]="f.key"
            [(ngModel)]="form[f.key]"
            (ngModelChange)="apply()"
            [attr.data-testid]="'users-filter-' + f.key"
          >
            <option value="">{{ 'common.all' | t }}</option>
            <option value="true">{{ f.yesKey | t }}</option>
            <option value="false">{{ f.noKey | t }}</option>
          </select>
        </hn-field>
      }
      <div class="flex flex-wrap items-end gap-2 sm:col-span-2 lg:col-span-6">
        <button hnButton variant="primary" type="submit" data-testid="users-apply">
          {{ 'adminMgmt.applyFilters' | t }}
        </button>
        @if (hasFilters()) {
          <button hnButton variant="ghost" type="button" (click)="reset()">
            {{ 'common.reset' | t }}
          </button>
        }
      </div>
    </form>
    </hn-filter-bar>

    <hn-banner class="mb-3" [error]="users.error()" />
    <!-- phone: cards; md+: table -->
    <ul class="grid gap-3 sm:grid-cols-2 md:hidden" data-testid="users-cards">
      @for (u of users.value()?.items ?? []; track u.id) {
        <li class="hn-card flex flex-col gap-1 p-4">
          <a class="hn-link font-medium" [routerLink]="['/app/administration/benutzer', u.id]" data-testid="users-card-open"><bdi>{{ u.displayName }}</bdi></a>
          <span class="truncate text-xs text-slate-500" dir="ltr"><bdi>{{ u.email }}</bdi></span>
          <div class="flex flex-wrap gap-1">
            @for (b of badges(u); track b.key) {
              <hn-status-badge [status]="b.key" kind="adminMgmt.userBadge" [tone]="b.tone" />
            }
          </div>
          <p class="text-xs text-slate-500">{{ 'adminMgmt.users.lastLogin' | t }}: {{ u.lastLoginAt ? (u.lastLoginAt | hnDate) : ('adminMgmt.users.never' | t) }}</p>
          @for (m of u.memberships; track m.organizationId) {
            <p class="text-xs"><bdi>{{ m.organizationName }}</bdi> <span class="text-slate-500">({{ m.role | tEnum: 'role' }})</span></p>
          }
        </li>
      } @empty {
        @if (!users.loading()) {
          <li class="sm:col-span-2"><div class="hn-card"><hn-empty-state [title]="'adminMgmt.users.empty' | t" [text]="hasFilters() ? ('adminMgmt.emptyFiltered' | t) : null" /></div></li>
        }
      }
    </ul>
    <div class="md:hidden">
      <hn-pagination [page]="page()" [pageSize]="pageSize" [totalCount]="users.value()?.totalCount ?? 0" (pageChange)="page.set($event)" />
    </div>
    <hn-table class="hidden md:block" [loading]="users.loading()">
      <table class="hn-table" data-testid="users-table">
        <thead>
          <tr>
            <th scope="col">{{ 'common.name' | t }}</th>
            <th scope="col">{{ 'adminMgmt.users.verified' | t }}</th>
            <th scope="col">{{ 'adminMgmt.users.lastLogin' | t }}</th>
            <th scope="col">{{ 'common.status' | t }}</th>
            <th scope="col">{{ 'adminMgmt.users.organizations' | t }}</th>
          </tr>
        </thead>
        <tbody>
          @for (u of users.value()?.items ?? []; track u.id) {
            <tr class="hn-row-link" [attr.data-user]="u.email" (click)="openRow($event, u)">
              <td>
                <a
                  class="hn-link font-medium"
                  [routerLink]="['/app/administration/benutzer', u.id]"
                  data-testid="users-open"
                  ><bdi>{{ u.displayName }}</bdi></a
                >
                <span class="block text-xs text-slate-500" dir="ltr"
                  ><bdi>{{ u.email }}</bdi></span
                >
              </td>
              <td class="text-sm">
                @if (u.emailVerified) {
                  <span class="text-emerald-800">{{ 'adminMgmt.yes' | t }}</span>
                } @else {
                  <span class="text-slate-500">{{ 'adminMgmt.no' | t }}</span>
                }
              </td>
              <td class="whitespace-nowrap text-xs">
                {{ u.lastLoginAt ? (u.lastLoginAt | hnDate) : ('adminMgmt.users.never' | t) }}
              </td>
              <td>
                <div class="flex flex-wrap gap-1" data-testid="users-badges">
                  @for (b of badges(u); track b.key) {
                    <hn-status-badge [status]="b.key" kind="adminMgmt.userBadge" [tone]="b.tone" />
                  }
                </div>
              </td>
              <td class="text-xs">
                @for (m of u.memberships; track m.organizationId) {
                  <span class="block"
                    ><bdi>{{ m.organizationName }}</bdi>
                    <span class="text-slate-500">({{ m.role | tEnum: 'role' }})</span></span
                  >
                } @empty {
                  <span class="text-slate-500">{{ 'adminMgmt.users.noOrganization' | t }}</span>
                }
              </td>
            </tr>
          } @empty {
            @if (users.loading() && !users.value()) {
              <tr class="hn-row-static">
                <td colspan="5"><hn-skeleton-rows /></td>
              </tr>
            } @else if (!users.loading()) {
              <tr>
                <td colspan="5">
                  <hn-empty-state
                    [title]="'adminMgmt.users.empty' | t"
                    [text]="hasFilters() ? ('adminMgmt.emptyFiltered' | t) : null"
                  />
                </td>
              </tr>
            }
          }
        </tbody>
      </table>
      <div footer class="border-t border-slate-200 px-2">
        <hn-pagination
          [page]="page()"
          [pageSize]="pageSize"
          [totalCount]="users.value()?.totalCount ?? 0"
          (pageChange)="page.set($event)"
        />
      </div>
    </hn-table>
  `,
})
export class AdminUsersPage {
  private readonly api = inject(AdminManagementApi);
  private readonly router = inject(Router);

  protected readonly triFilters: {
    key: Exclude<keyof UserFilters, 'search'>;
    labelKey: string;
    yesKey: string;
    noKey: string;
  }[] = [
    {
      key: 'emailVerified',
      labelKey: 'adminMgmt.users.verified',
      yesKey: 'adminMgmt.yes',
      noKey: 'adminMgmt.no',
    },
    {
      key: 'locked',
      labelKey: 'adminMgmt.users.locked',
      yesKey: 'adminMgmt.yes',
      noKey: 'adminMgmt.no',
    },
    {
      key: 'disabled',
      labelKey: 'adminMgmt.users.disabled',
      yesKey: 'adminMgmt.yes',
      noKey: 'adminMgmt.no',
    },
    {
      key: 'platformAdmin',
      labelKey: 'adminMgmt.users.platformAdmin',
      yesKey: 'adminMgmt.yes',
      noKey: 'adminMgmt.no',
    },
    {
      key: 'noOrganization',
      labelKey: 'adminMgmt.users.withoutOrganization',
      yesKey: 'adminMgmt.yes',
      noKey: 'adminMgmt.no',
    },
  ];

  protected form: UserFilters = { ...EMPTY_USER_FILTERS };
  protected readonly filters = signal<UserFilters>({ ...EMPTY_USER_FILTERS });
  protected readonly page = signal(1);
  protected readonly pageSize = 25;
  protected readonly hasFilters = computed(
    () => JSON.stringify(this.filters()) !== JSON.stringify(EMPTY_USER_FILTERS),
  );
  protected readonly users = createQuery(
    () => userQuery(this.filters(), this.page(), this.pageSize),
    (q) => this.api.userList(q),
  );
  protected readonly badges = userBadges;

  protected apply(): void {
    this.filters.set({ ...this.form, search: this.form.search.trim() });
    this.page.set(1);
  }

  protected reset(): void {
    this.form = { ...EMPTY_USER_FILTERS };
    this.apply();
  }

  protected openRow(event: MouseEvent, u: AdminUser): void {
    if ((event.target as HTMLElement | null)?.closest('a, button, input, select, label')) return;
    void this.router.navigate(['/app/administration/benutzer', u.id]);
  }
}
