import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { Observable, firstValueFrom } from 'rxjs';
import { AdminManagementApi } from '../../core/api/admin-management-api';
import { AdminApi } from '../../core/api/handynet-api';
import { AdminOrganization } from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { ToastService } from '../../core/ui-state/toast.service';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';
import { AdminTabsComponent } from './admin-tabs';
import {
  CAPABILITIES,
  CompanyFilters,
  EMPTY_COMPANY_FILTERS,
  ORG_STATUSES,
  companyQuery,
  validateDateRange,
} from './management/admin-management.logic';

type ReasonAction = 'reject' | 'suspend';

/**
 * Platform admin: companies (Unternehmen) — search and filters (status, module, business type, registration period), counts,
 * quick actions (verify, reject, suspend, modules) and the row link to the detail page. Account/contract data only.
 */
@Component({
  selector: 'hn-admin-page',
  imports: [FormsModule, RouterLink, AdminTabsComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './admin.page.html',
})
export class AdminPage {
  private readonly api = inject(AdminApi);
  private readonly management = inject(AdminManagementApi);
  private readonly auth = inject(AuthService);
  private readonly toast = inject(ToastService);
  private readonly i18n = inject(I18nService);
  private readonly router = inject(Router);

  protected readonly statuses = ORG_STATUSES;
  protected readonly capabilities = CAPABILITIES;
  /** Applied filters (the form below edits a copy). */
  protected readonly filters = signal<CompanyFilters>({ ...EMPTY_COMPANY_FILTERS });
  protected form: CompanyFilters = { ...EMPTY_COMPANY_FILTERS };
  protected readonly rangeError = signal<string | null>(null);
  protected readonly page = signal(1);
  protected readonly pageSize = 25;
  protected readonly hasFilters = computed(
    () => JSON.stringify(this.filters()) !== JSON.stringify(EMPTY_COMPANY_FILTERS),
  );

  protected readonly organizations = createQuery(
    () => companyQuery(this.filters(), this.page(), this.pageSize),
    (q) => this.management.companies(q),
  );
  protected readonly modules = createQuery(
    () => true,
    () => this.api.modules(),
  );

  protected readonly busyId = signal<string | null>(null);
  protected readonly actionError = signal<unknown>(null);

  protected readonly reasonFor = signal<{ org: AdminOrganization; action: ReasonAction } | null>(
    null,
  );
  protected reason = '';

  protected readonly modulesFor = signal<AdminOrganization | null>(null);
  protected selectedModules: Record<string, boolean> = {};

  protected applyFilters(): void {
    const error = validateDateRange(this.form.createdFrom, this.form.createdTo);
    this.rangeError.set(error);
    if (error) return;
    this.filters.set({ ...this.form, search: this.form.search.trim() });
    this.page.set(1);
  }

  protected setStatus(value: string): void {
    this.form.status = value;
    this.applyFilters();
  }

  protected resetFilters(): void {
    this.form = { ...EMPTY_COMPANY_FILTERS };
    this.rangeError.set(null);
    this.filters.set({ ...EMPTY_COMPANY_FILTERS });
    this.page.set(1);
  }

  /** Row click opens the detail page, except on the row's own buttons and links. */
  protected openRow(event: MouseEvent, org: AdminOrganization): void {
    if ((event.target as HTMLElement | null)?.closest('a, button, input, select, label')) return;
    void this.router.navigate(['/app/administration/unternehmen', org.id]);
  }

  /** Pending → verified (sends the "verified" email); suspended → reactivated (no email). */
  protected verify(org: AdminOrganization): Promise<boolean> {
    return org.status === 'Suspended'
      ? this.run(
          org,
          this.management.setStatus(org.id, 'reactivate', null),
          'adminMgmt.status.reactivated',
        )
      : this.run(org, this.api.verify(org.id), 'admin.verified');
  }

  protected openReason(org: AdminOrganization, action: ReasonAction): void {
    this.reason = '';
    this.actionError.set(null);
    this.reasonFor.set({ org, action });
  }

  protected async submitReason(): Promise<void> {
    const r = this.reasonFor();
    if (!r || !this.reason.trim()) return;
    const call =
      r.action === 'reject'
        ? this.api.reject(r.org.id, this.reason.trim())
        : this.api.suspend(r.org.id, this.reason.trim());
    if (await this.run(r.org, call, r.action === 'reject' ? 'admin.rejected' : 'admin.suspended'))
      this.reasonFor.set(null);
  }

  protected openModules(org: AdminOrganization): void {
    this.actionError.set(null);
    this.selectedModules = Object.fromEntries(
      (this.modules.value() ?? []).map((m) => [m, org.modules.includes(m)]),
    );
    this.modulesFor.set(org);
  }

  protected async saveModules(): Promise<void> {
    const org = this.modulesFor();
    if (!org) return;
    const modules = Object.entries(this.selectedModules)
      .filter(([, on]) => on)
      .map(([m]) => m);
    if (await this.run(org, this.api.setModules(org.id, modules), 'admin.modulesSaved'))
      this.modulesFor.set(null);
  }

  private async run(
    org: AdminOrganization,
    call: Observable<AdminOrganization>,
    successKey: string,
  ): Promise<boolean> {
    this.busyId.set(org.id);
    this.actionError.set(null);
    try {
      await firstValueFrom(call);
      this.toast.success(this.i18n.t(successKey, { name: org.name }));
      this.organizations.reload();
      // The admin may be a member of the organization too; refresh /api/me so navigation updates.
      void this.auth.loadMe(true);
      return true;
    } catch (e) {
      this.actionError.set(e);
      return false;
    } finally {
      this.busyId.set(null);
    }
  }
}
