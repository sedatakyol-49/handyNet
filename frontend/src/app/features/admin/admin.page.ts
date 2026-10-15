import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Observable, firstValueFrom } from 'rxjs';
import { AdminApi } from '../../core/api/handynet-api';
import { AdminOrganization } from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { ToastService } from '../../core/ui-state/toast.service';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';
import { AdminTabsComponent } from './admin-tabs';

type ReasonAction = 'reject' | 'suspend';

@Component({
  selector: 'hn-admin-page',
  imports: [FormsModule, AdminTabsComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './admin.page.html',
})
export class AdminPage {
  private readonly api = inject(AdminApi);
  private readonly auth = inject(AuthService);
  private readonly toast = inject(ToastService);
  private readonly i18n = inject(I18nService);

  protected readonly statuses = ['PendingVerification', 'Verified', 'Rejected', 'Suspended'];
  protected readonly status = signal('PendingVerification');
  protected readonly search = signal('');
  protected readonly page = signal(1);
  protected readonly pageSize = 25;

  protected readonly organizations = createQuery(
    () => ({ status: this.status(), search: this.search(), page: this.page(), pageSize: this.pageSize }),
    (q) => this.api.organizations(q),
  );
  protected readonly modules = createQuery(() => true, () => this.api.modules());

  protected readonly busyId = signal<string | null>(null);
  protected readonly actionError = signal<unknown>(null);

  protected readonly reasonFor = signal<{ org: AdminOrganization; action: ReasonAction } | null>(null);
  protected reason = '';

  protected readonly modulesFor = signal<AdminOrganization | null>(null);
  protected selectedModules: Record<string, boolean> = {};

  protected applySearch(value: string): void {
    this.search.set(value.trim());
    this.page.set(1);
  }

  protected setStatus(value: string): void {
    this.status.set(value);
    this.page.set(1);
  }

  protected verify(org: AdminOrganization): Promise<boolean> {
    return this.run(org, this.api.verify(org.id), 'admin.verified');
  }

  protected openReason(org: AdminOrganization, action: ReasonAction): void {
    this.reason = '';
    this.actionError.set(null);
    this.reasonFor.set({ org, action });
  }

  protected async submitReason(): Promise<void> {
    const r = this.reasonFor();
    if (!r || !this.reason.trim()) return;
    const call = r.action === 'reject' ? this.api.reject(r.org.id, this.reason.trim()) : this.api.suspend(r.org.id, this.reason.trim());
    if (await this.run(r.org, call, r.action === 'reject' ? 'admin.rejected' : 'admin.suspended')) this.reasonFor.set(null);
  }

  protected openModules(org: AdminOrganization): void {
    this.actionError.set(null);
    this.selectedModules = Object.fromEntries((this.modules.value() ?? []).map((m) => [m, org.modules.includes(m)]));
    this.modulesFor.set(org);
  }

  protected async saveModules(): Promise<void> {
    const org = this.modulesFor();
    if (!org) return;
    const modules = Object.entries(this.selectedModules)
      .filter(([, on]) => on)
      .map(([m]) => m);
    if (await this.run(org, this.api.setModules(org.id, modules), 'admin.modulesSaved')) this.modulesFor.set(null);
  }

  private async run(org: AdminOrganization, call: Observable<AdminOrganization>, successKey: string): Promise<boolean> {
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
